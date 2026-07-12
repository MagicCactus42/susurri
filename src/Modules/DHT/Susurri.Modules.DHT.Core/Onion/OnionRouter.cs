using System.Net;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Services;
using Susurri.Shared.Abstractions.Diagnostics;
using Susurri.Shared.Abstractions.Logging;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Onion;

public sealed class OnionRouter
{
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly KeyAgreementAlgorithm KeyExchange = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm KeyDerivation = KeyDerivationAlgorithm.HkdfSha256;

    private readonly Key _encryptionKey;
    private readonly KademliaDhtNode _dhtNode;
    private readonly ILogger<OnionRouter> _logger;
    private readonly bool _allowLoopback;
    private readonly RateLimiter _rateLimiter = new(maxTokens: 30, refillRatePerSecond: 5.0);
    private readonly MessageReplayCache _replayCache = new();
    private static readonly TimeSpan TimestampFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OfflineTimestampFreshness = TimeSpan.FromDays(8);

    public event Func<ChatMessage, ReplyPath, Task>? OnMessageReceived;

    public event Func<FileTransferMessage, ReplyPath, Task>? OnFileTransferReceived;

    public event Func<GroupChat.EncryptedGroupMessage, Task>? OnGroupMessageReceived;

    public event Func<GroupChat.EncryptedGroupMessageV2, byte[]?, Task>? OnGroupMessageV2Received;

    public event Func<GroupChat.GroupRekeyMessage, Task>? OnGroupRekeyReceived;

    public event Func<Guid, Task>? OnAckReceived;

    public OnionRouter(Key encryptionKey, KademliaDhtNode dhtNode, ILogger<OnionRouter> logger, bool allowLoopback = false)
    {
        _encryptionKey = encryptionKey;
        _dhtNode = dhtNode;
        _logger = logger;
        _allowLoopback = allowLoopback;
        _dhtNode.OnMessageReceived += (_, payload, sender) => ProcessIncomingAsync(payload, sender);
    }

    // A hop endpoint is deliverable if it is publicly routable, or — only when
    // the node is started in local-test mode — a loopback address. The loopback
    // escape hatch defaults off so production never relays to 127.0.0.1.
    private bool IsHopAllowed(IPAddress address) =>
        NetworkValidator.IsPubliclyRoutable(address) ||
        (_allowLoopback && IPAddress.IsLoopback(address));

