using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Network;

public sealed class UdpEndpoint : IAsyncDisposable
{
    private const int MaxFragmentPayload = 1024;
    private static readonly byte[] ReliableMagic = { 0x53, 0x55, 0x52, 0x4D }; // "SURM"
    private static readonly byte[] ProbeMagic = { 0x53, 0x55, 0x48, 0x50 };    // "SUHP"
    private static readonly byte[] RelayRegisterMagic = { 0x53, 0x55, 0x52, 0x47 }; // "SURG"
    private static readonly byte[] RelayForwardMagic = { 0x53, 0x55, 0x52, 0x46 };  // "SURF"
    private static readonly byte[] RelayDeliverMagic = { 0x53, 0x55, 0x52, 0x44 };  // "SURD"
    private const int NodeIdSize = 32;
    private const int MaxRegistrations = 4096;
    private static readonly TimeSpan RegistrationTtl = TimeSpan.FromSeconds(90);
    private const byte FrameData = 0x01;
    private const byte FrameAck = 0x02;
    private const int ProbePacketSize = 20;
    private const uint StunMagicCookie = 0x2112A442;

    private static readonly TimeSpan RetransmitInterval = TimeSpan.FromMilliseconds(300);
    private const int MaxRetransmits = 6;
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReassemblyTimeout = TimeSpan.FromSeconds(15);
    private const int MaxReassembliesPerSender = 64;

    private readonly ILogger _logger;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _sweepLoop;
    private bool _disposed;

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _outbound = new();
    private readonly ConcurrentDictionary<ReassemblyKey, Inbound> _inbound = new();
    private readonly ConcurrentDictionary<ReassemblyKey, DateTimeOffset> _completed = new();
    private readonly ConcurrentDictionary<IPEndPoint, int> _inboundPerSender = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IPEndPoint>> _punchSessions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> _stunPending = new();

    // Relay-server role: registered peers (nodeId -> their public endpoint).
    private readonly ConcurrentDictionary<string, (IPEndPoint Endpoint, DateTimeOffset Expiry)> _registrations = new();
    private readonly RateLimiter _relayLimiter = new(maxTokens: 200, refillRatePerSecond: 100.0);
    // Relay-client role: a synthetic endpoint per relayed peer, so the reliable
    // layer treats each as a distinct peer; the route says how to reach it.
    private readonly ConcurrentDictionary<string, IPEndPoint> _relayedByNode = new();
    private readonly ConcurrentDictionary<IPEndPoint, (IPEndPoint Relay, byte[] TargetNodeId)> _relayRoutes = new();
    private int _syntheticCounter;

    public IPEndPoint? LocalEndPoint { get; private set; }
    public int LocalPort { get; private set; }
    public byte[]? LocalNodeId { get; set; }
    public bool IsRunning => _client != null && _cts is { IsCancellationRequested: false };

    public event Func<IPEndPoint, byte[], Task>? OnMessage;

    public UdpEndpoint(ILogger logger)
    {
        _logger = logger;
    }

