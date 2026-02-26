using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Onion;

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

    // Cache of username -> public key mappings
    private readonly ConcurrentDictionary<string, UserPublicKeyRecord> _keyCache = new();

    // Pending messages waiting for ACK
    private readonly ConcurrentDictionary<Guid, PendingMessage> _pendingMessages = new();

    // Received messages
    private readonly ConcurrentDictionary<Guid, ReceivedMessage> _receivedMessages = new();

    private const int PathLength = 3;

    public event Func<ReceivedMessage, Task>? OnMessageReceived;
    public event Func<Guid, Task>? OnMessageAcknowledged;

    public byte[] LocalPublicKey => _dhtNode.EncryptionPublicKey;
    public byte[] LocalSigningPublicKey => _dhtNode.SigningPublicKey;
    public string? LocalUsername { get; private set; }
    public bool IsConnected => _dhtNode.IsRunning;
    public int PeerCount => _dhtNode.KnownNodes;
    public int ActiveRelays => _relayService.ActiveCircuits;

    public ChatService(
        Key encryptionKey,
        ILogger<ChatService> chatLogger,
        ILogger<KademliaDhtNode> dhtLogger,
        ILogger<OnionRouter> routerLogger,
        ILogger<RelayService> relayLogger,
        ILogger<ConnectionManager> connectionLogger,
        Key? signingKey = null)
    {
        _encryptionKey = encryptionKey;
        _signingKey = signingKey;
        _logger = chatLogger;
        _dhtNode = new KademliaDhtNode(encryptionKey, dhtLogger, signingKey);
        _router = new OnionRouter(encryptionKey, _dhtNode, routerLogger);

        var routingTable = GetRoutingTable();
        _relayService = new RelayService(routingTable, relayLogger);
        _connectionManager = new ConnectionManager(routingTable, _relayService, connectionLogger);

        _router.OnMessageReceived += HandleIncomingMessageAsync;
        _router.OnAckReceived += HandleAckAsync;
    }

    private RoutingTable GetRoutingTable()
    {
        return _dhtNode.RoutingTable;
    }

    public async Task StartAsync(int port, string username, IEnumerable<string>? bootstrapNodes = null)
    {
        LocalUsername = username;

        await _dhtNode.StartAsync(port);
        await _relayService.StartAsync();

        if (bootstrapNodes != null)
        {
            var endpoints = bootstrapNodes
                .Select(ParseEndpoint)
                .Where(e => e != null)
                .Select(e => e!)
                .ToList();

            await _dhtNode.BootstrapAsync(endpoints);
        }

        await _dhtNode.PublishPublicKeyAsync(username);
        await FetchOfflineMessagesAsync();

        _logger.LogInformation("Chat service started for user {Username} on port {Port}",
            username, port);
    }

    public async Task<SendResult> SendMessageAsync(string recipientUsername, string content)
    {
        var recipientKey = await GetPublicKeyAsync(recipientUsername);
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

        var message = new ChatMessage
        {
            SenderPublicKey = LocalPublicKey,
            SenderSigningPublicKey = LocalSigningPublicKey,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        // Sign the message if we have a signing key
        if (_signingKey != null)
        {
            message.Signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(
                _signingKey, message.GetSignableData());
        }

        _pendingMessages[message.MessageId] = new PendingMessage
        {
            Message = message,
            RecipientUsername = recipientUsername,
            SentAt = DateTimeOffset.UtcNow,
            Status = MessageStatus.Sending
        };

        try
        {
            await _router.SendMessageAsync(message, recipientKey.EncryptionPublicKey, path);

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

        var record = await _dhtNode.LookupPublicKeyAsync(username);
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
        var messages = await _dhtNode.GetOfflineMessagesAsync();

        _logger.LogInformation("Fetched {Count} offline messages", messages.Count);

        foreach (var encryptedMessage in messages)
        {
            try
            {
                await _router.ProcessOfflineMessageAsync(encryptedMessage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process offline message");
            }
        }
    }

    private async Task HandleIncomingMessageAsync(ChatMessage message, ReplyPath replyPath)
    {
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
            Content = message.Content,
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
            await OnMessageReceived(received);
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

        // If we know the sender by username, use normal message sending
        if (original.SenderUsername != null)
        {
            return await SendMessageAsync(original.SenderUsername, content);
        }

        // Otherwise use the reply path for anonymous replies
        if (original.ReplyPath.Tokens.Count == 0)
        {
            return new SendResult(false, null, "Cannot reply - no reply path available");
        }

        var message = new ChatMessage
        {
            SenderPublicKey = LocalPublicKey,
            SenderSigningPublicKey = LocalSigningPublicKey,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        if (_signingKey != null)
        {
            message.Signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(
                _signingKey, message.GetSignableData());
        }

        try
        {
            await _router.SendReplyAsync(message, original.ReplyPath);
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

    public async ValueTask DisposeAsync()
    {
        await _connectionManager.DisposeAsync();
        await _relayService.DisposeAsync();
        await _dhtNode.DisposeAsync();
    }
}

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
