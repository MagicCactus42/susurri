using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Kademlia.Storage;
using Susurri.Modules.DHT.Core.NatTraversal;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Shared.Abstractions.Diagnostics;
using Susurri.Shared.Abstractions.Logging;

namespace Susurri.Modules.DHT.Core.Kademlia;

public sealed class KademliaDhtNode : IAsyncDisposable
{
    private readonly ILogger<KademliaDhtNode> _logger;
    private readonly RoutingTable _routingTable;
    private readonly IDhtStorage _storage;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<KademliaMessage>> _pendingRequests = new();
    private readonly RateLimiter _rateLimiter = new(maxTokens: 50, refillRatePerSecond: 10.0);
    private readonly RateLimiter _pubkeyRateLimiter = new(maxTokens: 30, refillRatePerSecond: 1.0);
    private readonly MessageReplayCache _replayCache = new();
    private readonly NatTraversalService? _natTraversal;
    private readonly BackgroundTaskRunner _backgroundTasks;
    private readonly OfflineMessageService _offlineMessages;
    private readonly HolePunchCoordinator _holePunch;
    private bool _disposed;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private const int Alpha = 3;
    private const int K = 20;
    private const int MaxMessageSize = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StoreAuthTimestampWindow = TimeSpan.FromMinutes(5);

    public KademliaId LocalId { get; }
    public Key EncryptionKey { get; }
    public byte[] EncryptionPublicKey { get; }
    public Key? SigningKey { get; }
    public byte[] SigningPublicKey { get; }
    public IPEndPoint? LocalEndPoint { get; private set; }
    public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
    public int KnownNodes => _routingTable.TotalNodes;
    public RoutingTable RoutingTable => _routingTable;
    public NatTraversalService? NatTraversal => _natTraversal;

    public event Func<byte[], byte[], Task>? OnMessageReceived;

    public KademliaDhtNode(Key encryptionKey, ILogger<KademliaDhtNode> logger, Key? signingKey = null, NatTraversalService? natTraversal = null)
    {
        EncryptionKey = encryptionKey;
        EncryptionPublicKey = encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        SigningKey = signingKey;
        SigningPublicKey = signingKey?.PublicKey.Export(KeyBlobFormat.RawPublicKey) ?? Array.Empty<byte>();
        LocalId = KademliaId.FromPublicKey(EncryptionPublicKey);
        _logger = logger;
        _routingTable = new RoutingTable(LocalId, K);
        _storage = new DhtStorage();
        _natTraversal = natTraversal;
        _backgroundTasks = new BackgroundTaskRunner(logger);

        _offlineMessages = new OfflineMessageService(
            storage: _storage,
            pubkeyRateLimiter: _pubkeyRateLimiter,
            localId: LocalId,
            encryptionPublicKey: EncryptionPublicKey,
            signingKey: SigningKey,
            signingPublicKey: SigningPublicKey,
            sendRequest: SendRequestAsync,
            findClosestNodes: FindClosestNodesAsync,
            logger: _logger);

        _holePunch = new HolePunchCoordinator(
            routingTable: _routingTable,
            natTraversal: _natTraversal,
            localId: LocalId,
            encryptionPublicKey: EncryptionPublicKey,
            sendRequest: SendRequestAsync,
            backgroundTasks: _backgroundTasks,
            logger: _logger);
    }

    public async Task StartAsync(int port)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        LocalEndPoint = (IPEndPoint)_listener.LocalEndpoint;

        _logger.LogInformation("Kademlia DHT Node {NodeId} started on port {Port}",
            LocalId.ToString()[..16], port);

        _listenTask = ListenAsync(_cts.Token);

