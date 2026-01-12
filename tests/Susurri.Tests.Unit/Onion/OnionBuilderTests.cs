using System.Net;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Onion;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

/// <summary>
/// Unit tests for OnionBuilder and related data structures.
/// </summary>
public class OnionBuilderTests
{
    private readonly Key _senderKey;
    private readonly byte[] _senderPublicKey;

    public OnionBuilderTests()
    {
        _senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        _senderPublicKey = _senderKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    [Fact]
    public void Build_WithSingleHop_CreatesOnionPacket()
    {
        // Arrange
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(1);

        // Act
        var packet = builder.Build(message, recipientKey, path);

        // Assert
        Assert.NotNull(packet);
        Assert.Equal(path[0], packet.FirstHop);
        Assert.NotEmpty(packet.EncryptedPayload);
        Assert.Single(packet.ReplyTokens);
    }

    [Fact]
    public void Build_WithMultipleHops_CreatesOnionPacket()
    {
        // Arrange
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(3);

        // Act
        var packet = builder.Build(message, recipientKey, path);

        // Assert
        Assert.NotNull(packet);
        Assert.Equal(path[0], packet.FirstHop);
        Assert.NotEmpty(packet.EncryptedPayload);
        Assert.Equal(3, packet.ReplyTokens.Count);
    }

    [Fact]
    public void Build_EmptyPath_ThrowsArgumentException()
    {
        // Arrange
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var emptyPath = new List<KademliaNode>();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => builder.Build(message, recipientKey, emptyPath));
    }

    [Fact]
    public void Build_ReplyTokensHaveValidStructure()
    {
        // Arrange
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(3);

        // Act
        var packet = builder.Build(message, recipientKey, path);

        // Assert
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
        // Arrange
        var builder = new OnionBuilder(_senderKey);
        var message = CreateTestMessage();
        var recipientKey = CreateRandomX25519PublicKey();
        var path = CreateTestPath(2);

        // Act
        var packet1 = builder.Build(message, recipientKey, path);
        var packet2 = builder.Build(message, recipientKey, path);

        // Assert - Different ephemeral keys should produce different payloads
        Assert.NotEqual(packet1.EncryptedPayload, packet2.EncryptedPayload);
    }

