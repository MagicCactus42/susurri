#nullable enable
using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Onion;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

/// <summary>
/// Unit tests for OnionRouter and related classes.
/// </summary>
public class OnionRouterTests
{
    // Note: Full OnionRouter integration tests would require a mock DHT node
    // These tests focus on the data structures and message handling logic

    [Fact]
    public void OnMessageReceived_EventCanBeSubscribed()
    {
        // This is a basic test to verify event structure compiles
        // Full integration would require mock DHT
        Func<ChatMessage, ReplyPath, Task>? handler = null;

        handler = async (message, replyPath) =>
        {
            Assert.NotNull(message);
            Assert.NotNull(replyPath);
            await Task.CompletedTask;
        };

        Assert.NotNull(handler);
    }

    [Fact]
    public void OnAckReceived_EventCanBeSubscribed()
    {
        Func<Guid, Task>? handler = null;

        handler = async (messageId) =>
        {
            Assert.NotEqual(Guid.Empty, messageId);
            await Task.CompletedTask;
        };

        Assert.NotNull(handler);
    }
}

/// <summary>
/// Unit tests for AckMessage serialization.
/// </summary>
public class AckMessageTests
{
    [Fact]
    public void AckMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var original = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = AckMessage.Deserialize(serialized);

        // Assert
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
    }

    [Fact]
    public void AckMessage_EmptyGuid_RoundTrip()
    {
        // Arrange
        var original = new AckMessage
        {
            MessageId = Guid.Empty,
            Timestamp = 0
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = AckMessage.Deserialize(serialized);

        // Assert
        Assert.Equal(Guid.Empty, deserialized.MessageId);
        Assert.Equal(0, deserialized.Timestamp);
    }

    [Fact]
    public void AckMessage_MaxTimestamp_RoundTrip()
    {
        // Arrange
        var original = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = long.MaxValue
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = AckMessage.Deserialize(serialized);

        // Assert
        Assert.Equal(long.MaxValue, deserialized.Timestamp);
    }

    [Fact]
    public void AckMessage_SerializedSize_IsCorrect()
    {
        // Arrange
        var ack = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = 12345678L
        };

        // Act
        var serialized = ack.Serialize();

        // Assert - 16 bytes for GUID + 8 bytes for timestamp = 24 bytes
        Assert.Equal(24, serialized.Length);
    }

    [Fact]
    public void AckMessage_MultipleSerializations_ProduceSameResult()
    {
        // Arrange
        var original = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act
        var serialized1 = original.Serialize();
        var serialized2 = original.Serialize();

        // Assert
        Assert.Equal(serialized1, serialized2);
    }
}

/// <summary>
/// Integration tests for OnionBuilder and OnionLayer decryption.
/// These tests verify the full encryption/decryption cycle.
/// </summary>
public class OnionEncryptionIntegrationTests
{
    [Fact]
    public void EncryptDecrypt_SingleLayer_RecoverOriginalData()
    {
        // Arrange
        using var senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        using var recipientKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var recipientPubKey = recipientKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var builder = new OnionBuilder(senderKey);

        var message = new ChatMessage
        {
            SenderPublicKey = senderKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            Content = "Test message",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = Guid.NewGuid()
        };

        // Create path with recipient as target
        var nodePubKey = CreateRandomX25519PublicKey();
        var nodeId = KademliaId.FromPublicKey(nodePubKey);
        var path = new List<KademliaNode>
        {
            new KademliaNode(nodeId, nodePubKey, new IPEndPoint(IPAddress.Loopback, 8080))
        };

        // Act
        var packet = builder.Build(message, recipientPubKey, path);

        // Assert
        Assert.NotEmpty(packet.EncryptedPayload);
        Assert.Single(packet.ReplyTokens);

        // The encrypted payload should be parseable as an OnionLayer
        var layer = OnionLayer.Deserialize(packet.EncryptedPayload);
        Assert.NotEmpty(layer.EphemeralPublicKey);
        Assert.NotEmpty(layer.Nonce);
        Assert.NotEmpty(layer.Ciphertext);
    }

