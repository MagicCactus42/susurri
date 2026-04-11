using System.Net;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Onion;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class OnionBuilderTests
{
    private readonly Key _senderKey;
    private readonly byte[] _senderPublicKey;
    private readonly Key _signingKey;
    private readonly byte[] _signingPublicKey;

    public OnionBuilderTests()
    {
        _senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        _senderPublicKey = _senderKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        _signingPublicKey = _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    [Fact]
    public void Build_WithSingleHop_CreatesOnionPacket()
    {
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(1);

        var packet = builder.Build(message, recipientKey, path);

        Assert.NotNull(packet);
        Assert.Equal(path[0], packet.FirstHop);
        Assert.NotEmpty(packet.EncryptedPayload);
        Assert.Single(packet.ReplyTokens);
    }

    [Fact]
    public void Build_WithMultipleHops_CreatesOnionPacket()
    {
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(3);

        var packet = builder.Build(message, recipientKey, path);

        Assert.NotNull(packet);
        Assert.Equal(path[0], packet.FirstHop);
        Assert.NotEmpty(packet.EncryptedPayload);
        Assert.Equal(3, packet.ReplyTokens.Count);
    }

    [Fact]
    public void Build_EmptyPath_ThrowsArgumentException()
    {
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var emptyPath = new List<KademliaNode>();

        Assert.Throws<ArgumentException>(() => builder.Build(message, recipientKey, emptyPath));
    }

    [Fact]
    public void Build_UnsignedMessage_ThrowsArgumentException()
    {
        var builder = new OnionBuilder(_senderKey);
        var unsignedMessage = new ChatMessage
        {
            SenderPublicKey = _senderPublicKey,
            Content = "unsigned",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(1);

        Assert.Throws<ArgumentException>(() => builder.Build(unsignedMessage, recipientKey, path));
    }

    [Fact]
    public void Build_MissingSigningKey_ThrowsArgumentException()
    {
        var builder = new OnionBuilder(_senderKey);
        var message = new ChatMessage
        {
            SenderPublicKey = _senderPublicKey,
            Content = "has signature but no signing key",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Signature = new byte[64]
        };
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(1);

        Assert.Throws<ArgumentException>(() => builder.Build(message, recipientKey, path));
    }

    [Fact]
    public void Build_ReplyTokensHaveValidStructure()
    {
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(3);

        var packet = builder.Build(message, recipientKey, path);

        foreach (var token in packet.ReplyTokens)
        {
            Assert.NotEmpty(token.NodePublicKey);
            Assert.NotEmpty(token.EncryptedToken);
            Assert.NotEmpty(token.SessionKey);
            Assert.Equal(32, token.SessionKey.Length);
        }
    }

    [Fact]
    public void Build_GeneratesUniqueEncryptedPayloadPerBuild()
    {
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(2);

        var packet1 = builder.Build(message, recipientKey, path);
        var packet2 = builder.Build(message, recipientKey, path);

        Assert.NotEqual(packet1.EncryptedPayload, packet2.EncryptedPayload);
    }

    private ChatMessage CreateTestMessage()
    {
        var message = new ChatMessage
        {
            SenderPublicKey = _senderPublicKey,
            SenderSigningPublicKey = _signingPublicKey,
            Content = "Hello, World!",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };
        message.Signature = SignatureAlgorithm.Ed25519.Sign(_signingKey, message.GetSignableData());
        return message;
    }

    private byte[] CreateRandomX25519PublicKey()
    {
        using var key = Key.Create(KeyAgreementAlgorithm.X25519);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    private List<KademliaNode> CreateTestPath(int hops)
    {
        var path = new List<KademliaNode>();
        for (int i = 0; i < hops; i++)
        {
            var pubKey = CreateRandomX25519PublicKey();
            var id = KademliaId.FromPublicKey(pubKey);
            var endpoint = new IPEndPoint(IPAddress.Parse($"192.168.1.{i + 1}"), 8000 + i);
            path.Add(new KademliaNode(id, pubKey, endpoint));
        }
        return path;
    }
}

public class ChatMessageSignatureTests
{
    [Fact]
    public void VerifySignature_ValidSignature_ReturnsTrue()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        var sigPubKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            SenderSigningPublicKey = sigPubKey,
            Content = "test",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        message.Signature = SignatureAlgorithm.Ed25519.Sign(signingKey, message.GetSignableData());

        Assert.True(message.VerifySignature());
    }

    [Fact]
    public void VerifySignature_NoSigningKey_ReturnsFalse()
    {
        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = "unsigned",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        Assert.False(message.VerifySignature());
    }

    [Fact]
    public void VerifySignature_NoSignature_ReturnsFalse()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            SenderSigningPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            Content = "no signature field set",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        Assert.False(message.VerifySignature());
    }

    [Fact]
    public void VerifySignature_WrongKey_ReturnsFalse()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        using var wrongKey = Key.Create(SignatureAlgorithm.Ed25519);

        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            SenderSigningPublicKey = wrongKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            Content = "test",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        message.Signature = SignatureAlgorithm.Ed25519.Sign(signingKey, message.GetSignableData());

        Assert.False(message.VerifySignature());
    }

    [Fact]
    public void VerifySignature_TamperedContent_ReturnsFalse()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        var sigPubKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            SenderSigningPublicKey = sigPubKey,
            Content = "original",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        message.Signature = SignatureAlgorithm.Ed25519.Sign(signingKey, message.GetSignableData());

        var tampered = new ChatMessage
        {
            SenderPublicKey = message.SenderPublicKey,
            SenderSigningPublicKey = message.SenderSigningPublicKey,
            Content = "tampered",
            Timestamp = message.Timestamp,
            MessageId = message.MessageId,
            Signature = message.Signature
        };

        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void VerifySignature_StrippedSignature_ReturnsFalse()
    {
        using var signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        var sigPubKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var message = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            SenderSigningPublicKey = sigPubKey,
            Content = "signed message",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        message.Signature = SignatureAlgorithm.Ed25519.Sign(signingKey, message.GetSignableData());

        var stripped = new ChatMessage
        {
            SenderPublicKey = message.SenderPublicKey,
            SenderSigningPublicKey = Array.Empty<byte>(),
            Content = message.Content,
            Timestamp = message.Timestamp,
            MessageId = message.MessageId,
            Signature = Array.Empty<byte>()
        };

        Assert.False(stripped.VerifySignature());
    }
}