    public async Task ProcessIncomingAsync(byte[] wirePayload, IPEndPoint senderEndpoint)
    {
        using var activity = InboundActivity.Begin("onion.inbound", senderEndpoint);

        if (!_rateLimiter.IsAllowed(senderEndpoint))
        {
            _logger.LogWarning("Rate limited onion message from {Endpoint}", senderEndpoint);
            return;
        }

        if (wirePayload.Length < 2)
        {
            _logger.LogWarning("Dropped undersized onion payload from {Endpoint}", senderEndpoint);
            return;
        }

        try
        {
            var kind = (OnionWireKind)wirePayload[0];
            var body = wirePayload.AsSpan(1).ToArray();

            switch (kind)
            {
                case OnionWireKind.Layer:
                    await ProcessLayerAsync(body, senderEndpoint).ConfigureAwait(false);
                    break;

                case OnionWireKind.ReplyChain:
                    await ProcessReplyChainAsync(body, senderEndpoint).ConfigureAwait(false);
                    break;

                default:
                    _logger.LogWarning("Dropped onion payload with unknown wire kind {Kind}", wirePayload[0]);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing onion message");
        }
    }

    private async Task ProcessLayerAsync(byte[] encryptedLayer, IPEndPoint senderEndpoint)
    {
        var layer = OnionLayer.Deserialize(encryptedLayer);

        var decrypted = DecryptLayer(layer);
        if (decrypted == null)
        {
            SusurriMetrics.OnionDecryptFailures.Add(1);
            _logger.LogWarning("Failed to decrypt onion layer");
            return;
        }

        var content = OnionLayerContent.Deserialize(decrypted);

        _logger.LogDebug("Processing onion layer type: {Type}", content.Type);

        switch (content.Type)
        {
            case OnionLayerType.Relay:
                await HandleRelayAsync(content, senderEndpoint).ConfigureAwait(false);
                break;

            case OnionLayerType.FinalHop:
                await HandleFinalHopAsync(content, senderEndpoint).ConfigureAwait(false);
                break;

            case OnionLayerType.Delivery:
                await HandleDeliveryAsync(content).ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning("Dropped onion layer with unexpected type {Type}", content.Type);
                break;
        }
    }

    private async Task ProcessReplyChainAsync(byte[] chainBytes, IPEndPoint senderEndpoint)
    {
        var content = OnionLayerContent.Deserialize(chainBytes);

        if (content.Type != OnionLayerType.Ack)
        {
            _logger.LogWarning("Dropped reply chain element with unexpected type {Type}", content.Type);
            return;
        }

        await HandleAckAsync(content, senderEndpoint).ConfigureAwait(false);
    }

    public async Task ProcessOfflineMessageAsync(byte[] encryptedPayload)
    {
        try
        {
            await DeliverRecipientLayerAsync(encryptedPayload, OfflineTimestampFreshness).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing offline message");
        }
    }

    public async Task SendMessageAsync(ChatMessage message, byte[] recipientPublicKey, IReadOnlyList<KademliaNode> path)
    {
        var builder = new OnionBuilder(_encryptionKey);
        var packet = builder.Build(message, recipientPublicKey, path, _dhtNode.LocalEndPoint);

        await SendToNodeAsync(packet.FirstHop.EndPoint, packet.EncryptedPayload, OnionWireKind.Layer).ConfigureAwait(false);

        _logger.LogInformation("Sent onion message {MessageId} via {PathLength} hops",
            message.MessageId, path.Count);
    }

    public async Task SendRawAsync(byte[] rawPayload, byte[] recipientPublicKey, IReadOnlyList<KademliaNode> path)
    {
        var builder = new OnionBuilder(_encryptionKey);
        var packet = builder.BuildRaw(rawPayload, recipientPublicKey, path, _dhtNode.LocalEndPoint);

        await SendToNodeAsync(packet.FirstHop.EndPoint, packet.EncryptedPayload, OnionWireKind.Layer).ConfigureAwait(false);

        _logger.LogDebug("Sent raw onion payload ({Size} bytes) via {PathLength} hops",
            rawPayload.Length, path.Count);
    }

    public async Task SendAckAsync(Guid messageId, ReplyPath replyPath)
    {
        if (replyPath.Tokens.Count == 0)
        {
            _logger.LogWarning("Cannot send ACK - no reply tokens");
            return;
        }

        if (replyPath.SenderPublicKey.Length != SecurityLimits.PublicKeySize)
        {
            _logger.LogWarning("Cannot send ACK - reply path has no sender public key");
            return;
        }

        var ackContent = new AckMessage
        {
            MessageId = messageId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var sealedContent = new OnionLayerContent
        {
            Type = OnionLayerType.Ack,
            InnerPayload = EncryptLayerForNode(ackContent.Serialize(), replyPath.SenderPublicKey)
        };

        await SendViaReplyPathAsync(sealedContent.Serialize(), replyPath).ConfigureAwait(false);
        _logger.LogInformation("ACK sent for message {MessageId}", messageId);
    }

    public async Task SendReplyAsync(ChatMessage message, ReplyPath replyPath)
    {
        if (replyPath.Tokens.Count == 0)
        {
            _logger.LogWarning("Cannot send reply - no reply tokens");
            return;
        }

        if (replyPath.SenderPublicKey.Length != SecurityLimits.PublicKeySize)
        {
            _logger.LogWarning("Cannot send reply - reply path has no sender public key");
            return;
        }

        var messageBytes = message.Serialize();
        var paddedMessage = MessagePadding.Pad(messageBytes);

        var recipientPayload = new RecipientPayload
        {
            Message = paddedMessage,
            ReplyPath = new ReplyPath()
        };

        var sealedContent = new OnionLayerContent
        {
            Type = OnionLayerType.Delivery,
            InnerPayload = EncryptLayerForNode(recipientPayload.Serialize(), replyPath.SenderPublicKey)
        };

        await SendViaReplyPathAsync(sealedContent.Serialize(), replyPath).ConfigureAwait(false);

        _logger.LogInformation("Reply sent for message {MessageId}", message.MessageId);
    }

    private async Task SendViaReplyPathAsync(byte[] payload, ReplyPath replyPath)
    {
        var currentPayload = payload;

        for (int i = 0; i < replyPath.Tokens.Count; i++)
        {
            var token = replyPath.Tokens[i];
            var layerContent = new OnionLayerContent
            {
                Type = OnionLayerType.Ack,
                ReplyToken = token,
                InnerPayload = currentPayload
            };
            currentPayload = layerContent.Serialize();
        }

        if (string.IsNullOrEmpty(replyPath.FirstHopAddress) ||
            !IPAddress.TryParse(replyPath.FirstHopAddress, out var firstHopAddress))
        {
            _logger.LogWarning("Reply path has no valid first hop endpoint");
            return;
        }

        if (replyPath.FirstHopPort <= 0 || replyPath.FirstHopPort > SecurityLimits.MaxPortValue)
        {
            _logger.LogWarning("Reply path has invalid first hop port: {Port}", replyPath.FirstHopPort);
            return;
        }

        if (TestSendOverride == null && !IsHopAllowed(firstHopAddress))
        {
            _logger.LogWarning("Blocked reply to non-routable address {Address}", firstHopAddress);
            return;
        }

        var endpoint = new IPEndPoint(firstHopAddress, replyPath.FirstHopPort);
        await SendToNodeAsync(endpoint, currentPayload, OnionWireKind.ReplyChain).ConfigureAwait(false);
    }

    private async Task HandleRelayAsync(OnionLayerContent content, IPEndPoint senderEndpoint)
    {
        if (string.IsNullOrEmpty(content.NextHopAddress))
        {
            _logger.LogWarning("Relay layer missing next hop address");
            return;
        }

        if (!IPAddress.TryParse(content.NextHopAddress, out var nextAddress))
        {
            _logger.LogWarning("Relay layer has invalid next hop address");
            return;
        }

        if (content.NextHopPort <= 0 || content.NextHopPort > SecurityLimits.MaxPortValue)
        {
            _logger.LogWarning("Relay layer has invalid next hop port: {Port}", content.NextHopPort);
            return;
        }

        if (TestSendOverride == null && !IsHopAllowed(nextAddress))
        {
            _logger.LogWarning("Blocked relay to non-routable address {Address}", nextAddress);
            return;
        }

        var nextEndpoint = new IPEndPoint(nextAddress, content.NextHopPort);

        _logger.LogDebug("Relaying onion to {NextHop}", nextEndpoint);

        var delayMs = System.Security.Cryptography.RandomNumberGenerator.GetInt32(50, 501);
        await Task.Delay(delayMs).ConfigureAwait(false);

        SusurriMetrics.OnionRelayed.Add(1);
        await SendToNodeAsync(nextEndpoint, content.InnerPayload, OnionWireKind.Layer).ConfigureAwait(false);
    }

    private async Task HandleFinalHopAsync(OnionLayerContent content, IPEndPoint senderEndpoint)
    {
        _logger.LogDebug("Final hop - attempting to deliver message");

        var recipientPublicKey = content.RecipientPublicKey;
        if (recipientPublicKey.Length == 0)
        {
            _logger.LogWarning("Final hop layer missing recipient public key");
            return;
        }

        if (recipientPublicKey.SequenceEqual(_encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)))
        {
            var deliveryContent = new OnionLayerContent
            {
                Type = OnionLayerType.Delivery,
                InnerPayload = content.InnerPayload
            };
            await HandleDeliveryAsync(deliveryContent).ConfigureAwait(false);
            return;
        }

        var recipientId = Kademlia.KademliaId.FromPublicKey(recipientPublicKey);
        var closestNodes = _dhtNode.RoutingTable.FindClosestNodes(recipientId, 1);
        var recipientNode = closestNodes.FirstOrDefault(n => n.EncryptionPublicKey.SequenceEqual(recipientPublicKey));

        if (recipientNode != null)
        {
            var deliveryContent = new OnionLayerContent
            {
                Type = OnionLayerType.Delivery,
                InnerPayload = content.InnerPayload
            };
            var deliveryPayload = EncryptLayerForNode(deliveryContent.Serialize(), recipientPublicKey);
            await SendToNodeAsync(recipientNode.EndPoint, deliveryPayload, OnionWireKind.Layer).ConfigureAwait(false);
            _logger.LogInformation("Message forwarded to recipient node");
        }
        else
        {
            await _dhtNode.StoreOfflineMessageAsync(recipientPublicKey, content.InnerPayload).ConfigureAwait(false);
            _logger.LogInformation("Recipient offline, message stored for later delivery");
        }
    }

    private byte[] EncryptLayerForNode(byte[] plaintext, byte[] recipientPublicKey)
    {
        using var ephemeralKey = Key.Create(KeyExchange);
        var ephemeralPubKeyBytes = ephemeralKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var recipientPubKey = PublicKey.Import(KeyExchange, recipientPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = KeyExchange.Agree(ephemeralKey, recipientPubKey);
        if (sharedSecret == null)
            throw new System.Security.Cryptography.CryptographicException("Key agreement failed");

        using var symmetricKey = KeyDerivation.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            HkdfContexts.OnionLayer,
            Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var nonce = new byte[Aead.NonceSize];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

        var ciphertext = Aead.Encrypt(symmetricKey, nonce, null, plaintext);

        var layer = new OnionLayer
        {
            EphemeralPublicKey = ephemeralPubKeyBytes,
            Nonce = nonce,
            Ciphertext = ciphertext
        };

        return layer.Serialize();
    }

    private async Task HandleDeliveryAsync(OnionLayerContent content)
    {
        try
        {
            SusurriMetrics.OnionDelivered.Add(1);
            await DeliverRecipientLayerAsync(content.InnerPayload, TimestampFreshness).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message delivery");
        }
    }

    private async Task DeliverRecipientLayerAsync(byte[] encryptedRecipientLayer, TimeSpan freshness)
    {
        var layer = OnionLayer.Deserialize(encryptedRecipientLayer);

        var decrypted = DecryptLayer(layer);
        if (decrypted == null)
        {
            SusurriMetrics.OnionDecryptFailures.Add(1);
            _logger.LogWarning("Failed to decrypt recipient layer");
            return;
        }

        var payload = RecipientPayload.Deserialize(decrypted);
        var unpaddedMessage = MessagePadding.Unpad(payload.Message);

        if (MessageEnvelope.IsFileTransfer(unpaddedMessage))
        {
            await HandleFileTransferDeliveryAsync(unpaddedMessage, payload.ReplyPath, freshness).ConfigureAwait(false);
        }
        else if (MessageEnvelope.IsGroupMessage(unpaddedMessage))
        {
            await HandleGroupDeliveryAsync(unpaddedMessage).ConfigureAwait(false);
        }
        else if (MessageEnvelope.IsGroupMessageV2(unpaddedMessage))
        {
            await HandleGroupV2DeliveryAsync(unpaddedMessage).ConfigureAwait(false);
        }
        else if (MessageEnvelope.IsGroupRekey(unpaddedMessage))
        {
            await HandleGroupRekeyDeliveryAsync(unpaddedMessage).ConfigureAwait(false);
        }
        else
        {
            await HandleChatDeliveryAsync(unpaddedMessage, payload.ReplyPath, freshness).ConfigureAwait(false);
        }
    }

    private async Task HandleGroupDeliveryAsync(byte[] unpaddedMessage)
    {
        var encrypted = GroupChat.EncryptedGroupMessage.Deserialize(unpaddedMessage.AsSpan(1).ToArray());

        if (!_replayCache.TryRecord(encrypted.MessageId))
        {
            SusurriMetrics.ReplaysDropped.Add(1, new KeyValuePair<string, object?>("scope", "onion-group"));
            _logger.LogDebug("Replay dropped: group message {MessageId}", encrypted.MessageId);
            return;
        }

        _logger.LogInformation("Received group message {MessageId} for group {GroupId}",
            encrypted.MessageId, encrypted.GroupId);

        // Authenticity + freshness are verified by ChatService, which holds the
        // group symmetric key needed to decrypt and read the timestamp.
        if (OnGroupMessageReceived != null)
        {
            await OnGroupMessageReceived(encrypted).ConfigureAwait(false);
        }
    }

    private async Task HandleGroupV2DeliveryAsync(byte[] unpaddedMessage)
    {
        using var ms = new MemoryStream(unpaddedMessage, 1, unpaddedMessage.Length - 1);
        using var reader = new BinaryReader(ms);

        byte[]? sealedDistribution = null;
        if (reader.ReadBoolean())
        {
            var distributionLen = reader.ReadInt32();
            if (distributionLen <= 0 || distributionLen > SecurityLimits.MaxValueSize)
            {
                _logger.LogWarning("Dropped group message with invalid distribution length {Length}", distributionLen);
                return;
            }
            sealedDistribution = reader.ReadBytes(distributionLen);
        }

        var body = reader.ReadBytes((int)(ms.Length - ms.Position));
        var encrypted = GroupChat.EncryptedGroupMessageV2.Deserialize(body);

        if (!_replayCache.TryRecord(encrypted.MessageId))
        {
            SusurriMetrics.ReplaysDropped.Add(1, new KeyValuePair<string, object?>("scope", "onion-group"));
            _logger.LogDebug("Replay dropped: group message {MessageId}", encrypted.MessageId);
            return;
        }

        _logger.LogInformation("Received group message {MessageId} for group {GroupId}",
            encrypted.MessageId, encrypted.GroupId);

        if (OnGroupMessageV2Received != null)
        {
            await OnGroupMessageV2Received(encrypted, sealedDistribution).ConfigureAwait(false);
        }
    }

    private async Task HandleGroupRekeyDeliveryAsync(byte[] unpaddedMessage)
    {
        var rekey = GroupChat.GroupRekeyMessage.Deserialize(unpaddedMessage.AsSpan(1).ToArray());

        if (!_replayCache.TryRecord(rekey.MessageId))
        {
            SusurriMetrics.ReplaysDropped.Add(1, new KeyValuePair<string, object?>("scope", "onion-group-rekey"));
            _logger.LogDebug("Replay dropped: group rekey {MessageId}", rekey.MessageId);
            return;
        }

        _logger.LogInformation("Received group rekey {MessageId} for group {GroupId}",
            rekey.MessageId, rekey.GroupId);

        if (OnGroupRekeyReceived != null)
        {
            await OnGroupRekeyReceived(rekey).ConfigureAwait(false);
        }
    }

    private async Task HandleChatDeliveryAsync(byte[] unpaddedMessage, ReplyPath replyPath, TimeSpan freshness)
    {
        var message = ChatMessage.Deserialize(unpaddedMessage);

        if (!message.VerifySignature())
        {
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "signature"));
            _logger.LogWarning("Message {MessageId} rejected: missing or invalid signature",
                message.MessageId);
            return;
        }

        if (!MessageReplayCache.IsTimestampFresh(message.Timestamp, freshness))
        {
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "timestamp"));
            _logger.LogWarning("Message {MessageId} rejected: stale timestamp", message.MessageId);
            return;
        }

        if (!_replayCache.TryRecord(message.MessageId))
        {
            SusurriMetrics.ReplaysDropped.Add(1, new KeyValuePair<string, object?>("scope", "onion-chat"));
            _logger.LogDebug("Replay dropped: chat message {MessageId}", message.MessageId);
            return;
        }

        _logger.LogDebug("Message {MessageId} signature verified", message.MessageId);

        _logger.LogInformation("Received message {MessageId} from {SenderFingerprint}",
            message.MessageId, LogRedaction.KeyFingerprint(message.SenderPublicKey));

        if (OnMessageReceived != null)
        {
            await OnMessageReceived(message, replyPath).ConfigureAwait(false);
        }

        if (replyPath.Tokens.Count > 0)
        {
            await SendAckAsync(message.MessageId, replyPath).ConfigureAwait(false);
        }
    }

    private async Task HandleFileTransferDeliveryAsync(byte[] unpaddedMessage, ReplyPath replyPath, TimeSpan freshness)
    {
        var ftMessage = FileTransferMessage.Deserialize(unpaddedMessage);

        if (!ftMessage.VerifySignature())
        {
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "signature"));
            _logger.LogWarning("File transfer message {MessageId} rejected: invalid signature",
                ftMessage.MessageId);
            return;
        }

        if (!MessageReplayCache.IsTimestampFresh(ftMessage.Timestamp, freshness))
        {
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "timestamp"));
            _logger.LogWarning("File transfer message {MessageId} rejected: stale timestamp",
                ftMessage.MessageId);
            return;
        }

        if (!_replayCache.TryRecord(ftMessage.MessageId))
        {
            SusurriMetrics.ReplaysDropped.Add(1, new KeyValuePair<string, object?>("scope", "onion-file"));
            _logger.LogDebug("Replay dropped: file transfer message {MessageId}", ftMessage.MessageId);
            return;
        }

        _logger.LogInformation("Received file transfer message {Type} {MessageId} from {SenderFingerprint}",
            ftMessage.FileType, ftMessage.MessageId,
            LogRedaction.KeyFingerprint(ftMessage.SenderPublicKey));

        if (OnFileTransferReceived != null)
        {
            await OnFileTransferReceived(ftMessage, replyPath).ConfigureAwait(false);
        }
    }

    private async Task HandleAckAsync(OnionLayerContent content, IPEndPoint senderEndpoint)
    {
        if (content.ReplyToken.Length == 0)
        {
            _logger.LogWarning("Reply chain element missing reply token");
            return;
        }

        var tokenLayer = OnionLayer.Deserialize(content.ReplyToken);
        var tokenData = DecryptLayer(tokenLayer);

        if (tokenData == null)
        {
            _logger.LogWarning("Failed to decrypt reply token");
            return;
        }

        var tokenContent = ReplyTokenContent.Deserialize(tokenData);

        if (tokenContent.IsSenderToken)
        {
            await HandleReplyChainTerminusAsync(content.InnerPayload).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(tokenContent.PreviousHopAddress) ||
            !IPAddress.TryParse(tokenContent.PreviousHopAddress, out var prevAddress))
        {
            _logger.LogWarning("Reply token has no valid previous hop and no sender marker");
            return;
        }

        if (tokenContent.PreviousHopPort <= 0 || tokenContent.PreviousHopPort > SecurityLimits.MaxPortValue)
        {
            _logger.LogWarning("Reply token has invalid previous hop port: {Port}", tokenContent.PreviousHopPort);
            return;
        }

        if (TestSendOverride == null && !IsHopAllowed(prevAddress))
        {
            _logger.LogWarning("Blocked reply forward to non-routable address {Address}", prevAddress);
            return;
        }

        var prevEndpoint = new IPEndPoint(prevAddress, tokenContent.PreviousHopPort);

        var delayMs = System.Security.Cryptography.RandomNumberGenerator.GetInt32(50, 501);
        await Task.Delay(delayMs).ConfigureAwait(false);

        await SendToNodeAsync(prevEndpoint, content.InnerPayload, OnionWireKind.ReplyChain).ConfigureAwait(false);
    }

    private async Task HandleReplyChainTerminusAsync(byte[] payload)
    {
        var inner = OnionLayerContent.Deserialize(payload);

        switch (inner.Type)
        {
            case OnionLayerType.Delivery:
                await HandleDeliveryAsync(inner).ConfigureAwait(false);
                break;

            case OnionLayerType.Ack:
                await HandleSealedAckAsync(inner.InnerPayload).ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning("Reply chain terminus carried unexpected type {Type}", inner.Type);
                break;
        }
    }

    private async Task HandleSealedAckAsync(byte[] sealedAck)
    {
        var layer = OnionLayer.Deserialize(sealedAck);
        var ackData = DecryptLayer(layer);

        if (ackData == null)
        {
            _logger.LogWarning("Failed to decrypt sealed ACK");
            return;
        }

        var ack = AckMessage.Deserialize(ackData);

        if (!MessageReplayCache.IsTimestampFresh(ack.Timestamp, TimestampFreshness))
        {
            _logger.LogWarning("ACK {MessageId} rejected: stale timestamp", ack.MessageId);
            return;
        }

        if (!_replayCache.TryRecord(ack.MessageId))
        {
            _logger.LogDebug("Replay dropped: ACK {MessageId}", ack.MessageId);
            return;
        }

        _logger.LogInformation("Received ACK for message {MessageId}", ack.MessageId);

        if (OnAckReceived != null)
        {
            await OnAckReceived(ack.MessageId).ConfigureAwait(false);
        }
    }

    private byte[]? DecryptLayer(OnionLayer layer)
    {
        try
        {
            var ephemeralPubKey = PublicKey.Import(
                KeyExchange,
                layer.EphemeralPublicKey,
                KeyBlobFormat.RawPublicKey);

            using var sharedSecret = KeyExchange.Agree(_encryptionKey, ephemeralPubKey);
            if (sharedSecret == null)
                return null;

            using var symmetricKey = KeyDerivation.DeriveKey(
                sharedSecret,
                ReadOnlySpan<byte>.Empty,
                HkdfContexts.OnionLayer,
                Aead,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

            var plaintext = Aead.Decrypt(symmetricKey, layer.Nonce, null, layer.Ciphertext);

            return plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Layer decryption failed");
            return null;
        }
    }

    internal Func<IPEndPoint, byte[], Task>? TestSendOverride { get; set; }

    private async Task SendToNodeAsync(IPEndPoint endpoint, byte[] payload, OnionWireKind kind)
    {
        var wirePayload = new byte[payload.Length + 1];
        wirePayload[0] = (byte)kind;
        Buffer.BlockCopy(payload, 0, wirePayload, 1, payload.Length);

        if (TestSendOverride != null)
        {
            await TestSendOverride(endpoint, wirePayload).ConfigureAwait(false);
            return;
        }

        await _dhtNode.SendOnionAsync(endpoint, wirePayload).ConfigureAwait(false);
    }
}

public sealed class AckMessage
{
    public Guid MessageId { get; init; }
    public long Timestamp { get; init; }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(MessageId.ToByteArray());
        writer.Write(Timestamp);

        return ms.ToArray();
    }

    public static AckMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var messageId = new Guid(reader.ReadBytes(16));
        var timestamp = reader.ReadInt64();

        return new AckMessage
        {
            MessageId = messageId,
            Timestamp = timestamp
        };
    }
}