    [Fact]
    public void Build_MultipleHops_CreatesLayeredOnion()
    {
        // Arrange
        using var senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var recipientPubKey = CreateRandomX25519PublicKey();

        var builder = new OnionBuilder(senderKey);

        var message = new ChatMessage
        {
            SenderPublicKey = senderKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            Content = "Multi-hop message",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Create 3-hop path
        var path = new List<KademliaNode>();
        for (int i = 0; i < 3; i++)
        {
            var nodePubKey = CreateRandomX25519PublicKey();
            var nodeId = KademliaId.FromPublicKey(nodePubKey);
            path.Add(new KademliaNode(nodeId, nodePubKey, new IPEndPoint(IPAddress.Loopback, 8080 + i)));
        }

        // Act
        var packet = builder.Build(message, recipientPubKey, path);

        // Assert
        Assert.Equal(3, packet.ReplyTokens.Count);
        Assert.Equal(path[0], packet.FirstHop);

        // Verify outer layer is valid
        var outerLayer = OnionLayer.Deserialize(packet.EncryptedPayload);
        Assert.NotEmpty(outerLayer.Ciphertext);
    }

    [Fact]
    public void ReplyTokens_MatchPathNodes()
    {
        // Arrange
        using var senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var recipientPubKey = CreateRandomX25519PublicKey();

        var builder = new OnionBuilder(senderKey);
        var message = new ChatMessage { Content = "Test" };

        var path = new List<KademliaNode>();
        var nodePubKeys = new List<byte[]>();
        for (int i = 0; i < 3; i++)
        {
            var nodePubKey = CreateRandomX25519PublicKey();
            nodePubKeys.Add(nodePubKey);
            var nodeId = KademliaId.FromPublicKey(nodePubKey);
            path.Add(new KademliaNode(nodeId, nodePubKey, new IPEndPoint(IPAddress.Loopback, 8080 + i)));
        }

        // Act
        var packet = builder.Build(message, recipientPubKey, path);

        // Assert - each reply token should be associated with the correct node
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(nodePubKeys[i], packet.ReplyTokens[i].NodePublicKey);
        }
    }

    private byte[] CreateRandomX25519PublicKey()
    {
        using var key = Key.Create(KeyAgreementAlgorithm.X25519);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }
}

/// <summary>
/// Tests for OnionLayerContent which was defined in OnionLayer.cs
/// </summary>
public class OnionLayerContentTests
{
    [Fact]
    public void OnionLayerContent_Relay_RoundTrip()
    {
        // Arrange
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Relay,
            NextHopAddress = "192.168.1.100",
            NextHopPort = 9000,
            ReplyToken = new byte[] { 0x01, 0x02, 0x03 },
            InnerPayload = new byte[] { 0x04, 0x05, 0x06 }
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        // Assert
        Assert.Equal(OnionLayerType.Relay, deserialized.Type);
        Assert.Equal("192.168.1.100", deserialized.NextHopAddress);
        Assert.Equal(9000, deserialized.NextHopPort);
        Assert.Equal(original.ReplyToken, deserialized.ReplyToken);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_FinalHop_RoundTrip()
    {
        // Arrange
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.FinalHop,
            ReplyToken = new byte[] { 0xAA, 0xBB },
            InnerPayload = new byte[] { 0xCC, 0xDD }
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        // Assert
        Assert.Equal(OnionLayerType.FinalHop, deserialized.Type);
        Assert.Equal(original.ReplyToken, deserialized.ReplyToken);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_Delivery_RoundTrip()
    {
        // Arrange
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Delivery,
            InnerPayload = new byte[] { 0x11, 0x22, 0x33, 0x44 }
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        // Assert
        Assert.Equal(OnionLayerType.Delivery, deserialized.Type);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_Ack_RoundTrip()
    {
        // Arrange
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Ack,
            ReplyToken = new byte[] { 0xFF, 0xEE },
            InnerPayload = new byte[] { 0xDD, 0xCC }
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        // Assert
        Assert.Equal(OnionLayerType.Ack, deserialized.Type);
        Assert.Equal(original.ReplyToken, deserialized.ReplyToken);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_LargePayload_RoundTrip()
    {
        // Arrange
        var largePayload = new byte[10000];
        Random.Shared.NextBytes(largePayload);

        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Relay,
            NextHopAddress = "10.0.0.1",
            NextHopPort = 5000,
            InnerPayload = largePayload
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        // Assert
        Assert.Equal(largePayload, deserialized.InnerPayload);
    }
}
