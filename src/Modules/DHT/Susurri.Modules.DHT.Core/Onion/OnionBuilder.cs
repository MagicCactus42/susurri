using System.Security.Cryptography;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.Modules.DHT.Core.Onion;

public sealed class OnionBuilder
{
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;
    private static readonly KeyAgreementAlgorithm KeyExchange = KeyAgreementAlgorithm.X25519;
    private static readonly KeyDerivationAlgorithm KeyDerivation = KeyDerivationAlgorithm.HkdfSha256;

    private readonly Key _senderEncryptionKey;
    private readonly byte[] _senderPublicKey;

    public OnionBuilder(Key senderEncryptionKey)
    {
        _senderEncryptionKey = senderEncryptionKey;
        _senderPublicKey = senderEncryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public OnionPacket Build(ChatMessage message, byte[] recipientPublicKey, IReadOnlyList<KademliaNode> path)
    {
        if (path.Count < 1)
            throw new ArgumentException("Path must have at least one relay node", nameof(path));

        var messageBytes = message.Serialize();
        var paddedMessage = MessagePadding.Pad(messageBytes);
        var replyTokens = GenerateReplyTokens(path);

        // Build onion from inside out: first encrypt for recipient, then wrap for each relay
        var recipientLayer = BuildRecipientLayer(paddedMessage, recipientPublicKey, replyTokens);
        var currentPayload = recipientLayer;

        for (int i = path.Count - 1; i >= 0; i--)
        {
            var node = path[i];
            var isLastHop = (i == path.Count - 1);

            OnionLayerContent content;
            if (isLastHop)
            {
                content = new OnionLayerContent
                {
                    Type = OnionLayerType.FinalHop,
                    RecipientPublicKey = recipientPublicKey,
                    ReplyToken = replyTokens[i].EncryptedToken,
                    InnerPayload = currentPayload
                };
            }
            else
            {
                var nextNode = path[i + 1];
                content = new OnionLayerContent
                {
                    Type = OnionLayerType.Relay,
                    NextHopAddress = nextNode.EndPoint.Address.ToString(),
                    NextHopPort = nextNode.EndPoint.Port,
                    ReplyToken = replyTokens[i].EncryptedToken,
                    InnerPayload = currentPayload
                };
            }

            currentPayload = EncryptLayer(content.Serialize(), node.EncryptionPublicKey);
        }

        return new OnionPacket
        {
            FirstHop = path[0],
            EncryptedPayload = currentPayload,
            ReplyTokens = replyTokens
        };
    }

    private byte[] BuildRecipientLayer(byte[] message, byte[] recipientPublicKey, IReadOnlyList<ReplyToken> replyTokens)
    {
        var replyPath = new ReplyPath
        {
            SenderPublicKey = _senderPublicKey,
            Tokens = replyTokens.Select(t => t.EncryptedToken).ToList()
        };

        var recipientPayload = new RecipientPayload
        {
            Message = message,
            ReplyPath = replyPath
        };

        return EncryptLayer(recipientPayload.Serialize(), recipientPublicKey);
    }

    // Each reply token is encrypted for one relay node, containing previous hop info for return path
    private List<ReplyToken> GenerateReplyTokens(IReadOnlyList<KademliaNode> path)
    {
        var tokens = new List<ReplyToken>();

        // Generate a cryptographic nonce that identifies "this is the sender's token"
        // instead of using plaintext "SENDER"
        var senderMarkerNonce = new byte[32];
        RandomNumberGenerator.Fill(senderMarkerNonce);

        for (int i = 0; i < path.Count; i++)
        {
            var node = path[i];

            ReplyTokenContent tokenContent;
            if (i == 0)
            {
                tokenContent = new ReplyTokenContent
                {
                    PreviousHopAddress = string.Empty,
                    PreviousHopPort = 0,
                    SessionKey = GenerateSessionKey(),
                    SenderMarker = senderMarkerNonce
                };
            }
            else
            {
                var prevNode = path[i - 1];
                tokenContent = new ReplyTokenContent
                {
                    PreviousHopAddress = prevNode.EndPoint.Address.ToString(),
                    PreviousHopPort = prevNode.EndPoint.Port,
                    SessionKey = GenerateSessionKey()
                };
            }

            var encryptedToken = EncryptLayer(tokenContent.Serialize(), node.EncryptionPublicKey);

            tokens.Add(new ReplyToken
            {
                NodePublicKey = node.EncryptionPublicKey,
                EncryptedToken = encryptedToken,
                SessionKey = tokenContent.SessionKey,
                SenderMarkerNonce = i == 0 ? senderMarkerNonce : null
            });
        }

        return tokens;
    }

    // X25519 key exchange + ChaCha20-Poly1305 encryption for each onion layer
    private byte[] EncryptLayer(byte[] plaintext, byte[] recipientPublicKey)
    {
        using var ephemeralKey = Key.Create(KeyExchange);
        var ephemeralPubKeyBytes = ephemeralKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var recipientPubKey = PublicKey.Import(KeyExchange, recipientPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = KeyExchange.Agree(ephemeralKey, recipientPubKey);
        if (sharedSecret == null)
            throw new CryptographicException("Key agreement failed");

        using var symmetricKey = KeyDerivation.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var nonce = new byte[Aead.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = Aead.Encrypt(symmetricKey, nonce, null, plaintext);

        var layer = new OnionLayer
        {
            EphemeralPublicKey = ephemeralPubKeyBytes,
            Nonce = nonce,
            Ciphertext = ciphertext
        };

        return layer.Serialize();
    }

    private static byte[] GenerateSessionKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
}

public sealed class OnionPacket
{
    public KademliaNode FirstHop { get; init; } = null!;
    public byte[] EncryptedPayload { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<ReplyToken> ReplyTokens { get; init; } = Array.Empty<ReplyToken>();
}

public sealed class ReplyToken
{
    public byte[] NodePublicKey { get; init; } = Array.Empty<byte>();
    public byte[] EncryptedToken { get; init; } = Array.Empty<byte>();
    public byte[] SessionKey { get; init; } = Array.Empty<byte>();
    /// <summary>
    /// Only set for the first token (sender's relay). The sender keeps this
    /// to recognize their own ACK token upon receipt.
    /// </summary>
    public byte[]? SenderMarkerNonce { get; init; }
}

public sealed class ReplyTokenContent
{
    public string PreviousHopAddress { get; init; } = string.Empty;
    public int PreviousHopPort { get; init; }
    public byte[] SessionKey { get; init; } = Array.Empty<byte>();
    /// <summary>
    /// A cryptographic nonce that identifies the sender's own token.
    /// Non-empty only for the first hop (the sender's direct relay).
    /// Replaces the old plaintext "SENDER" sentinel.
    /// </summary>
    public byte[] SenderMarker { get; init; } = Array.Empty<byte>();

    public bool IsSenderToken => SenderMarker.Length > 0 && string.IsNullOrEmpty(PreviousHopAddress);

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(PreviousHopAddress);
        writer.Write((ushort)PreviousHopPort);
        writer.Write((byte)SessionKey.Length);
        writer.Write(SessionKey);
        writer.Write((byte)SenderMarker.Length);
        writer.Write(SenderMarker);

        return ms.ToArray();
    }

    public static ReplyTokenContent Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var prevAddress = reader.ReadString();
        var prevPort = reader.ReadUInt16();
        var keyLen = reader.ReadByte();
        var sessionKey = reader.ReadBytes(keyLen);
        byte[] senderMarker = Array.Empty<byte>();
        if (ms.Position < ms.Length)
        {
            var markerLen = reader.ReadByte();
            senderMarker = reader.ReadBytes(markerLen);
        }

        return new ReplyTokenContent
        {
            PreviousHopAddress = prevAddress,
            PreviousHopPort = prevPort,
            SessionKey = sessionKey,
            SenderMarker = senderMarker
        };
    }
}

public sealed class RecipientPayload
{
    public byte[] Message { get; init; } = Array.Empty<byte>();
    public ReplyPath ReplyPath { get; init; } = new();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Message.Length);
        writer.Write(Message);
        writer.Write(ReplyPath.Serialize());

        return ms.ToArray();
    }