public class ChatMessageTests
{
    [Fact]
    public void ChatMessage_RoundTrip_PreservesAllFields()
    {
        var senderPubKey = new byte[32];
        Random.Shared.NextBytes(senderPubKey);

        var original = new ChatMessage
        {
            SenderPublicKey = senderPubKey,
            Content = "Hello, this is a test message!",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal(original.SenderPublicKey, deserialized.SenderPublicKey);
        Assert.Equal(original.Content, deserialized.Content);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
        Assert.Equal(original.MessageId, deserialized.MessageId);
    }

    [Fact]
    public void ChatMessage_EmptyContent_RoundTrip()
    {
        var original = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = string.Empty,
            Timestamp = 0
        };

        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal(string.Empty, deserialized.Content);
    }

    [Fact]
    public void ChatMessage_UnicodeContent_RoundTrip()
    {
        var original = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = "Hello, 世界! 🌍 مرحبا",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal("Hello, 世界! 🌍 مرحبا", deserialized.Content);
    }

    [Fact]
    public void ChatMessage_LongContent_RoundTrip()
    {
        var longContent = new string('A', 10000);
        var original = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = longContent,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal(longContent, deserialized.Content);
    }
}

public class ReplyPathTests
{
    [Fact]
    public void ReplyPath_RoundTrip_PreservesAllFields()
    {
        var senderPubKey = new byte[32];
        Random.Shared.NextBytes(senderPubKey);

        var tokens = new List<byte[]>
        {
            new byte[] { 0x01, 0x02, 0x03 },
            new byte[] { 0x04, 0x05, 0x06 },
            new byte[] { 0x07, 0x08, 0x09 }
        };

        var original = new ReplyPath
        {
            SenderPublicKey = senderPubKey,
            FirstHopAddress = "203.0.113.50",
            FirstHopPort = 9001,
            Tokens = tokens
        };

        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        Assert.Equal(senderPubKey, deserialized.SenderPublicKey);
        Assert.Equal("203.0.113.50", deserialized.FirstHopAddress);
        Assert.Equal(9001, deserialized.FirstHopPort);
        Assert.Equal(3, deserialized.Tokens.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(tokens[i], deserialized.Tokens[i]);
        }
    }