    public void Start(int port)
    {
        _cts = new CancellationTokenSource();
        _client = new UdpClient(port);
        // Large buffers so a burst of fragments for one message (up to ~64
        // datagrams) isn't dropped while the receive loop is busy dispatching a
        // previous one.
        try
        {
            _client.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            _client.Client.SendBufferSize = 4 * 1024 * 1024;
        }
        catch (SocketException) { }
        LocalEndPoint = (IPEndPoint)_client.Client.LocalEndPoint!;
        LocalPort = LocalEndPoint.Port;
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        _sweepLoop = SweepLoopAsync(_cts.Token);
        _logger.LogInformation("UDP endpoint started on port {Port}", LocalPort);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _client?.Close();
        foreach (var tcs in _outbound.Values) tcs.TrySetResult(false);
        foreach (var tcs in _punchSessions.Values) tcs.TrySetCanceled();
        foreach (var tcs in _stunPending.Values) tcs.TrySetCanceled();

        if (_receiveLoop != null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (_sweepLoop != null)
        {
            try { await _sweepLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public async Task<bool> SendReliableAsync(IPEndPoint destination, byte[] payload, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("UDP endpoint not started");
        if (payload.Length > SecurityLimits.MaxMessageSize)
            throw new ArgumentException($"Payload exceeds {SecurityLimits.MaxMessageSize} bytes", nameof(payload));

        var messageId = Guid.NewGuid();
        var frames = BuildDataFrames(messageId, payload);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outbound[messageId] = tcs;

        try
        {
            for (int attempt = 0; attempt <= MaxRetransmits; attempt++)
            {
                foreach (var frame in frames)
                {
                    try
                    {
                        await SendDatagramAsync(destination, frame).ConfigureAwait(false);
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { return false; }
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(RetransmitInterval);
                try
                {
                    return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // retransmit
                }
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _outbound.TryRemove(messageId, out _);
        }
    }

    public async Task<bool> HolePunchAsync(Guid punchId, IPEndPoint remote, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("UDP endpoint not started");

        var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _punchSessions[punchId] = tcs;
        var probe = BuildProbe(punchId);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(10));

            // Send probes until we receive one from the peer. Once we do, keep
            // sending a few more so a peer that started listening later than us
            // still receives probes — otherwise the side that confirms first
            // stops early and the other side never converges.
            for (int i = 0; i < 50 && !linked.IsCancellationRequested; i++)
            {
                try { await SendDatagramAsync(remote, probe).ConfigureAwait(false); } catch { }

                if (tcs.Task.IsCompleted)
                {
                    for (int j = 0; j < 5; j++)
                    {
                        try { await SendDatagramAsync(remote, probe).ConfigureAwait(false); } catch { }
                        try { await Task.Delay(TimeSpan.FromMilliseconds(60), linked.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                    return true;
                }

                try { await Task.Delay(TimeSpan.FromMilliseconds(200), linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            return tcs.Task.IsCompletedSuccessfully;
        }
        finally
        {
            _punchSessions.TryRemove(punchId, out _);
        }
    }

    public async Task<IPEndPoint?> DiscoverPublicEndpointAsync(IReadOnlyList<DnsEndPoint> stunServers, CancellationToken ct = default)
    {
        if (_client == null)
            throw new InvalidOperationException("UDP endpoint not started");

        foreach (var server in stunServers)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(server.Host, ct).ConfigureAwait(false);
                var serverEp = addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => new IPEndPoint(a, server.Port))
                    .FirstOrDefault();
                if (serverEp == null)
                    continue;

                var txId = new byte[12];
                System.Security.Cryptography.RandomNumberGenerator.Fill(txId);
                var key = Convert.ToHexString(txId);
                var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stunPending[key] = tcs;

                try
                {
                    var request = BuildStunBindingRequest(txId);
                    await SendDatagramAsync(serverEp, request).ConfigureAwait(false);

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(TimeSpan.FromSeconds(3));
                    var mapped = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                    _logger.LogInformation("STUN mapped endpoint {Endpoint} via {Server}", mapped, server.Host);
                    return mapped;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // try next server
                }
                finally
                {
                    _stunPending.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "STUN query to {Server} failed", server.Host);
            }
        }

        return null;
    }

    private async Task SendDatagramAsync(IPEndPoint destination, byte[] datagram)
    {
        if (_client == null) return;

        // A synthetic destination means the peer is only reachable through a
        // relay: wrap the datagram so the relay forwards it to that peer.
        if (_relayRoutes.TryGetValue(destination, out var route) && LocalNodeId != null)
        {
            var wrapped = BuildRelayForward(route.TargetNodeId, LocalNodeId, datagram);
            await _client.SendAsync(wrapped, wrapped.Length, route.Relay).ConfigureAwait(false);
            return;
        }

        await _client.SendAsync(datagram, datagram.Length, destination).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers this node with a relay (and keeps the mapping alive) so peers
    /// that can't reach it directly — e.g. both sides behind symmetric NAT — can
    /// deliver to it through the relay. Runs until cancelled.
    /// </summary>
    public async Task RegisterWithRelayAsync(IPEndPoint relay, byte[] nodeId, CancellationToken ct = default)
    {
        var packet = new byte[4 + NodeIdSize];
        RelayRegisterMagic.CopyTo(packet, 0);
        nodeId.AsSpan(0, NodeIdSize).CopyTo(packet.AsSpan(4));

        while (!ct.IsCancellationRequested)
        {
            try { await SendDatagramAsync(relay, packet).ConfigureAwait(false); }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Sends a reliable payload to a peer via a relay, addressed by node id. The
    /// relay forwards it and forwards the peer's acks/replies back, so the normal
    /// reliability and request/response flow works over the relayed path.
    /// </summary>
    public Task<bool> SendReliableViaRelayAsync(IPEndPoint relay, byte[] targetNodeId, byte[] payload, CancellationToken ct = default)
    {
        var synthetic = SyntheticFor(targetNodeId, relay);
        return SendReliableAsync(synthetic, payload, ct);
    }

    private IPEndPoint SyntheticFor(byte[] nodeId, IPEndPoint relay)
    {
        var key = Convert.ToHexString(nodeId.AsSpan(0, NodeIdSize));
        var synthetic = _relayedByNode.GetOrAdd(key, _ =>
        {
            var n = System.Threading.Interlocked.Increment(ref _syntheticCounter);
            // 240.0.0.0/4 is reserved and never routed — a stable, collision-free
            // dictionary key that is never actually sent to.
            var addr = new IPAddress(new byte[] { 240, (byte)(n >> 16), (byte)(n >> 8), (byte)n });
            return new IPEndPoint(addr, 1);
        });
        _relayRoutes[synthetic] = (relay, nodeId.AsSpan(0, NodeIdSize).ToArray());
        return synthetic;
    }

    private static byte[] BuildRelayForward(byte[] targetNodeId, byte[] originNodeId, byte[] inner)
    {
        var packet = new byte[4 + NodeIdSize + NodeIdSize + inner.Length];
        RelayForwardMagic.CopyTo(packet, 0);
        targetNodeId.AsSpan(0, NodeIdSize).CopyTo(packet.AsSpan(4));
        originNodeId.AsSpan(0, NodeIdSize).CopyTo(packet.AsSpan(4 + NodeIdSize));
        inner.CopyTo(packet.AsSpan(4 + 2 * NodeIdSize));
        return packet;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            try
            {
                await DispatchDatagramAsync(result.RemoteEndPoint, result.Buffer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error handling UDP datagram from {Sender}", result.RemoteEndPoint);
            }
        }
    }

    private async Task DispatchDatagramAsync(IPEndPoint sender, byte[] data)
    {
        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(ProbeMagic))
        {
            HandleProbe(sender, data);
            return;
        }

        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(ReliableMagic))
        {
            await HandleReliableAsync(sender, data).ConfigureAwait(false);
            return;
        }

        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(RelayRegisterMagic))
        {
            if (_relayLimiter.IsAllowed(sender))
                HandleRelayRegister(sender, data);
            return;
        }

        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(RelayForwardMagic))
        {
            if (_relayLimiter.IsAllowed(sender))
                await HandleRelayForwardAsync(data).ConfigureAwait(false);
            return;
        }

        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(RelayDeliverMagic))
        {
            if (_relayLimiter.IsAllowed(sender))
                await HandleRelayDeliverAsync(sender, data).ConfigureAwait(false);
            return;
        }

        if (data.Length >= 20 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)) == StunMagicCookie)
        {
            HandleStunResponse(data);
            return;
        }
    }

    private void HandleRelayRegister(IPEndPoint sender, byte[] data)
    {
        if (data.Length != 4 + NodeIdSize)
            return;
        var nodeIdHex = Convert.ToHexString(data.AsSpan(4, NodeIdSize));
        var now = DateTimeOffset.UtcNow;

        if (_registrations.TryGetValue(nodeIdHex, out var existing)
            && existing.Expiry > now
            && !existing.Endpoint.Equals(sender))
            return;

        if (!_registrations.ContainsKey(nodeIdHex) && _registrations.Count >= MaxRegistrations
            && !EvictOldestRegistration(now))
            return;

        _registrations[nodeIdHex] = (sender, now.Add(RegistrationTtl));
    }

    private bool EvictOldestRegistration(DateTimeOffset now)
    {
        var oldest = default(string);
        var oldestExpiry = DateTimeOffset.MaxValue;
        foreach (var kvp in _registrations)
        {
            if (kvp.Value.Expiry <= now)
            {
                _registrations.TryRemove(kvp.Key, out _);
                return true;
            }
            if (kvp.Value.Expiry < oldestExpiry)
            {
                oldestExpiry = kvp.Value.Expiry;
                oldest = kvp.Key;
            }
        }

        return oldest != null && _registrations.TryRemove(oldest, out _);
    }

    private async Task HandleRelayForwardAsync(byte[] data)
    {
        if (data.Length < 4 + 2 * NodeIdSize)
            return;
        var targetHex = Convert.ToHexString(data.AsSpan(4, NodeIdSize));
        var origin = data.AsSpan(4 + NodeIdSize, NodeIdSize).ToArray();
        var inner = data.AsSpan(4 + 2 * NodeIdSize).ToArray();

        if (!_registrations.TryGetValue(targetHex, out var reg) || reg.Expiry < DateTimeOffset.UtcNow)
            return;

        var deliver = new byte[4 + NodeIdSize + inner.Length];
        RelayDeliverMagic.CopyTo(deliver, 0);
        origin.CopyTo(deliver.AsSpan(4));
        inner.CopyTo(deliver.AsSpan(4 + NodeIdSize));
        await SendDatagramAsync(reg.Endpoint, deliver).ConfigureAwait(false);
    }

    private async Task HandleRelayDeliverAsync(IPEndPoint relay, byte[] data)
    {
        if (data.Length < 4 + NodeIdSize)
            return;
        var origin = data.AsSpan(4, NodeIdSize).ToArray();
        var inner = data.AsSpan(4 + NodeIdSize).ToArray();

        // Attribute the inner datagram to a synthetic endpoint for this origin so
        // reassembly and any ack/reply route back through the same relay.
        var synthetic = SyntheticFor(origin, relay);
        await DispatchDatagramAsync(synthetic, inner).ConfigureAwait(false);
    }

    private void HandleProbe(IPEndPoint sender, byte[] data)
    {
        if (data.Length != ProbePacketSize)
            return;
        var punchId = new Guid(data.AsSpan(4, 16));
        if (_punchSessions.TryGetValue(punchId, out var tcs))
        {
            tcs.TrySetResult(sender);
            // Echo a probe back so a peer that started later still receives one
            // even after we've stopped our own send loop.
            _ = SendDatagramAsync(sender, BuildProbe(punchId));
        }
    }

    private async Task HandleReliableAsync(IPEndPoint sender, byte[] data)
    {
        if (data.Length < 5)
            return;
        var type = data[4];

        if (type == FrameAck)
        {
            if (data.Length < 21)
                return;
            var ackId = new Guid(data.AsSpan(5, 16));
            if (_outbound.TryGetValue(ackId, out var tcs))
                tcs.TrySetResult(true);
            return;
        }

        if (type != FrameData || data.Length < 27)
            return;

        var messageId = new Guid(data.AsSpan(5, 16));
        var total = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(21, 2));
        var index = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(23, 2));
        var fragLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(25, 2));

        if (total == 0 || index >= total || fragLen > MaxFragmentPayload || 27 + fragLen > data.Length)
            return;
        if (total * (long)MaxFragmentPayload > SecurityLimits.MaxMessageSize + total)
            return;

        var key = new ReassemblyKey(sender, messageId);

        if (_completed.ContainsKey(key))
        {
            await SendDatagramAsync(sender, BuildAckFrame(messageId)).ConfigureAwait(false);
            return;
        }

        if (!_inbound.TryGetValue(key, out var inbound))
        {
            if (CountInboundForSender(sender) >= MaxReassembliesPerSender)
                return;
            var created = new Inbound(total, DateTimeOffset.UtcNow);
            inbound = _inbound.GetOrAdd(key, created);
            if (ReferenceEquals(inbound, created))
                _inboundPerSender.AddOrUpdate(sender, 1, static (_, c) => c + 1);
        }
        if (inbound.Total != total)
            return;

        lock (inbound.Gate)
        {
            if (inbound.Fragments[index] == null)
            {
                inbound.Fragments[index] = data.AsSpan(27, fragLen).ToArray();
                inbound.Remaining--;
            }
        }

        if (inbound.Remaining > 0)
            return;

        if (!_inbound.TryRemove(key, out _))
            return;

        DecrementSender(sender);
        _completed[key] = DateTimeOffset.UtcNow;
        await SendDatagramAsync(sender, BuildAckFrame(messageId)).ConfigureAwait(false);

        var assembled = Reassemble(inbound);
        var handler = OnMessage;
        if (handler != null)
        {
            // Dispatch off the receive loop so a slow handler (deserialize +
            // protocol work) doesn't stall datagram draining and cause the
            // peer's next fragment burst to overflow the socket buffer.
            _ = Task.Run(async () =>
            {
                try { await handler(sender, assembled).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "UDP message handler failed"); }
            });
        }
    }

