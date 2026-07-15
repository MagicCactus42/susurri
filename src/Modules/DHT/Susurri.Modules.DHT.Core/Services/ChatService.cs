using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Onion.GroupChat;
using Susurri.Modules.DHT.Core.Onion.Ratchet;

namespace Susurri.Modules.DHT.Core.Services;

public sealed class ChatService : IAsyncDisposable
{
    private readonly KademliaDhtNode _dhtNode;
    private readonly OnionRouter _router;
    private readonly ConnectionManager _connectionManager;
    private readonly RelayService _relayService;
    private readonly ILogger<ChatService> _logger;
    private readonly Key _encryptionKey;
    private readonly Key? _signingKey;

    private readonly ConcurrentDictionary<string, UserPublicKeyRecord> _keyCache = new();

    private readonly ConcurrentDictionary<Guid, PendingMessage> _pendingMessages = new();

    private readonly ConcurrentDictionary<Guid, ReceivedMessage> _receivedMessages = new();

    private readonly GroupManager _groupManager;
    private readonly RatchetSessionManager _ratchet;
    private readonly ConcurrentDictionary<Guid, ReceivedGroupMessage> _receivedGroupMessages = new();
    private static readonly TimeSpan GroupFreshness = TimeSpan.FromMinutes(5);

    private const int PathLength = 3;

    public event Func<ReceivedMessage, Task>? OnMessageReceived;
    public event Func<Guid, Task>? OnMessageAcknowledged;
    public event Func<ReceivedGroupMessage, Task>? OnGroupMessageReceived;

    public byte[] LocalPublicKey => _dhtNode.EncryptionPublicKey;
    public byte[] LocalSigningPublicKey => _dhtNode.SigningPublicKey;
    public string? LocalUsername { get; private set; }
    public bool IsConnected => _dhtNode.IsRunning;
    public int PeerCount => _dhtNode.KnownNodes;
    public int ActiveRelays => _relayService.ActiveCircuits;
    public int LocalPort => _dhtNode.LocalPort;

    public ChatService(
        Key encryptionKey,
        ILogger<ChatService> chatLogger,
        ILogger<KademliaDhtNode> dhtLogger,
        ILogger<OnionRouter> routerLogger,
        ILogger<RelayService> relayLogger,
        ILogger<ConnectionManager> connectionLogger,
        Key? signingKey = null,
        ChatNodeOptions? nodeOptions = null)
    {
        var options = nodeOptions ?? new ChatNodeOptions();
        _encryptionKey = encryptionKey;
        _signingKey = signingKey;
        _logger = chatLogger;
        _dhtNode = new KademliaDhtNode(
            encryptionKey, dhtLogger, signingKey,
            natTraversal: null,
            enableUdpTransport: options.EnableUdp,
            useStun: options.UseStun,
            publicUdpEndpoint: options.PublicEndpoint,
            networkId: options.NetworkId);
        _router = new OnionRouter(encryptionKey, _dhtNode, routerLogger);

        var routingTable = GetRoutingTable();
        _relayService = new RelayService(routingTable, relayLogger);
        _connectionManager = new ConnectionManager(routingTable, _relayService, connectionLogger);

        _groupManager = new GroupManager(encryptionKey);
        _ratchet = new RatchetSessionManager(encryptionKey);

        _router.OnMessageReceived += HandleIncomingMessageAsync;
        _router.OnAckReceived += HandleAckAsync;
        _router.OnGroupMessageReceived += HandleGroupMessageAsync;
    }

    public GroupInfo CreateGroup(string name) => _groupManager.CreateGroup(name);

    public IReadOnlyList<GroupInfo> GetGroups() => _groupManager.GetAllGroups().ToList();

    public GroupInfo? GetGroup(Guid groupId) => _groupManager.GetGroup(groupId);

    public WrappedGroupKey InviteMember(Guid groupId, byte[] memberPublicKey)
    {
        var invite = _groupManager.GenerateInvite(groupId, memberPublicKey);
        _groupManager.AddMember(groupId, memberPublicKey);
        return invite;
    }

    public GroupInfo? JoinGroup(WrappedGroupKey wrappedKey, string name)
        => _groupManager.JoinGroup(wrappedKey, name);

