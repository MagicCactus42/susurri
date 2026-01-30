using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.Kademlia.Storage;
using Susurri.Modules.DHT.Core.Network;

namespace Susurri.Modules.DHT.Core.Kademlia;

public sealed class KademliaDhtNode : IAsyncDisposable
{
    private readonly ILogger<KademliaDhtNode> _logger;
    private readonly RoutingTable _routingTable;
    private readonly IDhtStorage _storage;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<KademliaMessage>> _pendingRequests = new();
    private readonly RateLimiter _rateLimiter = new(maxTokens: 50, refillRatePerSecond: 10.0);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private const int Alpha = 3; // Kademlia parallelism factor for iterative lookups
    private const int K = 20;
    private const int MaxMessageSize = 256 * 1024; // 256 KB max message from network
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(60);

    public KademliaId LocalId { get; }
    public Key EncryptionKey { get; }
    public byte[] EncryptionPublicKey { get; }
    public Key? SigningKey { get; }
    public byte[] SigningPublicKey { get; }
    public IPEndPoint? LocalEndPoint { get; private set; }
    public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
    public int KnownNodes => _routingTable.TotalNodes;
    public RoutingTable RoutingTable => _routingTable;

    public event Func<byte[], byte[], Task>? OnMessageReceived;

    public KademliaDhtNode(Key encryptionKey, ILogger<KademliaDhtNode> logger, Key? signingKey = null)
    {
        EncryptionKey = encryptionKey;
        EncryptionPublicKey = encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        SigningKey = signingKey;
        SigningPublicKey = signingKey?.PublicKey.Export(KeyBlobFormat.RawPublicKey) ?? Array.Empty<byte>();
        LocalId = KademliaId.FromPublicKey(EncryptionPublicKey);
        _logger = logger;
        _routingTable = new RoutingTable(LocalId, K);
        _storage = new DhtStorage();
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
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        if (_listenTask != null)
        {
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("Kademlia DHT Node stopped");
    }

    public async Task BootstrapAsync(IEnumerable<IPEndPoint> bootstrapNodes)
    {
        foreach (var endpoint in bootstrapNodes)
        {
            try
            {
                var pong = await PingAsync(endpoint);
                if (pong != null)
                {
                    var node = new KademliaNode(pong.SenderId, pong.SenderPublicKey, endpoint);
                    _routingTable.TryAddNode(node);
                    _logger.LogInformation("Connected to bootstrap node {NodeId}", pong.SenderId.ToString()[..16]);

                    // Kademlia bootstrap: lookup our own ID to populate routing table
                    await FindNodeAsync(LocalId);
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

        await StoreValueAsync(key, value);
        _logger.LogInformation("Published public key for {Username}", username);
    }

    public async Task<UserPublicKeyRecord?> LookupPublicKeyAsync(string username)
    {
        var key = KademliaId.FromString(username);
        var value = await FindValueAsync(key);

        if (value != null)
        {
            var record = UserPublicKeyRecord.Deserialize(value);

            // Verify signature if present
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

        var closestNodes = await FindClosestNodesAsync(key);

        var storeTasks = closestNodes.Select(async node =>
        {
            try
            {
                await SendStoreAsync(node, key, value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store value on node {NodeId}", node.Id.ToString()[..16]);
            }
        });

        await Task.WhenAll(storeTasks);
    }

    public async Task<byte[]?> FindValueAsync(KademliaId key)
    {
        var localValue = _storage.Get(key);
        if (localValue != null)
            return localValue;

        // Kademlia iterative lookup
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
                    return await SendFindValueAsync(node, key);
                }
                catch
                {
                    return null;
                }
            }));

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

    public async Task StoreOfflineMessageAsync(byte[] recipientPublicKey, byte[] encryptedMessage)
    {
        var key = KademliaId.FromPublicKey(recipientPublicKey);
        _storage.StoreOfflineMessage(key, encryptedMessage);

        var closestNodes = await FindClosestNodesAsync(key);
        foreach (var node in closestNodes.Take(K / 2))
        {
            try
            {
                var request = new StoreOfflineMessageMessage
                {
                    SenderId = LocalId,
                    SenderPublicKey = EncryptionPublicKey,
                    RecipientPublicKey = recipientPublicKey,
                    EncryptedMessage = encryptedMessage
                };
                await SendRequestAsync<StoreResponseMessage>(node.EndPoint, request);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store offline message on node {NodeId}", node.Id.ToString()[..16]);
            }
        }
    }

    public async Task<IReadOnlyList<byte[]>> GetOfflineMessagesAsync()
    {
        var key = KademliaId.FromPublicKey(EncryptionPublicKey);
        var messages = new List<byte[]>();
        messages.AddRange(_storage.GetOfflineMessages(key));

        var closestNodes = await FindClosestNodesAsync(key);
        foreach (var node in closestNodes.Take(K / 2))
        {
            try
            {
                var request = new GetOfflineMessagesMessage
                {
                    SenderId = LocalId,
                    SenderPublicKey = EncryptionPublicKey,
                    RecipientPublicKey = EncryptionPublicKey
                };
                var response = await SendRequestAsync<OfflineMessagesResponseMessage>(node.EndPoint, request);
                if (response != null)
                {
                    messages.AddRange(response.Messages);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get offline messages from node {NodeId}", node.Id.ToString()[..16]);
            }
        }

        return messages;
    }

    public IReadOnlyList<KademliaNode> GetRandomNodesForPath(int count)
    {
        return _routingTable.GetRandomNodes(count);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
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
        try
        {
            var remoteEndpoint = (IPEndPoint)client.Client.RemoteEndPoint!;

            // Rate limit per source IP
            if (!_rateLimiter.IsAllowed(remoteEndpoint))
            {
                _logger.LogWarning("Rate limited connection from {Endpoint}", remoteEndpoint);
                return;
            }

            client.ReceiveTimeout = 10_000; // 10 second read timeout
            using var stream = client.GetStream();
            using var reader = new BinaryReader(stream);

            // Read message length with bounds validation
            var length = reader.ReadInt32();
            if (length <= 0 || length > MaxMessageSize)
            {
                _logger.LogWarning("Rejected message with invalid size {Size} from {Endpoint}", length, remoteEndpoint);
                return;
            }
            var data = reader.ReadBytes(length);

            var message = KademliaMessage.Deserialize(data);

            var senderEndpoint = remoteEndpoint;
            var senderNode = new KademliaNode(message.SenderId, message.SenderPublicKey, senderEndpoint);
            _routingTable.TryAddNode(senderNode);

            var response = await HandleMessageAsync(message, senderEndpoint);

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

    private async Task<KademliaMessage?> HandleMessageAsync(KademliaMessage message, IPEndPoint sender)
    {
        return message switch
        {
            PingMessage ping => HandlePing(ping),
            FindNodeMessage findNode => HandleFindNode(findNode),
            FindValueMessage findValue => HandleFindValue(findValue),
            StoreMessage store => HandleStore(store),
            StoreOfflineMessageMessage storeOffline => HandleStoreOfflineMessage(storeOffline),
            GetOfflineMessagesMessage getOffline => HandleGetOfflineMessages(getOffline),
            PongMessage pong => HandleResponse(pong),
            FindNodeResponseMessage findNodeResp => HandleResponse(findNodeResp),
            FindValueResponseMessage findValueResp => HandleResponse(findValueResp),
            StoreResponseMessage storeResp => HandleResponse(storeResp),
            OfflineMessagesResponseMessage offlineResp => HandleResponse(offlineResp),
            OnionMessageWrapper onion => await HandleOnionMessage(onion),
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

    private StoreResponseMessage HandleStoreOfflineMessage(StoreOfflineMessageMessage msg)
    {
        try
        {
            var key = KademliaId.FromPublicKey(msg.RecipientPublicKey);
            _storage.StoreOfflineMessage(key, msg.EncryptedMessage);

            return new StoreResponseMessage
            {
                SenderId = LocalId,
                SenderPublicKey = EncryptionPublicKey,
                InResponseTo = msg.MessageId,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new StoreResponseMessage
            {
                SenderId = LocalId,
                SenderPublicKey = EncryptionPublicKey,
                InResponseTo = msg.MessageId,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private OfflineMessagesResponseMessage HandleGetOfflineMessages(GetOfflineMessagesMessage msg)
    {
        var key = KademliaId.FromPublicKey(msg.RecipientPublicKey);
        var messages = _storage.GetOfflineMessages(key);

        return new OfflineMessagesResponseMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            InResponseTo = msg.MessageId,
            Messages = messages.ToList()
        };
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
            await OnMessageReceived(onion.SenderPublicKey, onion.EncryptedPayload);
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

        var response = await SendRequestAsync<PongMessage>(endpoint, ping);
        return response;
    }

    private async Task<List<KademliaNode>> FindNodeAsync(KademliaId targetId)
    {
        var closestNodes = await FindClosestNodesAsync(targetId);
        return closestNodes.ToList();
    }

    private async Task<IReadOnlyList<KademliaNode>> FindClosestNodesAsync(KademliaId targetId)
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
                    return await SendFindNodeAsync(node, targetId);
                }
                catch
                {
                    return null;
                }
            }));

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

        return await SendRequestAsync<FindNodeResponseMessage>(node.EndPoint, request);
    }

    private async Task<FindValueResponseMessage?> SendFindValueAsync(KademliaNode node, KademliaId key)
    {
        var request = new FindValueMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            Key = key
        };

        return await SendRequestAsync<FindValueResponseMessage>(node.EndPoint, request);
    }

    private async Task SendStoreAsync(KademliaNode node, KademliaId key, byte[] value)
    {
        var request = new StoreMessage
        {
            SenderId = LocalId,
            SenderPublicKey = EncryptionPublicKey,
            Key = key,
            Value = value,
            TtlSeconds = 86400 // 24 hours
        };

        await SendRequestAsync<StoreResponseMessage>(node.EndPoint, request);
    }

    private async Task<TResponse?> SendRequestAsync<TResponse>(IPEndPoint endpoint, KademliaMessage request)
        where TResponse : KademliaMessage
    {
        var tcs = new TaskCompletionSource<KademliaMessage>();
        _pendingRequests[request.MessageId] = tcs;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port);

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

            var response = KademliaMessage.Deserialize(responseData);

            return response as TResponse;
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

        // Sign the record if we have a signing key
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
        await StopAsync();
        EncryptionKey.Dispose();
        SigningKey?.Dispose();
    }
}

public sealed record UserPublicKeyRecord
{
    public byte[] EncryptionPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] SigningPublicKey { get; init; } = Array.Empty<byte>();
    public long Timestamp { get; init; }
    public byte[]? Signature { get; init; }

    /// <summary>
    /// Returns the data that should be signed (everything except the signature itself).
    /// </summary>
    public byte[] GetSignableData()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)EncryptionPublicKey.Length);
        writer.Write(EncryptionPublicKey);
        writer.Write((byte)SigningPublicKey.Length);
        writer.Write(SigningPublicKey);
        writer.Write(Timestamp);

        return ms.ToArray();
    }

