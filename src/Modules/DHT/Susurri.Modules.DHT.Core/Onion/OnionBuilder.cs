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

        for (int i = 0; i < path.Count; i++)
        {
            var node = path[i];
            string prevAddress;
            int prevPort;

            if (i == 0)
            {
                prevAddress = "SENDER";
                prevPort = 0;
            }
            else
            {
                var prevNode = path[i - 1];
                prevAddress = prevNode.EndPoint.Address.ToString();
                prevPort = prevNode.EndPoint.Port;
            }

            var tokenContent = new ReplyTokenContent
            {
                PreviousHopAddress = prevAddress,
                PreviousHopPort = prevPort,
                SessionKey = GenerateSessionKey()
            };

            var encryptedToken = EncryptLayer(tokenContent.Serialize(), node.EncryptionPublicKey);

            tokens.Add(new ReplyToken
            {
                NodePublicKey = node.EncryptionPublicKey,
                EncryptedToken = encryptedToken,
                SessionKey = tokenContent.SessionKey
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
}

public sealed class ReplyTokenContent
{
    public string PreviousHopAddress { get; init; } = string.Empty;
    public int PreviousHopPort { get; init; }
    public byte[] SessionKey { get; init; } = Array.Empty<byte>();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(PreviousHopAddress);
        writer.Write((ushort)PreviousHopPort);
        writer.Write((byte)SessionKey.Length);
        writer.Write(SessionKey);

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

        return new ReplyTokenContent
        {
            PreviousHopAddress = prevAddress,
            PreviousHopPort = prevPort,
            SessionKey = sessionKey
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

    public static RecipientPayload Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var msgLen = reader.ReadInt32();
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

    public static ReplyPath Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var pubKeyLen = reader.ReadByte();
        var senderPublicKey = reader.ReadBytes(pubKeyLen);

        var tokenCount = reader.ReadByte();
        var tokens = new List<byte[]>();
        for (int i = 0; i < tokenCount; i++)
        {
            var tokenLen = reader.ReadInt32();
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
    public string Content { get; init; } = string.Empty;
    public long Timestamp { get; init; }
    public Guid MessageId { get; init; } = Guid.NewGuid();

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)SenderPublicKey.Length);
        writer.Write(SenderPublicKey);
        writer.Write(Content);
        writer.Write(Timestamp);
        writer.Write(MessageId.ToByteArray());

        return ms.ToArray();
    }

    public static ChatMessage Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var pubKeyLen = reader.ReadByte();
        var senderPublicKey = reader.ReadBytes(pubKeyLen);
        var content = reader.ReadString();
        var timestamp = reader.ReadInt64();
        var messageId = new Guid(reader.ReadBytes(16));

        return new ChatMessage
        {
            SenderPublicKey = senderPublicKey,
            Content = content,
            Timestamp = timestamp,
            MessageId = messageId
        };
    }
}
