using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Contacts;
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
    private readonly ConcurrentDictionary<string, string> _keyCacheByEncKey = new();

    private readonly ConcurrentDictionary<Guid, PendingMessage> _pendingMessages = new();

    private readonly ConcurrentDictionary<Guid, ReceivedMessage> _receivedMessages = new();

    private readonly GroupManager _groupManager;
    private readonly RatchetSessionManager _ratchet;
    private readonly GroupRatchetManager _groupRatchet;
    private readonly FileTransferService _fileTransfer;
    private readonly byte[]? _localStoreKey;
    private readonly ConcurrentDictionary<Guid, ReceivedGroupMessage> _receivedGroupMessages = new();
    private readonly Dictionary<string, List<EncryptedGroupMessageV2>> _pendingGroupV2 = new();
    private readonly object _pendingGate = new();
    private static readonly TimeSpan GroupFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RekeyFreshness = TimeSpan.FromDays(8);

    private const int PathLength = 3;
    private const int MaxPendingGroupMessages = 32;

    public ContactBook? Contacts { get; }

    public event Func<ReceivedMessage, Task>? OnMessageReceived;
    public event Func<Guid, Task>? OnMessageAcknowledged;
    public event Func<ReceivedGroupMessage, Task>? OnGroupMessageReceived;
    public event Func<FileTransferInfo, Task>? OnFileTransferRequested;
    public event Func<TransferProgress, Task>? OnFileTransferProgress;
    public event Func<CompletedTransfer, Task>? OnFileTransferCompleted;
    public event Func<Guid, string, Task>? OnFileTransferFailed;

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
        ChatNodeOptions? nodeOptions = null,
        byte[]? localStoreKey = null,
        ILogger<FileTransferService>? fileTransferLogger = null)
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
        _router = new OnionRouter(encryptionKey, _dhtNode, routerLogger, options.AllowLoopback);

        var routingTable = GetRoutingTable();
        _relayService = new RelayService(routingTable, relayLogger);
        _connectionManager = new ConnectionManager(routingTable, _relayService, connectionLogger);

        _localStoreKey = localStoreKey == null ? null : (byte[])localStoreKey.Clone();
        _groupManager = new GroupManager(encryptionKey, _localStoreKey, _dhtNode.SigningPublicKey);
        _ratchet = new RatchetSessionManager(encryptionKey);
        _groupRatchet = new GroupRatchetManager(_localStoreKey, _dhtNode.EncryptionPublicKey);
        Contacts = _localStoreKey != null
            ? new ContactBook(_localStoreKey, _dhtNode.EncryptionPublicKey)
            : null;

        _fileTransfer = new FileTransferService(
            _dhtNode, _router,
            fileTransferLogger ?? NullLogger<FileTransferService>.Instance,
            signingKey);
        _fileTransfer.OnTransferRequested += info => OnFileTransferRequested?.Invoke(info) ?? Task.CompletedTask;
        _fileTransfer.OnTransferProgress += p => OnFileTransferProgress?.Invoke(p) ?? Task.CompletedTask;
        _fileTransfer.OnTransferCompleted += t => OnFileTransferCompleted?.Invoke(t) ?? Task.CompletedTask;
        _fileTransfer.OnTransferFailed += (id, reason) => OnFileTransferFailed?.Invoke(id, reason) ?? Task.CompletedTask;

        _router.OnMessageReceived += HandleIncomingMessageAsync;
        _router.OnAckReceived += HandleAckAsync;
        _router.OnGroupMessageReceived += HandleGroupMessageAsync;
        _router.OnGroupMessageV2Received += HandleGroupMessageV2Async;
        _router.OnGroupRekeyReceived += HandleGroupRekeyAsync;
    }

    public async Task<SendResult> SendFileAsync(string recipientUsername, string filePath)
    {
        var recipientKey = await GetPublicKeyAsync(recipientUsername).ConfigureAwait(false);
        if (recipientKey == null)
            return new SendResult(false, null, "User not found");

        byte[] fileData;
        try
        {
            fileData = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new SendResult(false, null, $"Could not read file: {ex.Message}");
        }

        var fileName = Path.GetFileName(filePath);
        return await _fileTransfer.SendFileAsync(fileName, fileData, recipientKey.EncryptionPublicKey)
            .ConfigureAwait(false);
    }

    public Task AcceptFileTransferAsync(Guid transferId) => _fileTransfer.AcceptTransferAsync(transferId);

    public Task RejectFileTransferAsync(Guid transferId, string reason = "Rejected by user")
        => _fileTransfer.RejectTransferAsync(transferId, reason);

    public IReadOnlyList<FileTransferInfo> GetActiveFileTransfers() => _fileTransfer.GetActiveTransfers();

    public string? ResolveUsername(byte[] senderPublicKey) => ResolveSenderName(senderPublicKey);

    public GroupInfo CreateGroup(string name) => _groupManager.CreateGroup(name);

    public IReadOnlyList<GroupInfo> GetGroups() => _groupManager.GetAllGroups().ToList();

    public GroupInfo? GetGroup(Guid groupId) => _groupManager.GetGroup(groupId);

    public WrappedGroupKey InviteMember(Guid groupId, byte[] memberPublicKey)
    {
        var invite = _groupManager.GenerateInvite(groupId, memberPublicKey);
        _groupManager.AddMember(groupId, memberPublicKey);
        return invite;
    }

    public GroupInfo? JoinGroup(WrappedGroupKey wrappedKey, string name, byte[]? ownerSigningPublicKey = null)
        => _groupManager.JoinGroup(wrappedKey, name, ownerSigningPublicKey);

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

        if (_signingKey == null)
            return await SendGroupMessageLegacyAsync(group, message).ConfigureAwait(false);

        var members = group.Members
            .Where(m => !m.PublicKey.SequenceEqual(LocalPublicKey))
            .ToList();
        if (members.Count == 0)
            return 0;

        var keys = _groupRatchet.PrepareSend(group);
        var needDistribution = members
            .Where(m => _groupRatchet.NeedsDistribution(group, m.PublicKey))
            .ToHashSet();

        byte[] body;
        try
        {
            body = EncryptedGroupMessageV2
                .Seal(message, keys.MessageKey, keys.Generation, keys.Iteration, keys.KeyVersion)
                .Serialize();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys.MessageKey);
        }

        GroupSenderKeyDistribution? distribution = null;
        if (needDistribution.Count > 0)
        {
            distribution = new GroupSenderKeyDistribution
            {
                GroupId = groupId,
                Generation = keys.Generation,
                Iteration = keys.Iteration,
                KeyVersion = keys.KeyVersion,
                ChainKey = keys.ChainKeySnapshot,
                SenderPublicKey = LocalPublicKey,
                SenderSigningPublicKey = LocalSigningPublicKey,
                Timestamp = message.Timestamp
            };
            distribution.Signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(
                _signingKey, distribution.GetSignableData());
        }

        var delivered = 0;
        foreach (var member in members)
        {
            var path = _dhtNode.GetRandomNodesForPath(PathLength);
            if (path.Count == 0)
                continue;

            try
            {
                var attach = distribution != null && needDistribution.Contains(member);
                var envelope = BuildGroupEnvelope(body, attach ? distribution!.SealFor(member.PublicKey) : null);
                await _router.SendRawAsync(envelope, member.PublicKey, path).ConfigureAwait(false);
                if (attach)
                    _groupRatchet.MarkDistributed(group, member.PublicKey, keys.Iteration);
                delivered++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deliver group message to a member");
            }
        }

        CryptographicOperations.ZeroMemory(keys.ChainKeySnapshot);
        return delivered;
    }

    private async Task<int> SendGroupMessageLegacyAsync(GroupInfo group, GroupMessage message)
    {
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

    private static byte[] BuildGroupEnvelope(byte[] body, byte[]? sealedDistribution)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(MessageEnvelope.GroupMessageV2);
        writer.Write(sealedDistribution != null);
        if (sealedDistribution != null)
        {
            writer.Write(sealedDistribution.Length);
            writer.Write(sealedDistribution);
        }
        writer.Write(body);

        writer.Flush();
        return ms.ToArray();
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

        _groupManager.TryAddKnownMember(group.GroupId, message.SenderPublicKey);

        var received = new ReceivedGroupMessage
        {
            GroupId = encrypted.GroupId,
            GroupName = group.Name,
            MessageId = message.MessageId,
            SenderPublicKey = message.SenderPublicKey,
            SenderUsername = ResolveSenderName(message.SenderPublicKey),
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

    private async Task HandleGroupMessageV2Async(EncryptedGroupMessageV2 encrypted, byte[]? sealedDistribution)
    {
        var group = _groupManager.GetGroup(encrypted.GroupId);
        if (group == null)
        {
            _logger.LogDebug("Dropped group message for unknown group {GroupId}", encrypted.GroupId);
            return;
        }

        if (sealedDistribution != null && AcceptSenderKeyDistribution(group, encrypted.SenderPublicKey, sealedDistribution))
        {
            await DrainPendingGroupMessagesAsync(group, encrypted.SenderPublicKey).ConfigureAwait(false);
        }

        if (encrypted.SenderPublicKey.SequenceEqual(LocalPublicKey))
            return;

        if (!await TryDeliverGroupV2Async(group, encrypted).ConfigureAwait(false))
            BufferPendingGroupMessage(encrypted);
    }

    private bool AcceptSenderKeyDistribution(GroupInfo group, byte[] senderPublicKey, byte[] sealedDistribution)
    {
        GroupSenderKeyDistribution distribution;
        try
        {
            distribution = GroupSenderKeyDistribution.OpenSealed(sealedDistribution, _encryptionKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unseal group sender key for {GroupId}", group.GroupId);
            return false;
        }

        if (distribution.GroupId != group.GroupId ||
            !distribution.SenderPublicKey.SequenceEqual(senderPublicKey) ||
            distribution.SenderPublicKey.SequenceEqual(LocalPublicKey) ||
            !distribution.VerifySignature())
        {
            _logger.LogWarning("Rejected group sender key for {GroupId}: identity mismatch or bad signature",
                group.GroupId);
            return false;
        }

        _groupRatchet.AcceptDistribution(group, distribution);
        CryptographicOperations.ZeroMemory(distribution.ChainKey);
        _groupManager.TryAddKnownMember(group.GroupId, senderPublicKey);
        return true;
    }

    private async Task<bool> TryDeliverGroupV2Async(GroupInfo group, EncryptedGroupMessageV2 encrypted)
    {
        var messageKey = _groupRatchet.TryTakeMessageKey(
            group, encrypted.SenderPublicKey, encrypted.Generation, encrypted.Iteration, encrypted.KeyVersion);
        if (messageKey == null)
            return false;

        GroupMessage message;
        try
        {
            message = encrypted.Open(messageKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt group message for {GroupId}", encrypted.GroupId);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(messageKey);
        }

        if (!message.SenderPublicKey.SequenceEqual(encrypted.SenderPublicKey))
        {
            _logger.LogWarning("Group message {MessageId} rejected: sender mismatch", encrypted.MessageId);
            return true;
        }

        if (!MessageReplayCache.IsTimestampFresh(message.Timestamp, GroupFreshness))
        {
            _logger.LogWarning("Group message {MessageId} rejected: stale timestamp", message.MessageId);
            return true;
        }

        _groupManager.TryAddKnownMember(group.GroupId, message.SenderPublicKey);

        var received = new ReceivedGroupMessage
        {
            GroupId = encrypted.GroupId,
            GroupName = group.Name,
            MessageId = message.MessageId,
            SenderPublicKey = message.SenderPublicKey,
            SenderUsername = ResolveSenderName(message.SenderPublicKey),
            Content = message.Content,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Timestamp),
            ReceivedAt = DateTimeOffset.UtcNow
        };

        _receivedGroupMessages[message.MessageId] = received;

        if (OnGroupMessageReceived != null)
            await OnGroupMessageReceived(received).ConfigureAwait(false);

        return true;
    }

    private void BufferPendingGroupMessage(EncryptedGroupMessageV2 encrypted)
    {
        var key = PendingKey(encrypted.GroupId, encrypted.SenderPublicKey);
        lock (_pendingGate)
        {
            if (!_pendingGroupV2.TryGetValue(key, out var pending))
                _pendingGroupV2[key] = pending = new List<EncryptedGroupMessageV2>();
            if (pending.Count >= MaxPendingGroupMessages)
                pending.RemoveAt(0);
            pending.Add(encrypted);
        }
    }

    private async Task DrainPendingGroupMessagesAsync(GroupInfo group, byte[] senderPublicKey)
    {
        List<EncryptedGroupMessageV2>? pending;
        lock (_pendingGate)
        {
            if (!_pendingGroupV2.Remove(PendingKey(group.GroupId, senderPublicKey), out pending))
                return;
        }

        foreach (var encrypted in pending)
        {
            if (!await TryDeliverGroupV2Async(group, encrypted).ConfigureAwait(false))
                BufferPendingGroupMessage(encrypted);
        }
    }

    private static string PendingKey(Guid groupId, byte[] senderPublicKey)
        => $"{groupId:N}:{Convert.ToHexString(senderPublicKey)}";

    private string? ResolveSenderName(byte[] senderPublicKey)
    {
        var petname = Contacts?.FindByEncryptionKey(senderPublicKey)?.Petname;
        if (petname != null)
            return petname;

        var senderKeyHex = Convert.ToHexString(senderPublicKey);
        return _keyCacheByEncKey.GetValueOrDefault(senderKeyHex);
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
        var contact = Contacts?.Find(username);
        if (contact != null)
        {
            return new UserPublicKeyRecord
            {
                EncryptionPublicKey = contact.EncryptionPublicKey,
                SigningPublicKey = contact.SigningPublicKey,
                Timestamp = contact.AddedAt
            };
        }

        if (_keyCache.TryGetValue(username, out var cached))
        {
            return cached;
        }

        var record = await _dhtNode.LookupPublicKeyAsync(username).ConfigureAwait(false);
        if (record != null)
        {
            _keyCache[username] = record;
            _keyCacheByEncKey[Convert.ToHexString(record.EncryptionPublicKey)] = username;
            _logger.LogDebug("Cached public key for {Username}", username);
        }

        return record;
    }

    public Task<UserPublicKeyRecord?> LookupPublicKeyFreshAsync(string username)
        => _dhtNode.LookupPublicKeyAsync(username);

    public async Task<int> RotateGroupKeyAsync(Guid groupId)
    {
        var group = _groupManager.GetGroup(groupId)
            ?? throw new InvalidOperationException("Group not found");

        if (_signingKey == null)
            throw new InvalidOperationException("Cannot rotate a group key without a signing key");

        _groupManager.RotateKey(groupId);
        return await DistributeGroupRekeyAsync(group).ConfigureAwait(false);
    }

    public async Task<int> KickMemberAsync(Guid groupId, byte[] memberPublicKey)
    {
        var group = _groupManager.GetGroup(groupId)
            ?? throw new InvalidOperationException("Group not found");

        if (!group.IsOwner)
            throw new InvalidOperationException("Only the group owner can remove members");

        if (_signingKey == null)
            throw new InvalidOperationException("Cannot rotate a group key without a signing key");

        _groupManager.RemoveMember(groupId, memberPublicKey);
        _groupManager.RotateKey(groupId);
        return await DistributeGroupRekeyAsync(group).ConfigureAwait(false);
    }

    private async Task<int> DistributeGroupRekeyAsync(GroupInfo group)
    {
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
                var rekey = BuildRekeyFor(group, member.PublicKey);
                var body = rekey.Serialize();
                var envelope = new byte[1 + body.Length];
                envelope[0] = MessageEnvelope.GroupRekey;
                Buffer.BlockCopy(body, 0, envelope, 1, body.Length);

                await _router.SendRawAsync(envelope, member.PublicKey, path).ConfigureAwait(false);
                delivered++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deliver group rekey to a member");
            }
        }

        return delivered;
    }

    private GroupRekeyMessage BuildRekeyFor(GroupInfo group, byte[] memberPublicKey)
    {
        var rekey = new GroupRekeyMessage
        {
            GroupId = group.GroupId,
            Wrapped = group.Key.WrapForMember(memberPublicKey),
            Roster = group.Members.ToList(),
            OwnerSigningPublicKey = LocalSigningPublicKey,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        rekey.Signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(
            _signingKey!, rekey.GetSignableData());
        return rekey;
    }

    private Task HandleGroupRekeyAsync(GroupRekeyMessage rekey)
    {
        if (!MessageReplayCache.IsTimestampFresh(rekey.Timestamp, RekeyFreshness))
        {
            _logger.LogWarning("Group rekey {MessageId} rejected: stale timestamp", rekey.MessageId);
            return Task.CompletedTask;
        }

        var applied = _groupManager.ApplyRekey(rekey);
        if (applied != null)
        {
            _logger.LogInformation("Group {GroupId} re-keyed to version {Version}",
                applied.GroupId, applied.Key.Version);
        }
        else
        {
            _logger.LogDebug("Ignored group rekey {MessageId} for {GroupId}", rekey.MessageId, rekey.GroupId);
        }

        return Task.CompletedTask;
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

        var senderUsername = ResolveSenderName(message.SenderPublicKey);

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
            message.MessageId, senderUsername ?? Convert.ToHexString(message.SenderPublicKey)[..16]);

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
        _fileTransfer.Dispose();
        _groupManager.Dispose();
        _ratchet.Dispose();
        _groupRatchet.Dispose();
        if (_localStoreKey != null)
            CryptographicOperations.ZeroMemory(_localStoreKey);
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
    System.Net.IPEndPoint? PublicEndpoint = null,
    bool AllowLoopback = false);

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