        if (_natTraversal != null)
        {
            var ct = _cts.Token;
            _backgroundTasks.Run(
                () => _natTraversal.InitializeAsync(ct),
                "NAT traversal initialization");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        if (_listenTask != null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        // Drain in-flight HandleClientAsync handlers before returning so callers
        // observe a fully-stopped node (no half-processed messages, no orphaned
        // writes). Bounded by ShutdownDrainTimeout so a misbehaving handler can't
        // block forever.
        var drained = await _backgroundTasks.DrainAsync(ShutdownDrainTimeout).ConfigureAwait(false);
        if (!drained)
            _logger.LogWarning("DHT Node stopped with in-flight handlers abandoned after {Timeout}",
                ShutdownDrainTimeout);

        _logger.LogInformation("Kademlia DHT Node stopped");
    }

    public async Task BootstrapAsync(IEnumerable<IPEndPoint> bootstrapNodes)
    {
        foreach (var endpoint in bootstrapNodes)
        {
            try
            {
                var pong = await PingAsync(endpoint).ConfigureAwait(false);
                if (pong != null)
                {
                    var node = new KademliaNode(pong.SenderId, pong.SenderPublicKey, endpoint);
                    _routingTable.TryAddNode(node);
                    _logger.LogInformation("Connected to bootstrap node {NodeId}", pong.SenderId.ToString()[..16]);

                    await FindNodeAsync(LocalId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to bootstrap node {Endpoint}", endpoint);
            }
        }
    }

    public async Task PublishPublicKeyAsync(string username)
    {
        var key = KademliaId.FromString(username);
        var value = CreatePublicKeyRecord();

        await StoreValueAsync(key, value).ConfigureAwait(false);
        _logger.LogInformation("Published public key for {Username}", username);
    }

    public async Task<UserPublicKeyRecord?> LookupPublicKeyAsync(string username)
    {
        var key = KademliaId.FromString(username);
        var value = await FindValueAsync(key).ConfigureAwait(false);

        if (value != null)
        {
            var record = UserPublicKeyRecord.Deserialize(value);

            if (record.SigningPublicKey.Length > 0 && record.Signature != null && record.Signature.Length > 0)
            {
                if (!record.VerifySignature())
                {
                    _logger.LogWarning("Public key record for {Username} has invalid signature, rejecting", username);
                    return null;
                }
                _logger.LogDebug("Public key record for {Username} signature verified", username);
            }

            return record;
        }

        return null;
    }

    public async Task StoreValueAsync(KademliaId key, byte[] value)
    {
        _storage.Store(key, value, TimeSpan.FromHours(24));

        var closestNodes = await FindClosestNodesAsync(key).ConfigureAwait(false);

        var storeTasks = closestNodes.Select(async node =>
        {
            try
            {
                await SendStoreAsync(node, key, value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store value on node {NodeId}", node.Id.ToString()[..16]);
            }
        });

        await Task.WhenAll(storeTasks).ConfigureAwait(false);
    }

    public async Task<byte[]?> FindValueAsync(KademliaId key)
    {
        var localValue = _storage.Get(key);
        if (localValue != null)
            return localValue;

        var queried = new HashSet<KademliaId> { LocalId };
        var shortlist = new List<KademliaNode>(_routingTable.FindClosestNodes(key, K));

        while (true)
        {
            var toQuery = shortlist
                .Where(n => !queried.Contains(n.Id))
                .Take(Alpha)
                .ToList();

            if (toQuery.Count == 0)
                break;

            var results = await Task.WhenAll(toQuery.Select(async node =>
            {
                queried.Add(node.Id);
                try
                {
                    return await SendFindValueAsync(node, key).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            })).ConfigureAwait(false);

            foreach (var result in results.Where(r => r != null))
            {
                if (result!.Found && result.Value != null)
                {
                    _storage.Store(key, result.Value, TimeSpan.FromHours(1));
                    return result.Value;
                }

                foreach (var nodeRecord in result.ClosestNodes)
                {
                    var node = nodeRecord.ToNode();
                    if (!queried.Contains(node.Id) && !shortlist.Any(n => n.Id == node.Id))
                    {
                        shortlist.Add(node);
                    }
                }
            }

            shortlist = shortlist.OrderBy(n => n.Id.DistanceTo(key).CompareTo(default)).Take(K).ToList();
        }

        return null;
    }

    public Task StoreOfflineMessageAsync(byte[] recipientPublicKey, byte[] encryptedMessage)
        => _offlineMessages.StoreOfflineAsync(recipientPublicKey, encryptedMessage);

    public Task<IReadOnlyList<byte[]>> GetOfflineMessagesAsync()
        => _offlineMessages.GetOfflineAsync();

    public IReadOnlyList<KademliaNode> GetRandomNodesForPath(int count)
    {
        return _routingTable.GetRandomNodes(count);
    }

    public Task<HolePunchResponseMessage?> SendHolePunchRequestAsync(
        KademliaNode intermediary,
        HolePunchRequestMessage request)
        => _holePunch.SendRequestAsync(intermediary, request);

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "<unknown>";
                _backgroundTasks.Run(
                    () => HandleClientAsync(client, ct),
                    $"DHT client {endpoint}");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remoteEndpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        using var activity = InboundActivity.Begin("dht.inbound", remoteEndpoint);
        try
        {
            if (!_rateLimiter.IsAllowed(remoteEndpoint))
            {
                _logger.LogWarning("Rate limited connection from {Endpoint}", remoteEndpoint);
                return;
            }

            client.ReceiveTimeout = 10_000;
            using var stream = client.GetStream();
            using var reader = new BinaryReader(stream);

            var length = reader.ReadInt32();
            if (length <= 0 || length > MaxMessageSize)
            {
                _logger.LogWarning("Rejected message with invalid size {Size} from {Endpoint}", length, remoteEndpoint);
                return;
            }
            var data = reader.ReadBytes(length);

            var message = KademliaMessage.Deserialize(data);

            SusurriMetrics.DhtMessagesIn.Add(1, new KeyValuePair<string, object?>("type", message.GetType().Name));

            if (!_replayCache.TryRecord(message.MessageId))
            {
                SusurriMetrics.ReplaysDropped.Add(1, new KeyValuePair<string, object?>("scope", "dht"));
                _logger.LogDebug("Dropped replayed message {MessageId} of type {Type} from {Endpoint}",
                    message.MessageId, message.Type, remoteEndpoint);
                return;
            }

            var senderEndpoint = remoteEndpoint;
            var senderNode = new KademliaNode(message.SenderId, message.SenderPublicKey, senderEndpoint);
            _routingTable.TryAddNode(senderNode);

            var response = await DispatchAsync(message, senderEndpoint).ConfigureAwait(false);

            if (response != null)
            {
                using var writer = new BinaryWriter(stream);
                var responseData = response.Serialize();
                writer.Write(responseData.Length);
                writer.Write(responseData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client");
        }
        finally
        {
            client.Close();
        }
    }

    private async Task<KademliaMessage?> DispatchAsync(KademliaMessage message, IPEndPoint sender)
    {
        return message switch
        {
            PingMessage ping => HandlePing(ping),
            FindNodeMessage findNode => HandleFindNode(findNode),
            FindValueMessage findValue => HandleFindValue(findValue),
            StoreMessage store => HandleStore(store),
            StoreOfflineMessageMessage storeOffline => _offlineMessages.HandleStore(storeOffline),
            GetOfflineMessagesMessage getOffline => _offlineMessages.HandleGet(getOffline),
            HolePunchRequestMessage holePunch => await _holePunch.HandleAsync(holePunch, sender).ConfigureAwait(false),
            HolePunchResponseMessage holePunchResp => HandleResponse(holePunchResp),
            PongMessage pong => HandleResponse(pong),
            FindNodeResponseMessage findNodeResp => HandleResponse(findNodeResp),
            FindValueResponseMessage findValueResp => HandleResponse(findValueResp),
            StoreResponseMessage storeResp => HandleResponse(storeResp),
            OfflineMessagesResponseMessage offlineResp => HandleResponse(offlineResp),
            OnionMessageWrapper onion => await HandleOnionMessage(onion).ConfigureAwait(false),
            _ => null
        };
    }

    private PongMessage HandlePing(PingMessage ping)
    {
        return new PongMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            InResponseTo = ping.MessageId
        };
    }

    private FindNodeResponseMessage HandleFindNode(FindNodeMessage findNode)
    {
        var closest = _routingTable.FindClosestNodes(findNode.TargetId, K);
        return new FindNodeResponseMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            InResponseTo = findNode.MessageId,
            Nodes = closest.Select(NodeRecord.FromNode).ToList()
        };
    }

    private FindValueResponseMessage HandleFindValue(FindValueMessage findValue)
    {
        var value = _storage.Get(findValue.Key);

        if (value != null)
        {
            return new FindValueResponseMessage
            {
                SenderId = LocalId,
                SenderPublicKey = EncryptionPublicKey,
                InResponseTo = findValue.MessageId,
                Found = true,
                Value = value
            };
        }

        var closest = _routingTable.FindClosestNodes(findValue.Key, K);
        return new FindValueResponseMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            InResponseTo = findValue.MessageId,
            Found = false,
            ClosestNodes = closest.Select(NodeRecord.FromNode).ToList()
        };
    }

    private StoreResponseMessage HandleStore(StoreMessage store)
    {
        if (!VerifyStoreAuth(store))
        {
            _logger.LogWarning("Rejected unauthenticated STORE for key {Key} from {Sender}",
                store.Key.ToString()[..16], store.SenderId.ToString()[..16]);

            return new StoreResponseMessage
            {
                SenderId = LocalId,
                SenderPublicKey = EncryptionPublicKey,
                InResponseTo = store.MessageId,
                Success = false,
                Error = "Authentication required"
            };
        }

        if (!_pubkeyRateLimiter.IsAllowed(Convert.ToHexString(store.SigningPublicKey)))
        {
            _logger.LogWarning("Rate limited STORE from signing key {KeyFingerprint}",
                LogRedaction.KeyFingerprint(store.SigningPublicKey));

            return new StoreResponseMessage
            {
                SenderId = LocalId,
                SenderPublicKey = EncryptionPublicKey,
                InResponseTo = store.MessageId,
                Success = false,
                Error = "Rate limited"
            };
        }

        try
        {
            var ttl = store.TtlSeconds > 0 ? TimeSpan.FromSeconds(store.TtlSeconds) : (TimeSpan?)null;
            _storage.Store(store.Key, store.Value, ttl);

            return new StoreResponseMessage
            {
                SenderId = LocalId,
                SenderPublicKey = EncryptionPublicKey,
                InResponseTo = store.MessageId,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new StoreResponseMessage
            {
                SenderId = LocalId,
                SenderPublicKey = EncryptionPublicKey,
                InResponseTo = store.MessageId,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private bool VerifyStoreAuth(StoreMessage store)
    {
        if (store.SigningPublicKey.Length == 0 || store.Signature.Length == 0)
            return false;

        if (!MessageReplayCache.IsTimestampFresh(store.Timestamp, StoreAuthTimestampWindow))
        {
            _logger.LogWarning("STORE has stale timestamp (Δ={Delta}s)",
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - store.Timestamp));
            return false;
        }

        try
        {
            var signingPubKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                store.SigningPublicKey,
                KeyBlobFormat.RawPublicKey);

            if (!SignatureAlgorithm.Ed25519.Verify(signingPubKey, store.GetSignableData(), store.Signature))
                return false;

            // For UserPublicKeyRecord values, ensure the record's signing key matches the sender.
            // Prevents republishing of stolen records under a different key.
            if (TryDeserializeUserPublicKeyRecord(store.Value, out var record))
            {
                if (!record!.VerifySignature())
                {
                    _logger.LogWarning("STORE rejected: published UserPublicKeyRecord has invalid signature");
                    return false;
                }
                if (!record.SigningPublicKey.AsSpan().SequenceEqual(store.SigningPublicKey))
                {
                    _logger.LogWarning("STORE rejected: record's signing key does not match sender");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "STORE signature verification failed");
            return false;
        }
    }

    private static bool TryDeserializeUserPublicKeyRecord(byte[] value, out UserPublicKeyRecord? record)
    {
        record = null;
        try
        {
            record = UserPublicKeyRecord.Deserialize(value);
            return record.SigningPublicKey.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private KademliaMessage? HandleResponse(KademliaMessage response)
    {
        var responseToId = response switch
        {
            PongMessage p => p.InResponseTo,
            FindNodeResponseMessage f => f.InResponseTo,
            FindValueResponseMessage f => f.InResponseTo,
            StoreResponseMessage s => s.InResponseTo,
            OfflineMessagesResponseMessage o => o.InResponseTo,
            HolePunchResponseMessage h => h.InResponseTo,
            _ => Guid.Empty
        };

        if (_pendingRequests.TryRemove(responseToId, out var tcs))
        {
            tcs.TrySetResult(response);
        }

        return null;
    }

    private async Task<KademliaMessage?> HandleOnionMessage(OnionMessageWrapper onion)
    {
        if (OnMessageReceived != null)
        {
            await OnMessageReceived(onion.SenderPublicKey, onion.EncryptedPayload).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<PongMessage?> PingAsync(IPEndPoint endpoint)
    {
        var ping = new PingMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey
        };

        return await SendRequestAsync<PongMessage>(endpoint, ping).ConfigureAwait(false);
    }

    private async Task<List<KademliaNode>> FindNodeAsync(KademliaId targetId)
    {
        var closestNodes = await FindClosestNodesAsync(targetId).ConfigureAwait(false);
        return closestNodes.ToList();
    }

    internal async Task<IReadOnlyList<KademliaNode>> FindClosestNodesAsync(KademliaId targetId)
    {
        var queried = new HashSet<KademliaId> { LocalId };
        var shortlist = new List<KademliaNode>(_routingTable.FindClosestNodes(targetId, K));
        var closestDistance = shortlist.Count > 0
            ? shortlist.Min(n => n.Id.DistanceTo(targetId))
            : LocalId.DistanceTo(targetId);

        while (true)
        {
            var toQuery = shortlist
                .Where(n => !queried.Contains(n.Id))
                .OrderBy(n => n.Id.DistanceTo(targetId).CompareTo(default))
                .Take(Alpha)
                .ToList();

            if (toQuery.Count == 0)
                break;

            var results = await Task.WhenAll(toQuery.Select(async node =>
            {
                queried.Add(node.Id);
                try
                {
                    return await SendFindNodeAsync(node, targetId).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            })).ConfigureAwait(false);

            bool improved = false;
            foreach (var result in results.Where(r => r != null))
            {
                foreach (var nodeRecord in result!.Nodes)
                {
                    var node = nodeRecord.ToNode();
                    if (!queried.Contains(node.Id) && !shortlist.Any(n => n.Id == node.Id))
                    {
                        shortlist.Add(node);
                        _routingTable.TryAddNode(node);

                        var distance = node.Id.DistanceTo(targetId);
                        if (distance < closestDistance)
                        {
                            closestDistance = distance;
                            improved = true;
                        }
                    }
                }
            }

            if (!improved && toQuery.All(n => queried.Contains(n.Id)))
                break;

            shortlist = shortlist.OrderBy(n => n.Id.DistanceTo(targetId).CompareTo(default)).Take(K).ToList();
        }

        return shortlist;
    }

    private async Task<FindNodeResponseMessage?> SendFindNodeAsync(KademliaNode node, KademliaId targetId)
    {
        var request = new FindNodeMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            TargetId = targetId
        };

        return await SendRequestAsync<FindNodeResponseMessage>(node.EndPoint, request).ConfigureAwait(false);
    }

    private async Task<FindValueResponseMessage?> SendFindValueAsync(KademliaNode node, KademliaId key)
    {
        var request = new FindValueMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            Key = key
        };

        return await SendRequestAsync<FindValueResponseMessage>(node.EndPoint, request).ConfigureAwait(false);
    }

    private async Task SendStoreAsync(KademliaNode node, KademliaId key, byte[] value)
    {
        var request = CreateSignedStoreMessage(key, value, 86400);
        await SendRequestAsync<StoreResponseMessage>(node.EndPoint, request).ConfigureAwait(false);
    }

    private StoreMessage CreateSignedStoreMessage(KademliaId key, byte[] value, uint ttlSeconds)
    {
        var draft = new StoreMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            Key = key,
            Value = value,
            TtlSeconds = ttlSeconds,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SigningPublicKey = SigningPublicKey
        };

        if (SigningKey == null)
            return draft;

        var signature = SignatureAlgorithm.Ed25519.Sign(SigningKey, draft.GetSignableData());

        return new StoreMessage
        {
            MessageId = draft.MessageId,
            SenderId = draft.SenderId,
            SenderPublicKey = draft.SenderPublicKey,
            Key = draft.Key,
            Value = draft.Value,
            TtlSeconds = draft.TtlSeconds,
            Timestamp = draft.Timestamp,
            SigningPublicKey = draft.SigningPublicKey,
            Signature = signature
        };
    }

    private async Task<TResponse?> SendRequestAsync<TResponse>(IPEndPoint endpoint, KademliaMessage request)
        where TResponse : KademliaMessage
    {
        var response = await SendRequestAsync(endpoint, request).ConfigureAwait(false);
        return response as TResponse;
    }

    /// <summary>
    /// Non-generic transport hook used by extracted services (OfflineMessageService,
    /// HolePunchCoordinator) so they don't need a generic delegate.
    /// </summary>
    internal async Task<KademliaMessage?> SendRequestAsync(IPEndPoint endpoint, KademliaMessage request)
    {
        var tcs = new TaskCompletionSource<KademliaMessage>();
        _pendingRequests[request.MessageId] = tcs;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);

            using var stream = client.GetStream();
            using var writer = new BinaryWriter(stream);

            var data = request.Serialize();
            writer.Write(data.Length);
            writer.Write(data);

            using var cts = new CancellationTokenSource(RequestTimeout);
            cts.Token.Register(() => tcs.TrySetCanceled());

            using var reader = new BinaryReader(stream);
            var length = reader.ReadInt32();
            var responseData = reader.ReadBytes(length);

            return KademliaMessage.Deserialize(responseData);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Request to {Endpoint} failed", endpoint);
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(request.MessageId, out _);
        }
    }

    private byte[] CreatePublicKeyRecord()
    {
        var record = new UserPublicKeyRecord
        {
            EncryptionPublicKey = EncryptionPublicKey,
            SigningPublicKey = SigningPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (SigningKey != null)
        {
            var dataToSign = record.GetSignableData();
            var signature = SignatureAlgorithm.Ed25519.Sign(SigningKey, dataToSign);
            record = record with { Signature = signature };
        }

        return record.Serialize();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        await _backgroundTasks.DisposeAsync().ConfigureAwait(false);

        if (_natTraversal != null)
            await _natTraversal.DisposeAsync().ConfigureAwait(false);

        EncryptionKey.Dispose();
        SigningKey?.Dispose();
    }
}