    public void LeaveGroup(Guid groupId) => _groupManager.LeaveGroup(groupId);

    public IReadOnlyList<ReceivedGroupMessage> GetGroupMessages(Guid groupId)
        => _receivedGroupMessages.Values
            .Where(m => m.GroupId == groupId)
            .OrderBy(m => m.ReceivedAt)
            .ToList();

    public async Task<int> SendGroupMessageAsync(Guid groupId, string content)
    {
        var group = _groupManager.GetGroup(groupId)
            ?? throw new InvalidOperationException("Group not found");

        var message = new GroupMessage
        {
            GroupId = groupId,
            SenderPublicKey = LocalPublicKey,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var encrypted = message.EncryptUnpadded(group.Key);
        var body = encrypted.Serialize();
        var envelope = new byte[1 + body.Length];
        envelope[0] = MessageEnvelope.GroupMessage;
        Buffer.BlockCopy(body, 0, envelope, 1, body.Length);

        var delivered = 0;
        foreach (var member in group.Members)
        {
            if (member.PublicKey.SequenceEqual(LocalPublicKey))
                continue;

            var path = _dhtNode.GetRandomNodesForPath(PathLength);
            if (path.Count == 0)
                continue;

            try
            {
                await _router.SendRawAsync(envelope, member.PublicKey, path).ConfigureAwait(false);
                delivered++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deliver group message to a member");
            }
        }

        return delivered;
    }

    private async Task HandleGroupMessageAsync(EncryptedGroupMessage encrypted)
    {
        var group = _groupManager.GetGroup(encrypted.GroupId);
        if (group == null)
        {
            _logger.LogDebug("Dropped group message for unknown group {GroupId}", encrypted.GroupId);
            return;
        }

        GroupMessage message;
        try
        {
            message = GroupMessage.DecryptUnpadded(encrypted, group.Key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt group message for {GroupId}", encrypted.GroupId);
            return;
        }

        if (!MessageReplayCache.IsTimestampFresh(message.Timestamp, GroupFreshness))
        {
            _logger.LogWarning("Group message {MessageId} rejected: stale timestamp", message.MessageId);
            return;
        }

        if (message.SenderPublicKey.SequenceEqual(LocalPublicKey))
            return;

        string? senderUsername = null;
        var senderKeyHex = Convert.ToHexString(message.SenderPublicKey);
        foreach (var kvp in _keyCache)
        {
            if (Convert.ToHexString(kvp.Value.EncryptionPublicKey) == senderKeyHex)
            {
                senderUsername = kvp.Key;
                break;
            }
        }

        var received = new ReceivedGroupMessage
        {
            GroupId = encrypted.GroupId,
            GroupName = group.Name,
            MessageId = message.MessageId,
            SenderPublicKey = message.SenderPublicKey,
            SenderUsername = senderUsername,
            Content = message.Content,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Timestamp),
            ReceivedAt = DateTimeOffset.UtcNow
        };

        _receivedGroupMessages[message.MessageId] = received;

        if (OnGroupMessageReceived != null)
        {
            await OnGroupMessageReceived(received).ConfigureAwait(false);
        }
    }

    private RoutingTable GetRoutingTable()
    {
        return _dhtNode.RoutingTable;
    }

    public async Task StartAsync(int port, string username, IEnumerable<string>? bootstrapNodes = null)
    {
        LocalUsername = username;

        await _dhtNode.StartAsync(port).ConfigureAwait(false);
        await _relayService.StartAsync().ConfigureAwait(false);

        if (bootstrapNodes != null)
        {
            var endpoints = bootstrapNodes
                .Select(ParseEndpoint)
                .Where(e => e != null)
                .Select(e => e!)
                .ToList();

            await _dhtNode.BootstrapAsync(endpoints).ConfigureAwait(false);
        }

        await _dhtNode.PublishPublicKeyAsync(username).ConfigureAwait(false);
        await FetchOfflineMessagesAsync().ConfigureAwait(false);

        _logger.LogInformation("Chat service started for user {Username} on port {Port}",
            username, port);
    }

    public async Task<SendResult> SendMessageAsync(string recipientUsername, string content)
    {
        var recipientKey = await GetPublicKeyAsync(recipientUsername).ConfigureAwait(false);
        if (recipientKey == null)
        {
            _logger.LogWarning("Could not find public key for user {Username}", recipientUsername);
            return new SendResult(false, null, "User not found");
        }

        var path = _dhtNode.GetRandomNodesForPath(PathLength);
        if (path.Count < PathLength)
        {
            _logger.LogWarning("Not enough peers for onion routing. Need {Required}, have {Available}",
                PathLength, path.Count);

            if (path.Count == 0)
            {
                return new SendResult(false, null, "No peers available for routing");
            }
        }

        if (_signingKey == null)
        {
            return new SendResult(false, null, "Cannot send messages without a signing key");
        }

        var envelope = _ratchet.Seal(recipientKey.EncryptionPublicKey, System.Text.Encoding.UTF8.GetBytes(content));

        var message = new ChatMessage
        {
            SenderPublicKey = LocalPublicKey,
            SenderSigningPublicKey = LocalSigningPublicKey,
            Content = string.Empty,
            RatchetEnvelope = envelope,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        message.Signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(
            _signingKey, message.GetSignableData());

        _pendingMessages[message.MessageId] = new PendingMessage
        {
            Message = message,
            RecipientUsername = recipientUsername,
            SentAt = DateTimeOffset.UtcNow,
            Status = MessageStatus.Sending
        };

        try
        {
            await _router.SendMessageAsync(message, recipientKey.EncryptionPublicKey, path).ConfigureAwait(false);

            _pendingMessages[message.MessageId].Status = MessageStatus.Sent;

            _logger.LogInformation("Message {MessageId} sent to {Recipient}",
                message.MessageId, recipientUsername);

            return new SendResult(true, message.MessageId, null);
        }
        catch (Exception ex)
        {
            _pendingMessages[message.MessageId].Status = MessageStatus.Failed;

            _logger.LogError(ex, "Failed to send message to {Recipient}", recipientUsername);

            return new SendResult(false, message.MessageId, ex.Message);
        }
    }

    public async Task<UserPublicKeyRecord?> GetPublicKeyAsync(string username)
    {
        if (_keyCache.TryGetValue(username, out var cached))
        {
            return cached;
        }

        var record = await _dhtNode.LookupPublicKeyAsync(username).ConfigureAwait(false);
        if (record != null)
        {
            _keyCache[username] = record;
            _logger.LogDebug("Cached public key for {Username}", username);
        }

        return record;
    }

    public IReadOnlyList<ReceivedMessage> GetMessages()
    {
        return _receivedMessages.Values
            .OrderBy(m => m.ReceivedAt)
            .ToList();
    }

    public IReadOnlyList<ReceivedMessage> GetMessagesFrom(string username)
    {
        return _receivedMessages.Values
            .Where(m => m.SenderUsername == username)
            .OrderBy(m => m.ReceivedAt)
            .ToList();
    }

    public IReadOnlyList<PendingMessage> GetPendingMessages()
    {
        return _pendingMessages.Values
            .Where(m => m.Status != MessageStatus.Acknowledged)
            .OrderBy(m => m.SentAt)
            .ToList();
    }

    private async Task FetchOfflineMessagesAsync()
    {
        var messages = await _dhtNode.GetOfflineMessagesAsync().ConfigureAwait(false);

        _logger.LogInformation("Fetched {Count} offline messages", messages.Count);

        foreach (var encryptedMessage in messages)
        {
            try
            {
                await _router.ProcessOfflineMessageAsync(encryptedMessage).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process offline message");
            }
        }
    }

    private async Task HandleIncomingMessageAsync(ChatMessage message, ReplyPath replyPath)
    {
        string content;
        if (message.RatchetEnvelope.Length > 0)
        {
            try
            {
                content = System.Text.Encoding.UTF8.GetString(
                    _ratchet.Open(message.SenderPublicKey, message.RatchetEnvelope));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt ratchet message {MessageId}", message.MessageId);
                return;
            }
        }
        else
        {
            content = message.Content;
        }

        var senderKeyHex = Convert.ToHexString(message.SenderPublicKey);
        string? senderUsername = null;
        foreach (var kvp in _keyCache)
        {
            if (Convert.ToHexString(kvp.Value.EncryptionPublicKey) == senderKeyHex)
            {
                senderUsername = kvp.Key;
                break;
            }
        }

        var received = new ReceivedMessage
        {
            MessageId = message.MessageId,
            Content = content,
            SenderPublicKey = message.SenderPublicKey,
            SenderUsername = senderUsername,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Timestamp),
            ReceivedAt = DateTimeOffset.UtcNow,
            ReplyPath = replyPath
        };

        _receivedMessages[message.MessageId] = received;

        _logger.LogInformation("Received message {MessageId} from {Sender}",
            message.MessageId, senderUsername ?? senderKeyHex[..16]);

        if (OnMessageReceived != null)
        {
            await OnMessageReceived(received).ConfigureAwait(false);
        }
    }

    private Task HandleAckAsync(Guid messageId)
    {
        if (_pendingMessages.TryGetValue(messageId, out var pending))
        {
            pending.Status = MessageStatus.Acknowledged;
            pending.AcknowledgedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Message {MessageId} acknowledged", messageId);

            return OnMessageAcknowledged?.Invoke(messageId) ?? Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    public async Task<SendResult> ReplyAsync(Guid originalMessageId, string content)
    {
        if (!_receivedMessages.TryGetValue(originalMessageId, out var original))
            return new SendResult(false, null, "Original message not found");

        if (original.SenderUsername != null)
        {
            return await SendMessageAsync(original.SenderUsername, content).ConfigureAwait(false);
        }

        if (original.ReplyPath.Tokens.Count == 0)
        {
            return new SendResult(false, null, "Cannot reply - no reply path available");
        }

        if (_signingKey == null)
        {
            return new SendResult(false, null, "Cannot send replies without a signing key");
        }

        var replyEnvelope = _ratchet.Seal(original.SenderPublicKey, System.Text.Encoding.UTF8.GetBytes(content));

        var message = new ChatMessage
        {
            SenderPublicKey = LocalPublicKey,
            SenderSigningPublicKey = LocalSigningPublicKey,
            Content = string.Empty,
            RatchetEnvelope = replyEnvelope,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        message.Signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(
            _signingKey, message.GetSignableData());

        try
        {
            await _router.SendReplyAsync(message, original.ReplyPath).ConfigureAwait(false);
            _logger.LogInformation("Reply {MessageId} sent via reply path", message.MessageId);
            return new SendResult(true, message.MessageId, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send reply via reply path");
            return new SendResult(false, message.MessageId, ex.Message);
        }
    }

    private static System.Net.IPEndPoint? ParseEndpoint(string endpoint)
    {
        var parts = endpoint.Split(':');
        if (parts.Length == 2 &&
            System.Net.IPAddress.TryParse(parts[0], out var ip) &&
            int.TryParse(parts[1], out var port))
        {
            return new System.Net.IPEndPoint(ip, port);
        }
        return null;
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _groupManager.Dispose();
        _ratchet.Dispose();
        await _connectionManager.DisposeAsync().ConfigureAwait(false);
        await _relayService.DisposeAsync().ConfigureAwait(false);
        await _dhtNode.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class ReceivedGroupMessage
{
    public Guid GroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public Guid MessageId { get; init; }
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public string? SenderUsername { get; init; }
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}

public sealed record ChatNodeOptions(
    bool EnableUdp = true,
    bool UseStun = false,
    uint NetworkId = Susurri.Modules.DHT.Core.Kademlia.Protocol.KademliaMessage.DefaultNetworkId,
    System.Net.IPEndPoint? PublicEndpoint = null);

public sealed record SendResult(bool Success, Guid? MessageId, string? Error);

public sealed class PendingMessage
{
    public ChatMessage Message { get; init; } = null!;
    public string RecipientUsername { get; init; } = string.Empty;
    public DateTimeOffset SentAt { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public MessageStatus Status { get; set; }
}

public sealed class ReceivedMessage
{
    public Guid MessageId { get; init; }
    public string Content { get; init; } = string.Empty;
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public string? SenderUsername { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public ReplyPath ReplyPath { get; init; } = new();
}

public enum MessageStatus
{
    Sending,
    Sent,
    Acknowledged,
    Failed
}