    private const int MaxPayloadSize = 256 * 1024; // 256 KB

    public static RecipientPayload Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var msgLen = reader.ReadInt32();
        if (msgLen < 0 || msgLen > MaxPayloadSize)
            throw new InvalidDataException($"Invalid recipient payload size: {msgLen}");
        var message = reader.ReadBytes(msgLen);

        var remaining = (int)(ms.Length - ms.Position);
        var replyPathData = reader.ReadBytes(remaining);
        var replyPath = ReplyPath.Deserialize(replyPathData);

        return new RecipientPayload
        {
            Message = message,
            ReplyPath = replyPath
        };
    }
}

public sealed class ReplyPath
{
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public List<byte[]> Tokens { get; init; } = new();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write((byte)Tokens.Count);
        foreach (var token in Tokens)
        {
            writer.Write(token.Length);
            writer.Write(token);
        }

        return ms.ToArray();
    }

    private const int MaxTokens = 20;
    private const int MaxTokenSize = 64 * 1024; // 64 KB per token

    public static ReplyPath Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var pubKeyLen = reader.ReadByte();
        var senderPublicKey = reader.ReadBytes(pubKeyLen);

        var tokenCount = reader.ReadByte();
        if (tokenCount > MaxTokens)
            throw new InvalidDataException($"Too many reply tokens: {tokenCount}");

        var tokens = new List<byte[]>(tokenCount);
        for (int i = 0; i < tokenCount; i++)
        {
            var tokenLen = reader.ReadInt32();
            if (tokenLen < 0 || tokenLen > MaxTokenSize)
                throw new InvalidDataException($"Invalid reply token size: {tokenLen}");
            tokens.Add(reader.ReadBytes(tokenLen));
        }

        return new ReplyPath
        {
            SenderPublicKey = senderPublicKey,
            Tokens = tokens
        };
    }
}

