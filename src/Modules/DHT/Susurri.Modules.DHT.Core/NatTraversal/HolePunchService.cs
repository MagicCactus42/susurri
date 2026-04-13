using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Susurri.Modules.DHT.Core.NatTraversal;

/// <summary>
/// Coordinates UDP hole punching between two NAT'd peers.
///
/// Flow:
/// 1. Initiator calls <see cref="PunchAsync"/> which sends probe packets to the target's
///    public UDP endpoint while simultaneously listening for incoming probes.
/// 2. When a probe is received from the peer, the hole is punched and the UDP "connection" is ready.
/// 3. Both sides must call PunchAsync concurrently (coordinated via the DHT signaling layer).
/// </summary>
public sealed class HolePunchService : IAsyncDisposable
{
    private readonly ILogger<HolePunchService> _logger;
    private readonly ConcurrentDictionary<Guid, HolePunchSession> _activeSessions = new();

    // Probe packet: 4-byte magic + 16-byte punch ID
    private static readonly byte[] ProbeMagic = { 0x53, 0x55, 0x48, 0x50 }; // "SUHP" - Susurri UDP Hole Punch
    private const int ProbePacketSize = 20; // 4 magic + 16 punch ID

    private static readonly TimeSpan PunchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(200);
    private const int MaxProbes = 50; // 10 seconds / 200ms

    public HolePunchService(ILogger<HolePunchService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts UDP hole punching to the given remote endpoint.
    /// Sends probes and listens for incoming probes concurrently.
    /// Returns the UdpClient with an established "connection" on success.
    /// </summary>
    /// <param name="punchId">Unique ID for this hole punch session, shared by both peers.</param>
    /// <param name="remoteEndpoint">The peer's public UDP endpoint (from STUN).</param>
    /// <param name="localPort">Local UDP port to bind (0 for auto-assign).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The UDP client with a punched hole, or null if hole punching failed.</returns>
    public async Task<HolePunchResult?> PunchAsync(
        Guid punchId,
        IPEndPoint remoteEndpoint,
        int localPort = 0,
        CancellationToken ct = default)
    {
        var client = new UdpClient(localPort);
        var session = new HolePunchSession
        {
            PunchId = punchId,
            RemoteEndpoint = remoteEndpoint,
            Client = client,
            Completion = new TaskCompletionSource<IPEndPoint>()
        };

        _activeSessions[punchId] = session;

        try
        {
            _logger.LogDebug("Starting hole punch {PunchId} to {Remote}", punchId, remoteEndpoint);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PunchTimeout);

            // Run probe sending and listening concurrently
            var listenTask = ListenForProbesAsync(session, cts.Token);
            var sendTask = SendProbesAsync(session, cts.Token);

            try
            {
                var confirmedEndpoint = await session.Completion.Task.WaitAsync(cts.Token).ConfigureAwait(false);

                _logger.LogInformation("Hole punch {PunchId} succeeded: connected to {Remote}",
                    punchId, confirmedEndpoint);

                return new HolePunchResult
                {
                    Client = client,
                    RemoteEndPoint = confirmedEndpoint,
                    LocalEndPoint = (IPEndPoint)client.Client.LocalEndPoint!
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Hole punch {PunchId} to {Remote} timed out", punchId, remoteEndpoint);
                client.Dispose();
                return null;
            }
            finally
            {
                cts.Cancel();
                try { await sendTask.ConfigureAwait(false); } catch { }
                try { await listenTask.ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            _activeSessions.TryRemove(punchId, out _);
        }
    }

    /// <summary>
    /// Checks if incoming UDP data is a hole punch probe and handles it.
    /// Call this from the UDP receive loop if you share a UdpClient.
    /// Returns true if the data was a probe (consumed), false if it should be handled normally.
    /// </summary>
    public bool TryHandleProbe(byte[] data, IPEndPoint sender)
    {
        if (data.Length != ProbePacketSize)
            return false;

        if (!data.AsSpan(0, 4).SequenceEqual(ProbeMagic))
            return false;

        var punchId = new Guid(data.AsSpan(4, 16));

        if (_activeSessions.TryGetValue(punchId, out var session))
        {
            session.Completion.TrySetResult(sender);
            return true;
        }

        return false;
    }

    private async Task SendProbesAsync(HolePunchSession session, CancellationToken ct)
    {
        var probe = BuildProbePacket(session.PunchId);

        for (int i = 0; i < MaxProbes && !ct.IsCancellationRequested; i++)
        {
            try
            {
                await session.Client.SendAsync(probe, probe.Length, session.RemoteEndpoint).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Probe send failed for {PunchId}", session.PunchId);
            }
            catch (ObjectDisposedException) { break; }

            try
            {
                await Task.Delay(ProbeInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ListenForProbesAsync(HolePunchSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await session.Client.ReceiveAsync(ct).ConfigureAwait(false);

                if (TryHandleProbe(result.Buffer, result.RemoteEndPoint))
                    return;
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private static byte[] BuildProbePacket(Guid punchId)
    {
        var packet = new byte[ProbePacketSize];
        ProbeMagic.CopyTo(packet, 0);
        punchId.ToByteArray().CopyTo(packet, 4);
        return packet;
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in _activeSessions.Values)
        {
            session.Completion.TrySetCanceled();
            session.Client.Dispose();
        }
        _activeSessions.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class HolePunchSession
    {
        public Guid PunchId { get; init; }
        public IPEndPoint RemoteEndpoint { get; init; } = null!;
        public UdpClient Client { get; init; } = null!;
        public TaskCompletionSource<IPEndPoint> Completion { get; init; } = null!;
    }
}

/// <summary>
/// Result of a successful UDP hole punch.
/// </summary>
public sealed class HolePunchResult : IDisposable
{
    /// <summary>
    /// The UDP client with a punched NAT hole.
    /// </summary>
    public UdpClient Client { get; init; } = null!;

    /// <summary>
    /// The confirmed remote endpoint.
    /// </summary>
    public IPEndPoint RemoteEndPoint { get; init; } = null!;

    /// <summary>
    /// The local endpoint used.
    /// </summary>
    public IPEndPoint LocalEndPoint { get; init; } = null!;

    public void Dispose() => Client.Dispose();
}
