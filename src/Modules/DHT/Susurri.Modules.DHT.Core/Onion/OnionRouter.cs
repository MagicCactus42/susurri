using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private readonly RateLimiter _rateLimiter = new(maxTokens: 30, refillRatePerSecond: 5.0);
    private readonly MessageReplayCache _replayCache = new();
    private static readonly TimeSpan TimestampFreshness = TimeSpan.FromMinutes(5);

    public event Func<ChatMessage, ReplyPath, Task>? OnMessageReceived;

    public event Func<FileTransferMessage, ReplyPath, Task>? OnFileTransferReceived;

    public event Func<Guid, Task>? OnAckReceived;

    public OnionRouter(Key encryptionKey, KademliaDhtNode dhtNode, ILogger<OnionRouter> logger)
    {
        _encryptionKey = encryptionKey;
        _dhtNode = dhtNode;
        _logger = logger;
    }

    public async Task ProcessIncomingAsync(byte[] encryptedPayload, IPEndPoint senderEndpoint)
    {
        using var activity = InboundActivity.Begin("onion.inbound", senderEndpoint);

        if (!_rateLimiter.IsAllowed(senderEndpoint))
        {
            _logger.LogWarning("Rate limited onion message from {Endpoint}", senderEndpoint);
            return;
        }

        try
        {
            var layer = OnionLayer.Deserialize(encryptedPayload);

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

                case OnionLayerType.Ack:
                    await HandleAckAsync(content, senderEndpoint).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing onion message");
        }
    }

    public async Task ProcessOfflineMessageAsync(byte[] encryptedPayload)
    {
        try
        {
            var layer = OnionLayer.Deserialize(encryptedPayload);
            var decrypted = DecryptLayer(layer);
            if (decrypted == null)
            {
                _logger.LogWarning("Failed to decrypt offline message");
                return;
            }

            var payload = RecipientPayload.Deserialize(decrypted);
            var unpaddedMessage = MessagePadding.Unpad(payload.Message);

            if (MessageEnvelope.IsFileTransfer(unpaddedMessage))
            {
                await HandleFileTransferDeliveryAsync(unpaddedMessage, payload.ReplyPath).ConfigureAwait(false);
            }
            else
            {
                var message = ChatMessage.Deserialize(unpaddedMessage);

                if (!message.VerifySignature())
                {
                    _logger.LogWarning("Offline message {MessageId} rejected: missing or invalid signature",
                        message.MessageId);
                    return;
                }

                _logger.LogInformation("Processed offline message {MessageId}", message.MessageId);

                if (OnMessageReceived != null)
                {
                    await OnMessageReceived(message, payload.ReplyPath).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing offline message");
        }
    }

    public async Task SendMessageAsync(ChatMessage message, byte[] recipientPublicKey, IReadOnlyList<KademliaNode> path)
    {
        var builder = new OnionBuilder(_encryptionKey);
        var packet = builder.Build(message, recipientPublicKey, path);

        await SendToNodeAsync(packet.FirstHop.EndPoint, packet.EncryptedPayload).ConfigureAwait(false);

        _logger.LogInformation("Sent onion message {MessageId} via {PathLength} hops",
            message.MessageId, path.Count);
    }

    /// <summary>
    /// Sends a raw byte payload (e.g. file transfer message) through onion routing.
    /// </summary>
    public async Task SendRawAsync(byte[] rawPayload, byte[] recipientPublicKey, IReadOnlyList<KademliaNode> path)
    {
        var builder = new OnionBuilder(_encryptionKey);
        var packet = builder.BuildRaw(rawPayload, recipientPublicKey, path);

        await SendToNodeAsync(packet.FirstHop.EndPoint, packet.EncryptedPayload).ConfigureAwait(false);

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

        var ackContent = new AckMessage
        {
            MessageId = messageId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await SendViaReplyPathAsync(ackContent.Serialize(), replyPath, OnionLayerType.Ack).ConfigureAwait(false);
        _logger.LogInformation("ACK sent for message {MessageId}", messageId);
    }

    public async Task SendReplyAsync(ChatMessage message, ReplyPath replyPath)
    {
        if (replyPath.Tokens.Count == 0)
        {
            _logger.LogWarning("Cannot send reply - no reply tokens");
            return;
        }

        var messageBytes = message.Serialize();
        var paddedMessage = MessagePadding.Pad(messageBytes);

        if (replyPath.SenderPublicKey.Length > 0)
        {
            var recipientPayload = new RecipientPayload
            {
                Message = paddedMessage,
                ReplyPath = new ReplyPath()
            };
            var encrypted = EncryptLayerForNode(recipientPayload.Serialize(), replyPath.SenderPublicKey);
            var deliveryContent = new OnionLayerContent
            {
                Type = OnionLayerType.Delivery,
                InnerPayload = encrypted
            };
            await SendViaReplyPathAsync(deliveryContent.Serialize(), replyPath, OnionLayerType.Relay).ConfigureAwait(false);
        }
        else
        {
            await SendViaReplyPathAsync(paddedMessage, replyPath, OnionLayerType.Relay).ConfigureAwait(false);
        }

        _logger.LogInformation("Reply sent for message {MessageId}", message.MessageId);
    }

    private async Task SendViaReplyPathAsync(byte[] payload, ReplyPath replyPath, OnionLayerType innerType)
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

        if (!string.IsNullOrEmpty(replyPath.FirstHopAddress) &&
            IPAddress.TryParse(replyPath.FirstHopAddress, out var firstHopAddress))
        {
            var endpoint = new IPEndPoint(firstHopAddress, replyPath.FirstHopPort);
            await SendToNodeAsync(endpoint, currentPayload).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("Reply path has no valid first hop endpoint");
        }
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

        if (content.NextHopPort <= 0 || content.NextHopPort > 65535)
        {
            _logger.LogWarning("Relay layer has invalid next hop port: {Port}", content.NextHopPort);
            return;
        }

        if (IPAddress.IsLoopback(nextAddress) || nextAddress.IsIPv6LinkLocal)
        {
            _logger.LogWarning("Blocked relay to loopback/link-local address {Address}", nextAddress);
            return;
        }

        var nextEndpoint = new IPEndPoint(nextAddress, content.NextHopPort);

        _logger.LogDebug("Relaying onion to {NextHop}", nextEndpoint);

        // Random delay (50-500ms) to prevent timing correlation attacks
        var delayMs = Random.Shared.Next(50, 501);
        await Task.Delay(delayMs).ConfigureAwait(false);

        SusurriMetrics.OnionRelayed.Add(1);
        await SendToNodeAsync(nextEndpoint, content.InnerPayload).ConfigureAwait(false);
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
            await SendToNodeAsync(recipientNode.EndPoint, deliveryPayload).ConfigureAwait(false);
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
            var payload = RecipientPayload.Deserialize(content.InnerPayload);
            var unpaddedMessage = MessagePadding.Unpad(payload.Message);

            // Dispatch based on envelope type:
            // - 0x02 = FileTransferMessage
            // - anything else (typically 0x20 = pubkey length 32) = ChatMessage
            if (MessageEnvelope.IsFileTransfer(unpaddedMessage))
            {
                await HandleFileTransferDeliveryAsync(unpaddedMessage, payload.ReplyPath).ConfigureAwait(false);
            }
            else
            {
                await HandleChatDeliveryAsync(unpaddedMessage, payload.ReplyPath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message delivery");
        }
    }

    private async Task HandleChatDeliveryAsync(byte[] unpaddedMessage, ReplyPath replyPath)
    {
        var message = ChatMessage.Deserialize(unpaddedMessage);

        if (!message.VerifySignature())
        {
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "signature"));
            _logger.LogWarning("Message {MessageId} rejected: missing or invalid signature",
                message.MessageId);
            return;
        }

        if (!MessageReplayCache.IsTimestampFresh(message.Timestamp, TimestampFreshness))
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

        await SendAckAsync(message.MessageId, replyPath).ConfigureAwait(false);
    }

    private async Task HandleFileTransferDeliveryAsync(byte[] unpaddedMessage, ReplyPath replyPath)
    {
        var ftMessage = FileTransferMessage.Deserialize(unpaddedMessage);

        if (!ftMessage.VerifySignature())
        {
            SusurriMetrics.AuthFailures.Add(1, new KeyValuePair<string, object?>("kind", "signature"));
            _logger.LogWarning("File transfer message {MessageId} rejected: invalid signature",
                ftMessage.MessageId);
            return;
        }

        if (!MessageReplayCache.IsTimestampFresh(ftMessage.Timestamp, TimestampFreshness))
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
        var layer = OnionLayer.Deserialize(content.ReplyToken);
        var tokenData = DecryptLayer(layer);

        if (tokenData == null)
        {
            _logger.LogWarning("Failed to decrypt reply token");
            return;
        }

        var tokenContent = ReplyTokenContent.Deserialize(tokenData);

        if (tokenContent.IsSenderToken)
        {
            var ack = AckMessage.Deserialize(content.InnerPayload);

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
        else if (!string.IsNullOrEmpty(tokenContent.PreviousHopAddress))
        {
            var prevEndpoint = new IPEndPoint(
                IPAddress.Parse(tokenContent.PreviousHopAddress),
                tokenContent.PreviousHopPort);

            await SendToNodeAsync(prevEndpoint, content.InnerPayload).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("Reply token has no previous hop and no sender marker");
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

    /// <summary>
    /// Test-only hook: when set, SendToNodeAsync delegates to this Func instead of
    /// opening a TCP connection. Used by Susurri.Tests.E2E to wire routers together
    /// in-memory so the full ProcessIncomingAsync → HandleRelayAsync → forward chain
    /// can be exercised without depending on the (currently broken) production wire
    /// transport. See KNOWN-LIMITATIONS.md for the production-transport gap.
    /// </summary>
    internal Func<IPEndPoint, byte[], Task>? TestSendOverride { get; set; }

    private async Task SendToNodeAsync(IPEndPoint endpoint, byte[] payload)
    {
        if (TestSendOverride != null)
        {
            await TestSendOverride(endpoint, payload).ConfigureAwait(false);
            return;
        }

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);

            using var stream = client.GetStream();
            using var writer = new BinaryWriter(stream);

            writer.Write(payload.Length);
            writer.Write(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send to node {Endpoint}", endpoint);
        }
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