public sealed class ChatMessage
{
    public byte[] SenderPublicKey { get; init; } = Array.Empty<byte>();
    public byte[] SenderSigningPublicKey { get; init; } = Array.Empty<byte>();
    public string Content { get; init; } = string.Empty;
    public long Timestamp { get; init; }
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public byte[] Signature { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Returns the signable portion of the message (everything except the signature itself).
    /// </summary>
    public byte[] GetSignableData()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write((byte)SenderSigningPublicKey.Length);
        writer.Write(SenderSigningPublicKey);
        writer.Write(Content);
        writer.Write(Timestamp);
        writer.Write(MessageId.ToByteArray());

        return ms.ToArray();
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write((byte)SenderSigningPublicKey.Length);
        writer.Write(SenderSigningPublicKey);
        writer.Write(Content);
        writer.Write(Timestamp);
        writer.Write(MessageId.ToByteArray());
        writer.Write((ushort)Signature.Length);
        writer.Write(Signature);

        return ms.ToArray();
    }

    public static ChatMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var pubKeyLen = reader.ReadByte();
        var senderPublicKey = reader.ReadBytes(pubKeyLen);
        var sigPubKeyLen = reader.ReadByte();
        var senderSigningPublicKey = reader.ReadBytes(sigPubKeyLen);
        var content = reader.ReadString();
        var timestamp = reader.ReadInt64();
        var messageId = new Guid(reader.ReadBytes(16));
        var sigLen = reader.ReadUInt16();
        var signature = reader.ReadBytes(sigLen);

        return new ChatMessage
        {
            SenderPublicKey = senderPublicKey,
            SenderSigningPublicKey = senderSigningPublicKey,
            Content = content,
            Timestamp = timestamp,
            MessageId = messageId,
            Signature = signature
        };
    }

    /// <summary>
    /// Verifies the Ed25519 signature on this message.
    /// Returns true if signature is valid, false otherwise.
    /// Returns true if no signing key is provided (backwards compat).
    /// </summary>
    public bool VerifySignature()
    {
        if (SenderSigningPublicKey.Length == 0 || Signature.Length == 0)
            return true; // No signing key = unverifiable, allow for backward compat

        try
        {
            var signingPubKey = NSec.Cryptography.PublicKey.Import(
                NSec.Cryptography.SignatureAlgorithm.Ed25519,
                SenderSigningPublicKey,
                NSec.Cryptography.KeyBlobFormat.RawPublicKey);

            return NSec.Cryptography.SignatureAlgorithm.Ed25519.Verify(
                signingPubKey, GetSignableData(), Signature);
        }
        catch
        {
            return false;
        }
    }
}
