#nullable enable
using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Onion;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class OnionRouterTests
{
    [Fact]
    public void OnMessageReceived_EventCanBeSubscribed()
    {
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

public class AckMessageTests
{
    [Fact]
    public void AckMessage_RoundTrip_PreservesAllFields()
    {
        var original = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var serialized = original.Serialize();
        var deserialized = AckMessage.Deserialize(serialized);

        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
    }

    [Fact]
    public void AckMessage_EmptyGuid_RoundTrip()
    {
        var original = new AckMessage
        {
            MessageId = Guid.Empty,
            Timestamp = 0
        };

        var serialized = original.Serialize();
        var deserialized = AckMessage.Deserialize(serialized);

        Assert.Equal(Guid.Empty, deserialized.MessageId);
        Assert.Equal(0, deserialized.Timestamp);
    }

    [Fact]
    public void AckMessage_MaxTimestamp_RoundTrip()
    {
        var original = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = long.MaxValue
        };

        var serialized = original.Serialize();
        var deserialized = AckMessage.Deserialize(serialized);

        Assert.Equal(long.MaxValue, deserialized.Timestamp);
    }

    [Fact]
    public void AckMessage_SerializedSize_IsCorrect()
    {
        var ack = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = 12345678L
        };

        var serialized = ack.Serialize();

        Assert.Equal(24, serialized.Length);
    }

    [Fact]
    public void AckMessage_MultipleSerializations_ProduceSameResult()
    {
        var original = new AckMessage
        {
            MessageId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var serialized1 = original.Serialize();
        var serialized2 = original.Serialize();

        Assert.Equal(serialized1, serialized2);
    }
}

public class OnionEncryptionIntegrationTests
{
    private readonly Key _signingKey;
    private readonly byte[] _signingPublicKey;

    public OnionEncryptionIntegrationTests()
    {
        _signingKey = Key.Create(SignatureAlgorithm.Ed25519);
        _signingPublicKey = _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    [Fact]
    public void EncryptDecrypt_SingleLayer_RecoverOriginalData()
    {
        using var senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        using var recipientKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var recipientPubKey = recipientKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var builder = new OnionBuilder(senderKey);
        var message = CreateSignedMessage(senderKey, "Test message");

        var nodePubKey = CreateRandomX25519PublicKey();
        var nodeId = KademliaId.FromPublicKey(nodePubKey);
        var path = new List<KademliaNode>
        {
            new KademliaNode(nodeId, nodePubKey, new IPEndPoint(IPAddress.Loopback, 8080))
        };

        var packet = builder.Build(message, recipientPubKey, path);

        Assert.NotEmpty(packet.EncryptedPayload);
        Assert.Single(packet.ReplyTokens);

        var layer = OnionLayer.Deserialize(packet.EncryptedPayload);
        Assert.NotEmpty(layer.EphemeralPublicKey);
        Assert.NotEmpty(layer.Nonce);
        Assert.NotEmpty(layer.Ciphertext);
    }

    [Fact]
    public void Build_MultipleHops_CreatesLayeredOnion()
    {
        using var senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var recipientPubKey = CreateRandomX25519PublicKey();

        var builder = new OnionBuilder(senderKey);
        var message = CreateSignedMessage(senderKey, "Multi-hop message");

        var path = new List<KademliaNode>();
        for (int i = 0; i < 3; i++)
        {
            var nodePubKey = CreateRandomX25519PublicKey();
            var nodeId = KademliaId.FromPublicKey(nodePubKey);
            path.Add(new KademliaNode(nodeId, nodePubKey, new IPEndPoint(IPAddress.Loopback, 8080 + i)));
        }

        var packet = builder.Build(message, recipientPubKey, path);

        Assert.Equal(3, packet.ReplyTokens.Count);
        Assert.Equal(path[0], packet.FirstHop);

        var outerLayer = OnionLayer.Deserialize(packet.EncryptedPayload);
        Assert.NotEmpty(outerLayer.Ciphertext);
    }

    [Fact]
    public void ReplyTokens_MatchPathNodes()
    {
        using var senderKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var recipientPubKey = CreateRandomX25519PublicKey();

        var builder = new OnionBuilder(senderKey);
        var message = CreateSignedMessage(senderKey, "Test");

        var path = new List<KademliaNode>();
        var nodePubKeys = new List<byte[]>();
        for (int i = 0; i < 3; i++)
        {
            var nodePubKey = CreateRandomX25519PublicKey();
            nodePubKeys.Add(nodePubKey);
            var nodeId = KademliaId.FromPublicKey(nodePubKey);
            path.Add(new KademliaNode(nodeId, nodePubKey, new IPEndPoint(IPAddress.Loopback, 8080 + i)));
        }

        var packet = builder.Build(message, recipientPubKey, path);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(nodePubKeys[i], packet.ReplyTokens[i].NodePublicKey);
        }
    }

    private ChatMessage CreateSignedMessage(Key senderEncryptionKey, string content)
    {
        var message = new ChatMessage
        {
            SenderPublicKey = senderEncryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            SenderSigningPublicKey = _signingPublicKey,
            Content = content,
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
}

public class OnionLayerContentTests
{
    [Fact]
    public void OnionLayerContent_Relay_RoundTrip()
    {
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Relay,
            NextHopAddress = "192.168.1.100",
            NextHopPort = 9000,
            ReplyToken = new byte[] { 0x01, 0x02, 0x03 },
            InnerPayload = new byte[] { 0x04, 0x05, 0x06 }
        };

        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        Assert.Equal(OnionLayerType.Relay, deserialized.Type);
        Assert.Equal("192.168.1.100", deserialized.NextHopAddress);
        Assert.Equal(9000, deserialized.NextHopPort);
        Assert.Equal(original.ReplyToken, deserialized.ReplyToken);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_FinalHop_RoundTrip()
    {
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.FinalHop,
            ReplyToken = new byte[] { 0xAA, 0xBB },
            InnerPayload = new byte[] { 0xCC, 0xDD }
        };

        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        Assert.Equal(OnionLayerType.FinalHop, deserialized.Type);
        Assert.Equal(original.ReplyToken, deserialized.ReplyToken);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_Delivery_RoundTrip()
    {
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Delivery,
            InnerPayload = new byte[] { 0x11, 0x22, 0x33, 0x44 }
        };

        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        Assert.Equal(OnionLayerType.Delivery, deserialized.Type);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_Ack_RoundTrip()
    {
        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Ack,
            ReplyToken = new byte[] { 0xFF, 0xEE },
            InnerPayload = new byte[] { 0xDD, 0xCC }
        };

        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        Assert.Equal(OnionLayerType.Ack, deserialized.Type);
        Assert.Equal(original.ReplyToken, deserialized.ReplyToken);
        Assert.Equal(original.InnerPayload, deserialized.InnerPayload);
    }

    [Fact]
    public void OnionLayerContent_LargePayload_RoundTrip()
    {
        var largePayload = new byte[10000];
        Random.Shared.NextBytes(largePayload);

        var original = new OnionLayerContent
        {
            Type = OnionLayerType.Relay,
            NextHopAddress = "10.0.0.1",
            NextHopPort = 5000,
            InnerPayload = largePayload
        };

        var serialized = original.Serialize();
        var deserialized = OnionLayerContent.Deserialize(serialized);

        Assert.Equal(largePayload, deserialized.InnerPayload);
    }
}
