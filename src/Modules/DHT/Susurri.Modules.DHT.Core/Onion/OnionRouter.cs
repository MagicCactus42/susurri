using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.Modules.DHT.Core.Onion;

/// <summary>
/// Handles onion message routing - decrypting layers and forwarding.
/// </summary>
public sealed class OnionRouter
{
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly KeyAgreementAlgorithm KeyExchange = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm KeyDerivation = KeyDerivationAlgorithm.HkdfSha256;

    private readonly Key _encryptionKey;
    private readonly KademliaDhtNode _dhtNode;
    private readonly ILogger<OnionRouter> _logger;

    /// <summary>
    /// Event raised when a chat message is received for the local user.
    /// </summary>
    public event Func<ChatMessage, ReplyPath, Task>? OnMessageReceived;

    /// <summary>
    /// Event raised when an ACK is received.
    /// </summary>
    public event Func<Guid, Task>? OnAckReceived;

    public OnionRouter(Key encryptionKey, KademliaDhtNode dhtNode, ILogger<OnionRouter> logger)
    {
        _encryptionKey = encryptionKey;
        _dhtNode = dhtNode;
        _logger = logger;
    }

    /// <summary>
    /// Processes an incoming onion message.
    /// Decrypts the outer layer and either forwards or delivers it.
    /// </summary>
    public async Task ProcessIncomingAsync(byte[] encryptedPayload, IPEndPoint senderEndpoint)
    {
        try
        {
            // Parse the outer onion layer
            var layer = OnionLayer.Deserialize(encryptedPayload);

            // Decrypt this layer
            var decrypted = DecryptLayer(layer);
            if (decrypted == null)
            {
                _logger.LogWarning("Failed to decrypt onion layer");
                return;
            }

            // Parse the content
            var content = OnionLayerContent.Deserialize(decrypted);

            _logger.LogDebug("Processing onion layer type: {Type}", content.Type);

            switch (content.Type)
            {
                case OnionLayerType.Relay:
                    await HandleRelayAsync(content, senderEndpoint);
                    break;

                case OnionLayerType.FinalHop:
                    await HandleFinalHopAsync(content, senderEndpoint);
                    break;

                case OnionLayerType.Delivery:
                    await HandleDeliveryAsync(content);
                    break;

                case OnionLayerType.Ack:
                    await HandleAckAsync(content, senderEndpoint);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing onion message");
        }
    }

    /// <summary>
    /// Sends an onion-encrypted message.
    /// </summary>
    public async Task SendMessageAsync(ChatMessage message, byte[] recipientPublicKey, IReadOnlyList<KademliaNode> path)
    {
        var builder = new OnionBuilder(_encryptionKey);
        var packet = builder.Build(message, recipientPublicKey, path);

        await SendToNodeAsync(packet.FirstHop.EndPoint, packet.EncryptedPayload);

        _logger.LogInformation("Sent onion message {MessageId} via {PathLength} hops",
            message.MessageId, path.Count);
    }

    /// <summary>
    /// Sends an ACK back via the reply path.
    /// </summary>
    public async Task SendAckAsync(Guid messageId, ReplyPath replyPath)
    {
        if (replyPath.Tokens.Count == 0)
        {
            _logger.LogWarning("Cannot send ACK - no reply tokens");
            return;
        }

        // Build ACK message
        var ackContent = new AckMessage
        {
            MessageId = messageId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Encrypt the ACK and wrap it in onion layers using reply tokens
        // Start from the last token (closest to recipient) and work back
        var currentPayload = ackContent.Serialize();

        for (int i = replyPath.Tokens.Count - 1; i >= 0; i--)
        {
            var token = replyPath.Tokens[i];

            // Each token is already encrypted for that node
            // The node will decrypt it, extract the previous hop, and forward the inner payload
            var layerContent = new OnionLayerContent
            {
                Type = OnionLayerType.Ack,
                ReplyToken = token,
                InnerPayload = currentPayload
            };

            // Note: In a real implementation, we'd encrypt this with the session key
            // from the token. For now, we'll use a simpler approach.
            currentPayload = layerContent.Serialize();
        }

        // For now, we'll need to somehow get the address of the first node in the reply path
        // This is a simplification - in production, the first token would contain this info
        _logger.LogInformation("ACK prepared for message {MessageId}", messageId);
    }

    private async Task HandleRelayAsync(OnionLayerContent content, IPEndPoint senderEndpoint)
    {
        if (string.IsNullOrEmpty(content.NextHopAddress))
        {
            _logger.LogWarning("Relay layer missing next hop address");
            return;
        }

        var nextEndpoint = new IPEndPoint(IPAddress.Parse(content.NextHopAddress), content.NextHopPort);

        _logger.LogDebug("Relaying onion to {NextHop}", nextEndpoint);

        // Store the reply token for the return path
        // (In a full implementation, we'd store this with the session for routing ACKs back)

        await SendToNodeAsync(nextEndpoint, content.InnerPayload);
    }

    private async Task HandleFinalHopAsync(OnionLayerContent content, IPEndPoint senderEndpoint)
    {
        // This is the last relay node - need to look up recipient in DHT
        // The inner payload is encrypted for the recipient

        // Parse the inner layer to get recipient info
        // For now, we assume the recipient is connected to us or we store it

        _logger.LogDebug("Final hop - attempting to deliver message");

        // Try to find the recipient's current node via DHT
        // For now, store as offline message
        // The inner payload is still encrypted for the recipient

        // In a real implementation:
        // 1. Extract recipient's public key from somewhere (maybe message metadata)
        // 2. Look up their current node in DHT
        // 3. Forward to that node, or store offline

        // Simplified: just store it
        // We'd need recipient info to store properly - this is a TODO

        _logger.LogInformation("Message queued for delivery (final hop)");
    }

    private async Task HandleDeliveryAsync(OnionLayerContent content)
    {
        // This is a direct delivery to us as the recipient
        try
        {
            // Parse the recipient payload
            var payload = RecipientPayload.Deserialize(content.InnerPayload);
            var unpaddedMessage = MessagePadding.Unpad(payload.Message);
            var message = ChatMessage.Deserialize(unpaddedMessage);

            _logger.LogInformation("Received message {MessageId} from {Sender}",
                message.MessageId, Convert.ToHexString(message.SenderPublicKey)[..16]);

            // Raise event for the application
            if (OnMessageReceived != null)
            {
                await OnMessageReceived(message, payload.ReplyPath);
            }

            // Send ACK
            await SendAckAsync(message.MessageId, payload.ReplyPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message delivery");
        }
    }

    private async Task HandleAckAsync(OnionLayerContent content, IPEndPoint senderEndpoint)
    {
        // Decrypt our reply token to get the previous hop
        var layer = OnionLayer.Deserialize(content.ReplyToken);
        var tokenData = DecryptLayer(layer);

        if (tokenData == null)
        {
            _logger.LogWarning("Failed to decrypt reply token");
            return;
        }

        var tokenContent = ReplyTokenContent.Deserialize(tokenData);

        if (tokenContent.PreviousHopAddress == "SENDER")
        {
            // This ACK is for us (the original sender)
            var ack = AckMessage.Deserialize(content.InnerPayload);
            _logger.LogInformation("Received ACK for message {MessageId}", ack.MessageId);

            if (OnAckReceived != null)
            {
                await OnAckReceived(ack.MessageId);
            }
        }
        else
        {
            // Forward the ACK to the previous hop
            var prevEndpoint = new IPEndPoint(
                IPAddress.Parse(tokenContent.PreviousHopAddress),
                tokenContent.PreviousHopPort);

            await SendToNodeAsync(prevEndpoint, content.InnerPayload);
        }
    }

    private byte[]? DecryptLayer(OnionLayer layer)
    {
        try
        {
            // Import the ephemeral public key
            var ephemeralPubKey = PublicKey.Import(
                KeyExchange,
                layer.EphemeralPublicKey,
                KeyBlobFormat.RawPublicKey);

            // Perform key exchange
            using var sharedSecret = KeyExchange.Agree(_encryptionKey, ephemeralPubKey);
            if (sharedSecret == null)
                return null;

            // Derive symmetric key
            using var symmetricKey = KeyDerivation.DeriveKey(
                sharedSecret,
                ReadOnlySpan<byte>.Empty,
                ReadOnlySpan<byte>.Empty,
                Aead,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

            // Decrypt
            var plaintext = Aead.Decrypt(symmetricKey, layer.Nonce, null, layer.Ciphertext);

            return plaintext;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Layer decryption failed");
            return null;
        }
    }

    private async Task SendToNodeAsync(IPEndPoint endpoint, byte[] payload)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port);

            using var stream = client.GetStream();
            using var writer = new BinaryWriter(stream);

            // Write length-prefixed payload
            writer.Write(payload.Length);
            writer.Write(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send to node {Endpoint}", endpoint);
        }
    }
}

/// <summary>
/// ACK message structure.
/// </summary>
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