    [Fact]
    public void ReplyPath_EmptyTokens_RoundTrip()
    {
        var original = new ReplyPath
        {
            SenderPublicKey = new byte[32],
            FirstHopAddress = "10.0.0.1",
            FirstHopPort = 8080,
            Tokens = new List<byte[]>()
        };

        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        Assert.Empty(deserialized.Tokens);
        Assert.Equal("10.0.0.1", deserialized.FirstHopAddress);
        Assert.Equal(8080, deserialized.FirstHopPort);
    }

    [Fact]
    public void ReplyPath_LargeTokens_RoundTrip()
    {
        var largeToken = new byte[1000];
        Random.Shared.NextBytes(largeToken);

        var original = new ReplyPath
        {
            SenderPublicKey = new byte[32],
            FirstHopAddress = "192.168.1.100",
            FirstHopPort = 3000,
            Tokens = new List<byte[]> { largeToken }
        };

        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        Assert.Single(deserialized.Tokens);
        Assert.Equal(largeToken, deserialized.Tokens[0]);
        Assert.Equal("192.168.1.100", deserialized.FirstHopAddress);
    }

    [Fact]
    public void ReplyPath_IPv6FirstHop_RoundTrip()
    {
        var original = new ReplyPath
        {
            SenderPublicKey = new byte[32],
            FirstHopAddress = "2001:db8::1",
            FirstHopPort = 5000,
            Tokens = new List<byte[]> { new byte[] { 0xAA } }
        };

        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        Assert.Equal("2001:db8::1", deserialized.FirstHopAddress);
        Assert.Equal(5000, deserialized.FirstHopPort);
    }

    [Fact]
    public void ReplyPath_EmptyFirstHop_RoundTrip()
    {
        var original = new ReplyPath
        {
            SenderPublicKey = new byte[32],
            Tokens = new List<byte[]>()
        };

        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        Assert.Equal(string.Empty, deserialized.FirstHopAddress);
        Assert.Equal(0, deserialized.FirstHopPort);
    }
}

public class RecipientPayloadTests
{
    [Fact]
    public void RecipientPayload_RoundTrip_PreservesAllFields()
    {
        var message = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var replyPath = new ReplyPath
        {
            SenderPublicKey = new byte[32],
            Tokens = new List<byte[]> { new byte[] { 0xAA, 0xBB } }
        };

        var original = new RecipientPayload
        {
            Message = message,
            ReplyPath = replyPath
        };

        var serialized = original.Serialize();
        var deserialized = RecipientPayload.Deserialize(serialized);

        Assert.Equal(message, deserialized.Message);
        Assert.Equal(replyPath.SenderPublicKey, deserialized.ReplyPath.SenderPublicKey);
        Assert.Single(deserialized.ReplyPath.Tokens);
    }

    [Fact]
    public void RecipientPayload_EmptyMessage_RoundTrip()
    {
        var original = new RecipientPayload
        {
            Message = Array.Empty<byte>(),
            ReplyPath = new ReplyPath()
        };

        var serialized = original.Serialize();
        var deserialized = RecipientPayload.Deserialize(serialized);

        Assert.Empty(deserialized.Message);
    }
}

