using System.Net;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Xunit;

namespace Susurri.Tests.Unit.Kademlia.Protocol;

/// <summary>
/// Unit tests for Kademlia protocol message serialization and deserialization.
/// </summary>
public class KademliaMessageTests
{
    private readonly KademliaId _senderId;
    private readonly byte[] _senderPublicKey;

    public KademliaMessageTests()
    {
        _senderPublicKey = new byte[32];
        Random.Shared.NextBytes(_senderPublicKey);
        _senderId = KademliaId.FromPublicKey(_senderPublicKey);
    }

    #region PingMessage Tests

    [Fact]
    public void PingMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var original = new PingMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as PingMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.SenderId, deserialized.SenderId);
        Assert.Equal(original.SenderPublicKey, deserialized.SenderPublicKey);
    }

    [Fact]
    public void PingMessage_HasCorrectType()
    {
        var message = new PingMessage { SenderId = _senderId };
        Assert.Equal(MessageType.Ping, message.Type);
    }

    #endregion

    #region PongMessage Tests

    [Fact]
    public void PongMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var original = new PongMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            InResponseTo = Guid.NewGuid()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as PongMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.InResponseTo, deserialized.InResponseTo);
        Assert.Equal(original.SenderId, deserialized.SenderId);
    }

    [Fact]
    public void PongMessage_HasCorrectType()
    {
        var message = new PongMessage { SenderId = _senderId };
        Assert.Equal(MessageType.Pong, message.Type);
    }

    #endregion

    #region FindNodeMessage Tests

    [Fact]
    public void FindNodeMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var targetId = KademliaId.Random();
        var original = new FindNodeMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            TargetId = targetId
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as FindNodeMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(targetId, deserialized.TargetId);
    }

    [Fact]
    public void FindNodeMessage_HasCorrectType()
    {
        var message = new FindNodeMessage { SenderId = _senderId, TargetId = KademliaId.Random() };
        Assert.Equal(MessageType.FindNode, message.Type);
    }

    #endregion

    #region FindNodeResponseMessage Tests

    [Fact]
    public void FindNodeResponseMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var nodes = CreateTestNodes(3);
        var original = new FindNodeResponseMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            InResponseTo = Guid.NewGuid(),
            Nodes = nodes
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as FindNodeResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.InResponseTo, deserialized.InResponseTo);
        Assert.Equal(3, deserialized.Nodes.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(nodes[i].Id, deserialized.Nodes[i].Id);
            Assert.Equal(nodes[i].IpAddress, deserialized.Nodes[i].IpAddress);
            Assert.Equal(nodes[i].Port, deserialized.Nodes[i].Port);
        }
    }

    [Fact]
    public void FindNodeResponseMessage_EmptyNodes_RoundTrip()
    {
        // Arrange
        var original = new FindNodeResponseMessage
        {
            SenderId = _senderId,
            InResponseTo = Guid.NewGuid(),
            Nodes = new List<NodeRecord>()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as FindNodeResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Nodes);
    }

    [Fact]
    public void FindNodeResponseMessage_HasCorrectType()
    {
        var message = new FindNodeResponseMessage { SenderId = _senderId };
        Assert.Equal(MessageType.FindNodeResponse, message.Type);
    }

    #endregion

    #region FindValueMessage Tests

    [Fact]
    public void FindValueMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var key = KademliaId.FromString("test-key");
        var original = new FindValueMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            Key = key
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as FindValueMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(key, deserialized.Key);
    }

    [Fact]
    public void FindValueMessage_HasCorrectType()
    {
        var message = new FindValueMessage { SenderId = _senderId, Key = KademliaId.Random() };
        Assert.Equal(MessageType.FindValue, message.Type);
    }

    #endregion

    #region FindValueResponseMessage Tests

    [Fact]
    public void FindValueResponseMessage_Found_RoundTrip()
    {
        // Arrange
        var value = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var original = new FindValueResponseMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            InResponseTo = Guid.NewGuid(),
            Found = true,
            Value = value
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as FindValueResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Found);
        Assert.Equal(value, deserialized.Value);
    }

    [Fact]
    public void FindValueResponseMessage_NotFound_ReturnsClosestNodes()
    {
        // Arrange
        var nodes = CreateTestNodes(5);
        var original = new FindValueResponseMessage
        {
            SenderId = _senderId,
            InResponseTo = Guid.NewGuid(),
            Found = false,
            ClosestNodes = nodes
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as FindValueResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized.Found);
        Assert.Equal(5, deserialized.ClosestNodes.Count);
    }

    [Fact]
    public void FindValueResponseMessage_HasCorrectType()
    {
        var message = new FindValueResponseMessage { SenderId = _senderId };
        Assert.Equal(MessageType.FindValueResponse, message.Type);
    }

    #endregion

    #region StoreMessage Tests

    [Fact]
    public void StoreMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var key = KademliaId.FromString("storage-key");
        var value = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var original = new StoreMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            Key = key,
            Value = value,
            TtlSeconds = 3600
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as StoreMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(key, deserialized.Key);
        Assert.Equal(value, deserialized.Value);
        Assert.Equal(3600u, deserialized.TtlSeconds);
    }

    [Fact]
    public void StoreMessage_LargeValue_RoundTrip()
    {
        // Arrange
        var value = new byte[10000];
        Random.Shared.NextBytes(value);

        var original = new StoreMessage
        {
            SenderId = _senderId,
            Key = KademliaId.Random(),
            Value = value,
            TtlSeconds = 0
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as StoreMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(value, deserialized.Value);
    }

    [Fact]
    public void StoreMessage_HasCorrectType()
    {
        var message = new StoreMessage { SenderId = _senderId, Key = KademliaId.Random() };
        Assert.Equal(MessageType.Store, message.Type);
    }

    #endregion

    #region StoreResponseMessage Tests

    [Fact]
    public void StoreResponseMessage_Success_RoundTrip()
    {
        // Arrange
        var original = new StoreResponseMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            InResponseTo = Guid.NewGuid(),
            Success = true
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as StoreResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Null(deserialized.Error);
    }

    [Fact]
    public void StoreResponseMessage_Failure_PreservesError()
    {
        // Arrange
        var original = new StoreResponseMessage
        {
            SenderId = _senderId,
            InResponseTo = Guid.NewGuid(),
            Success = false,
            Error = "Storage quota exceeded"
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as StoreResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized.Success);
        Assert.Equal("Storage quota exceeded", deserialized.Error);
    }

    [Fact]
    public void StoreResponseMessage_HasCorrectType()
    {
        var message = new StoreResponseMessage { SenderId = _senderId };
        Assert.Equal(MessageType.StoreResponse, message.Type);
    }

    #endregion

    #region OnionMessageWrapper Tests

    [Fact]
    public void OnionMessageWrapper_RoundTrip_PreservesPayload()
    {
        // Arrange
        var payload = new byte[256];
        Random.Shared.NextBytes(payload);

        var original = new OnionMessageWrapper
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            EncryptedPayload = payload
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as OnionMessageWrapper;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(payload, deserialized.EncryptedPayload);
    }

    [Fact]
    public void OnionMessageWrapper_EmptyPayload_RoundTrip()
    {
        // Arrange
        var original = new OnionMessageWrapper
        {
            SenderId = _senderId,
            EncryptedPayload = Array.Empty<byte>()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as OnionMessageWrapper;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.EncryptedPayload);
    }

    [Fact]
    public void OnionMessageWrapper_HasCorrectType()
    {
        var message = new OnionMessageWrapper { SenderId = _senderId };
        Assert.Equal(MessageType.OnionMessage, message.Type);
    }

    #endregion

    #region NodeRecord Tests

    [Fact]
    public void NodeRecord_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var id = KademliaId.Random();
        var pubKey = new byte[32];
        Random.Shared.NextBytes(pubKey);

        var original = new NodeRecord
        {
            Id = id,
            PublicKey = pubKey,
            IpAddress = "192.168.1.100",
            Port = 8080
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Serialize(writer);

        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        var deserialized = NodeRecord.Deserialize(reader);

        // Assert
        Assert.Equal(id, deserialized.Id);
        Assert.Equal(pubKey, deserialized.PublicKey);
        Assert.Equal("192.168.1.100", deserialized.IpAddress);
        Assert.Equal(8080, deserialized.Port);
    }

    [Fact]
    public void NodeRecord_FromNode_CreatesCorrectRecord()
    {
        // Arrange
        var pubKey = new byte[32];
        Random.Shared.NextBytes(pubKey);
        var id = KademliaId.FromPublicKey(pubKey);
        var endpoint = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 9000);
        var node = new KademliaNode(id, pubKey, endpoint);

        // Act
        var record = NodeRecord.FromNode(node);

        // Assert
        Assert.Equal(id, record.Id);
        Assert.Equal(pubKey, record.PublicKey);
        Assert.Equal("10.0.0.5", record.IpAddress);
        Assert.Equal(9000, record.Port);
    }

    [Fact]
    public void NodeRecord_ToNode_CreatesCorrectNode()
    {
        // Arrange
        var pubKey = new byte[32];
        Random.Shared.NextBytes(pubKey);
        var id = KademliaId.FromPublicKey(pubKey);

        var record = new NodeRecord
        {
            Id = id,
            PublicKey = pubKey,
            IpAddress = "172.16.0.1",
            Port = 5000
        };

        // Act
        var node = record.ToNode();

        // Assert
        Assert.Equal(id, node.Id);
        Assert.Equal(pubKey, node.EncryptionPublicKey);
        Assert.Equal(IPAddress.Parse("172.16.0.1"), node.EndPoint.Address);
        Assert.Equal(5000, node.EndPoint.Port);
    }

    [Fact]
    public void NodeRecord_IPv6Address_RoundTrip()
    {
        // Arrange
        var id = KademliaId.Random();
        var pubKey = new byte[32];

        var original = new NodeRecord
        {
            Id = id,
            PublicKey = pubKey,
            IpAddress = "::1",
            Port = 3000
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        original.Serialize(writer);

        ms.Position = 0;
        using var reader = new BinaryReader(ms);
        var deserialized = NodeRecord.Deserialize(reader);

        // Assert
        Assert.Equal("::1", deserialized.IpAddress);
    }

    #endregion

    #region Deserialization Error Tests

    [Fact]
    public void Deserialize_UnknownType_ThrowsException()
    {
        // Arrange - create invalid message with unknown type
        var data = new byte[50];
        data[0] = 0xFF; // Invalid message type
        // Fill in dummy message ID and sender ID
        Guid.NewGuid().ToByteArray().CopyTo(data, 1);
        new byte[32].CopyTo(data, 17);

        // Act & Assert
        Assert.Throws<InvalidDataException>(() => KademliaMessage.Deserialize(data));
    }

    #endregion

    #region MessageId Tests

    [Fact]
    public void AllMessages_GenerateUniqueMessageIds()
    {
        // Arrange & Act
        var messages = new KademliaMessage[]
        {
            new PingMessage { SenderId = _senderId },
            new PongMessage { SenderId = _senderId },
            new FindNodeMessage { SenderId = _senderId, TargetId = KademliaId.Random() },
            new FindNodeResponseMessage { SenderId = _senderId },
            new FindValueMessage { SenderId = _senderId, Key = KademliaId.Random() },
            new FindValueResponseMessage { SenderId = _senderId },
            new StoreMessage { SenderId = _senderId, Key = KademliaId.Random() },
            new StoreResponseMessage { SenderId = _senderId },
            new OnionMessageWrapper { SenderId = _senderId }
        };

        // Assert
        var messageIds = messages.Select(m => m.MessageId).ToList();
        Assert.Equal(messageIds.Count, messageIds.Distinct().Count());
    }

    #endregion

    private List<NodeRecord> CreateTestNodes(int count)
    {
        var nodes = new List<NodeRecord>();
        for (int i = 0; i < count; i++)
        {
            var pubKey = new byte[32];
            Random.Shared.NextBytes(pubKey);

            nodes.Add(new NodeRecord
            {
                Id = KademliaId.FromPublicKey(pubKey),
                PublicKey = pubKey,
                IpAddress = $"192.168.1.{i + 1}",
                Port = 8000 + i
            });
        }
        return nodes;
    }
}