    /// <summary>
    /// Verifies the Ed25519 signature on this record.
    /// </summary>
    public bool VerifySignature()
    {
        if (SigningPublicKey.Length == 0 || Signature == null || Signature.Length == 0)
            return false;

        try
        {
            var signingPubKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                SigningPublicKey,
                KeyBlobFormat.RawPublicKey);

            return SignatureAlgorithm.Ed25519.Verify(signingPubKey, GetSignableData(), Signature);
        }
        catch
        {
            return false;
        }
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)EncryptionPublicKey.Length);
        writer.Write(EncryptionPublicKey);
        writer.Write((byte)SigningPublicKey.Length);
        writer.Write(SigningPublicKey);
        writer.Write(Timestamp);

        if (Signature != null)
        {
            writer.Write(true);
            writer.Write((byte)Signature.Length);
            writer.Write(Signature);
        }
        else
        {
            writer.Write(false);
        }

        return ms.ToArray();
    }

    public static UserPublicKeyRecord Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var encKeyLen = reader.ReadByte();
        var encryptionPublicKey = reader.ReadBytes(encKeyLen);

        var signKeyLen = reader.ReadByte();
        var signingPublicKey = reader.ReadBytes(signKeyLen);

        var timestamp = reader.ReadInt64();

        byte[]? signature = null;
        if (reader.ReadBoolean())
        {
            var sigLen = reader.ReadByte();
            signature = reader.ReadBytes(sigLen);
        }

        return new UserPublicKeyRecord
        {
            EncryptionPublicKey = encryptionPublicKey,
            SigningPublicKey = signingPublicKey,
            Timestamp = timestamp,
            Signature = signature
        };
    }
}