public class ReplyTokenContentTests
{
    [Fact]
    public void ReplyTokenContent_RoundTrip_PreservesAllFields()
    {
        var sessionKey = new byte[32];
        Random.Shared.NextBytes(sessionKey);

        var original = new ReplyTokenContent
        {
            PreviousHopAddress = "192.168.1.100",
            PreviousHopPort = 8080,
            SessionKey = sessionKey
        };

        var serialized = original.Serialize();
        var deserialized = ReplyTokenContent.Deserialize(serialized);

        Assert.Equal("192.168.1.100", deserialized.PreviousHopAddress);
        Assert.Equal(8080, deserialized.PreviousHopPort);
        Assert.Equal(sessionKey, deserialized.SessionKey);
    }

    [Fact]
    public void ReplyTokenContent_SenderAddress_RoundTrip()
    {
        var original = new ReplyTokenContent
        {
            PreviousHopAddress = "SENDER",
            PreviousHopPort = 0,
            SessionKey = new byte[32]
        };

        var serialized = original.Serialize();
        var deserialized = ReplyTokenContent.Deserialize(serialized);

        Assert.Equal("SENDER", deserialized.PreviousHopAddress);
        Assert.Equal(0, deserialized.PreviousHopPort);
    }

    [Fact]
    public void ReplyTokenContent_IPv6Address_RoundTrip()
    {
        var original = new ReplyTokenContent
        {
            PreviousHopAddress = "2001:db8::1",
            PreviousHopPort = 3000,
            SessionKey = new byte[32]
        };

        var serialized = original.Serialize();
        var deserialized = ReplyTokenContent.Deserialize(serialized);

        Assert.Equal("2001:db8::1", deserialized.PreviousHopAddress);
    }
}

public class OnionPacketTests
{
    [Fact]
    public void OnionPacket_Properties_CanBeSet()
    {
        var pubKey = new byte[32];
        Random.Shared.NextBytes(pubKey);
        var id = KademliaId.FromPublicKey(pubKey);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 8080);
        var firstHop = new KademliaNode(id, pubKey, endpoint);

        var payload = new byte[100];
        Random.Shared.NextBytes(payload);

        var tokens = new List<ReplyToken>
        {
            new ReplyToken
            {
                NodePublicKey = pubKey,
                EncryptedToken = new byte[50],
                SessionKey = new byte[32]
            }
        };

        var packet = new OnionPacket
        {
            FirstHop = firstHop,
            EncryptedPayload = payload,
            ReplyTokens = tokens
        };

        Assert.Equal(firstHop, packet.FirstHop);
        Assert.Equal(payload, packet.EncryptedPayload);
        Assert.Single(packet.ReplyTokens);
    }
}

public class ReplyTokenTests
{
    [Fact]
    public void ReplyToken_Properties_CanBeSet()
    {
        var nodePubKey = new byte[32];
        var encryptedToken = new byte[100];
        var sessionKey = new byte[32];

        Random.Shared.NextBytes(nodePubKey);
        Random.Shared.NextBytes(encryptedToken);
        Random.Shared.NextBytes(sessionKey);

        var token = new ReplyToken
        {
            NodePublicKey = nodePubKey,
            EncryptedToken = encryptedToken,
            SessionKey = sessionKey
        };

        Assert.Equal(nodePubKey, token.NodePublicKey);
        Assert.Equal(encryptedToken, token.EncryptedToken);
        Assert.Equal(sessionKey, token.SessionKey);
    }

    [Fact]
    public void ReplyToken_DefaultValues_AreEmptyArrays()
    {
        var token = new ReplyToken();

        Assert.Empty(token.NodePublicKey);
        Assert.Empty(token.EncryptedToken);
        Assert.Empty(token.SessionKey);
    }
}