    private int CountInboundForSender(IPEndPoint sender)
        => _inboundPerSender.TryGetValue(sender, out var count) ? count : 0;

    private void DecrementSender(IPEndPoint sender)
        => _inboundPerSender.AddOrUpdate(sender, 0, static (_, c) => c > 0 ? c - 1 : 0);

    private void HandleStunResponse(byte[] data)
    {
        var messageType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
        if (messageType != 0x0101) // Binding Success Response
            return;

        var txId = Convert.ToHexString(data.AsSpan(8, 12));
        if (!_stunPending.TryGetValue(txId, out var tcs))
            return;

        var mapped = ParseMappedAddress(data);
        if (mapped != null)
            tcs.TrySetResult(mapped);
    }

    private static IPEndPoint? ParseMappedAddress(byte[] data)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        int offset = 20;
        int end = 20 + length;
        if (end > data.Length)
            return null;

        while (offset + 4 <= end)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var attrLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));
            var valueOffset = offset + 4;
            if (valueOffset + attrLen > data.Length)
                break;

            if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8 && data[valueOffset + 1] == 0x01)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(valueOffset + 2, 2));
                var addrBytes = data.AsSpan(valueOffset + 4, 4).ToArray();
                if (attrType == 0x0020)
                {
                    port ^= (ushort)(StunMagicCookie >> 16);
                    var cookie = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(cookie, StunMagicCookie);
                    for (int i = 0; i < 4; i++)
                        addrBytes[i] ^= cookie[i];
                }
                return new IPEndPoint(new IPAddress(addrBytes), port);
            }

            offset = valueOffset + attrLen + ((4 - (attrLen % 4)) % 4);
        }

        return null;
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in _completed)
                if (now - kvp.Value > CompletedRetention)
                    _completed.TryRemove(kvp.Key, out _);
            foreach (var kvp in _inbound)
                if (now - kvp.Value.StartedAt > ReassemblyTimeout && _inbound.TryRemove(kvp.Key, out _))
                    DecrementSender(kvp.Key.Sender);
            foreach (var kvp in _inboundPerSender)
                if (kvp.Value == 0)
                    ((ICollection<KeyValuePair<IPEndPoint, int>>)_inboundPerSender).Remove(kvp);
            foreach (var kvp in _registrations)
                if (kvp.Value.Expiry < now)
                    _registrations.TryRemove(kvp.Key, out _);
        }
    }

    private static byte[][] BuildDataFrames(Guid messageId, byte[] payload)
    {
        int count = payload.Length == 0
            ? 1
            : (payload.Length + MaxFragmentPayload - 1) / MaxFragmentPayload;

        var frames = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int offset = i * MaxFragmentPayload;
            int len = Math.Min(MaxFragmentPayload, payload.Length - offset);
            if (len < 0) len = 0;

            var frame = new byte[27 + len];
            ReliableMagic.CopyTo(frame, 0);
            frame[4] = FrameData;
            messageId.TryWriteBytes(frame.AsSpan(5, 16));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(21, 2), (ushort)count);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(23, 2), (ushort)i);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(25, 2), (ushort)len);
            if (len > 0)
                payload.AsSpan(offset, len).CopyTo(frame.AsSpan(27));
            frames[i] = frame;
        }
        return frames;
    }

    private static byte[] Reassemble(Inbound inbound)
    {
        var total = inbound.Fragments.Sum(f => f!.Length);
        var result = new byte[total];
        int pos = 0;
        foreach (var frag in inbound.Fragments)
        {
            Buffer.BlockCopy(frag!, 0, result, pos, frag!.Length);
            pos += frag.Length;
        }
        return result;
    }

    private static byte[] BuildAckFrame(Guid messageId)
    {
        var frame = new byte[21];
        ReliableMagic.CopyTo(frame, 0);
        frame[4] = FrameAck;
        messageId.TryWriteBytes(frame.AsSpan(5, 16));
        return frame;
    }

    private static byte[] BuildProbe(Guid punchId)
    {
        var packet = new byte[ProbePacketSize];
        ProbeMagic.CopyTo(packet, 0);
        punchId.ToByteArray().CopyTo(packet, 4);
        return packet;
    }

    private static byte[] BuildStunBindingRequest(byte[] txId)
    {
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0001); // Binding Request
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0);      // length
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), StunMagicCookie);
        txId.CopyTo(packet, 8);
        return packet;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _client?.Dispose();
    }

    private readonly record struct ReassemblyKey(IPEndPoint Sender, Guid MessageId);

    private sealed class Inbound
    {
        public Inbound(int total, DateTimeOffset startedAt)
        {
            Total = total;
            Fragments = new byte[total][];
            Remaining = total;
            StartedAt = startedAt;
        }

        public int Total { get; }
        public byte[]?[] Fragments { get; }
        public int Remaining;
        public DateTimeOffset StartedAt { get; }
        public object Gate { get; } = new();
    }
}