    private ChatMessage CreateTestMessage()
    {
        return new ChatMessage
        {
            SenderPublicKey = _senderPublicKey,
            Content = "Hello, World!",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };
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

/// <summary>
/// Unit tests for ChatMessage serialization.
/// </summary>
public class ChatMessageTests
{
    [Fact]
    public void ChatMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var senderPubKey = new byte[32];
        Random.Shared.NextBytes(senderPubKey);

        var original = new ChatMessage
        {
            SenderPublicKey = senderPubKey,
            Content = "Hello, this is a test message!",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        // Assert
        Assert.Equal(original.SenderPublicKey, deserialized.SenderPublicKey);
        Assert.Equal(original.Content, deserialized.Content);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
        Assert.Equal(original.MessageId, deserialized.MessageId);
    }

    [Fact]
    public void ChatMessage_EmptyContent_RoundTrip()
    {
        // Arrange
        var original = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = string.Empty,
            Timestamp = 0
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        // Assert
        Assert.Equal(string.Empty, deserialized.Content);
    }

    [Fact]
    public void ChatMessage_UnicodeContent_RoundTrip()
    {
        // Arrange
        var original = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = "Hello, 世界! 🌍 مرحبا",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        // Assert
        Assert.Equal("Hello, 世界! 🌍 مرحبا", deserialized.Content);
    }

    [Fact]
    public void ChatMessage_LongContent_RoundTrip()
    {
        // Arrange
        var longContent = new string('A', 10000);
        var original = new ChatMessage
        {
            SenderPublicKey = new byte[32],
            Content = longContent,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ChatMessage.Deserialize(serialized);

        // Assert
        Assert.Equal(longContent, deserialized.Content);
    }
}

/// <summary>
/// Unit tests for ReplyPath serialization.
/// </summary>
public class ReplyPathTests
{
    [Fact]
    public void ReplyPath_RoundTrip_PreservesAllFields()
    {
        // Arrange
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
            Tokens = tokens
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        // Assert
        Assert.Equal(senderPubKey, deserialized.SenderPublicKey);
        Assert.Equal(3, deserialized.Tokens.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(tokens[i], deserialized.Tokens[i]);
        }
    }

    [Fact]
    public void ReplyPath_EmptyTokens_RoundTrip()
    {
        // Arrange
        var original = new ReplyPath
        {
            SenderPublicKey = new byte[32],
            Tokens = new List<byte[]>()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        // Assert
        Assert.Empty(deserialized.Tokens);
    }

    [Fact]
    public void ReplyPath_LargeTokens_RoundTrip()
    {
        // Arrange
        var largeToken = new byte[1000];
        Random.Shared.NextBytes(largeToken);

        var original = new ReplyPath
        {
            SenderPublicKey = new byte[32],
            Tokens = new List<byte[]> { largeToken }
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ReplyPath.Deserialize(serialized);

        // Assert
        Assert.Single(deserialized.Tokens);
        Assert.Equal(largeToken, deserialized.Tokens[0]);
    }
}

/// <summary>
/// Unit tests for RecipientPayload serialization.
/// </summary>
public class RecipientPayloadTests
{
    [Fact]
    public void RecipientPayload_RoundTrip_PreservesAllFields()
    {
        // Arrange
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

        // Act
        var serialized = original.Serialize();
        var deserialized = RecipientPayload.Deserialize(serialized);

        // Assert
        Assert.Equal(message, deserialized.Message);
        Assert.Equal(replyPath.SenderPublicKey, deserialized.ReplyPath.SenderPublicKey);
        Assert.Single(deserialized.ReplyPath.Tokens);
    }

    [Fact]
    public void RecipientPayload_EmptyMessage_RoundTrip()
    {
        // Arrange
        var original = new RecipientPayload
        {
            Message = Array.Empty<byte>(),
            ReplyPath = new ReplyPath()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RecipientPayload.Deserialize(serialized);

        // Assert
        Assert.Empty(deserialized.Message);
    }
}

/// <summary>
/// Unit tests for ReplyTokenContent serialization.
/// </summary>
public class ReplyTokenContentTests
{
    [Fact]
    public void ReplyTokenContent_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var sessionKey = new byte[32];
        Random.Shared.NextBytes(sessionKey);

        var original = new ReplyTokenContent
        {
            PreviousHopAddress = "192.168.1.100",
            PreviousHopPort = 8080,
            SessionKey = sessionKey
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ReplyTokenContent.Deserialize(serialized);

        // Assert
        Assert.Equal("192.168.1.100", deserialized.PreviousHopAddress);
        Assert.Equal(8080, deserialized.PreviousHopPort);
        Assert.Equal(sessionKey, deserialized.SessionKey);
    }

    [Fact]
    public void ReplyTokenContent_SenderAddress_RoundTrip()
    {
        // Arrange
        var original = new ReplyTokenContent
        {
            PreviousHopAddress = "SENDER",
            PreviousHopPort = 0,
            SessionKey = new byte[32]
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ReplyTokenContent.Deserialize(serialized);

        // Assert
        Assert.Equal("SENDER", deserialized.PreviousHopAddress);
        Assert.Equal(0, deserialized.PreviousHopPort);
    }

    [Fact]
    public void ReplyTokenContent_IPv6Address_RoundTrip()
    {
        // Arrange
        var original = new ReplyTokenContent
        {
            PreviousHopAddress = "2001:db8::1",
            PreviousHopPort = 3000,
            SessionKey = new byte[32]
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = ReplyTokenContent.Deserialize(serialized);

        // Assert
        Assert.Equal("2001:db8::1", deserialized.PreviousHopAddress);
    }
}

/// <summary>
/// Unit tests for OnionPacket.
/// </summary>
public class OnionPacketTests
{
    [Fact]
    public void OnionPacket_Properties_CanBeSet()
    {
        // Arrange
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

        // Act
        var packet = new OnionPacket
        {
            FirstHop = firstHop,
            EncryptedPayload = payload,
            ReplyTokens = tokens
        };

        // Assert
        Assert.Equal(firstHop, packet.FirstHop);
        Assert.Equal(payload, packet.EncryptedPayload);
        Assert.Single(packet.ReplyTokens);
    }
}

/// <summary>
/// Unit tests for ReplyToken.
/// </summary>
public class ReplyTokenTests
{
    [Fact]
    public void ReplyToken_Properties_CanBeSet()
    {
        // Arrange
        var nodePubKey = new byte[32];
        var encryptedToken = new byte[100];
        var sessionKey = new byte[32];

        Random.Shared.NextBytes(nodePubKey);
        Random.Shared.NextBytes(encryptedToken);
        Random.Shared.NextBytes(sessionKey);

        // Act
        var token = new ReplyToken
        {
            NodePublicKey = nodePubKey,
            EncryptedToken = encryptedToken,
            SessionKey = sessionKey
        };

        // Assert
        Assert.Equal(nodePubKey, token.NodePublicKey);
        Assert.Equal(encryptedToken, token.EncryptedToken);
        Assert.Equal(sessionKey, token.SessionKey);
    }

    [Fact]
    public void ReplyToken_DefaultValues_AreEmptyArrays()
    {
        // Arrange & Act
        var token = new ReplyToken();

        // Assert
        Assert.Empty(token.NodePublicKey);
        Assert.Empty(token.EncryptedToken);
        Assert.Empty(token.SessionKey);
    }
}
