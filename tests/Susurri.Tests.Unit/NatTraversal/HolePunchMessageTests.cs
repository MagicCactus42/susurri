using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Xunit;

namespace Susurri.Tests.Unit.NatTraversal;

public class HolePunchMessageTests
{
    private readonly KademliaId _senderId;
    private readonly byte[] _senderPublicKey;

    public HolePunchMessageTests()
    {
        _senderPublicKey = new byte[32];
        Random.Shared.NextBytes(_senderPublicKey);
        _senderId = KademliaId.FromPublicKey(_senderPublicKey);
    }

    #region HolePunchRequestMessage Tests

    [Fact]
    public void HolePunchRequestMessage_RoundTrip_PreservesAllFields()
    {
        var targetId = KademliaId.Random();
        var punchId = Guid.NewGuid();

        var original = new HolePunchRequestMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            TargetNodeId = targetId,
            InitiatorEndpoint = "203.0.113.5:12345",
            PunchId = punchId
        };

        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as HolePunchRequestMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.SenderId, deserialized.SenderId);
        Assert.Equal(original.SenderPublicKey, deserialized.SenderPublicKey);
        Assert.Equal(original.TargetNodeId, deserialized.TargetNodeId);
        Assert.Equal("203.0.113.5:12345", deserialized.InitiatorEndpoint);
        Assert.Equal(punchId, deserialized.PunchId);
    }

    [Fact]
    public void HolePunchRequestMessage_HasCorrectType()
    {
        var message = new HolePunchRequestMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            TargetNodeId = KademliaId.Random()
        };

        Assert.Equal(MessageType.HolePunchRequest, message.Type);
    }

    [Fact]
    public void HolePunchRequestMessage_EmptyEndpoint_Survives()
    {
        var original = new HolePunchRequestMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            TargetNodeId = KademliaId.Random(),
            InitiatorEndpoint = string.Empty,
            PunchId = Guid.NewGuid()
        };

        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as HolePunchRequestMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(string.Empty, deserialized.InitiatorEndpoint);
    }

    #endregion

    #region HolePunchResponseMessage Tests

    [Fact]
    public void HolePunchResponseMessage_Accepted_RoundTrip()
    {
        var punchId = Guid.NewGuid();
        var inResponseTo = Guid.NewGuid();

        var original = new HolePunchResponseMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            InResponseTo = inResponseTo,
            Accepted = true,
            TargetEndpoint = "198.51.100.10:54321",
            PunchId = punchId
        };

        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as HolePunchResponseMessage;

        Assert.NotNull(deserialized);
        Assert.Equal(inResponseTo, deserialized.InResponseTo);
        Assert.True(deserialized.Accepted);
        Assert.Equal("198.51.100.10:54321", deserialized.TargetEndpoint);
        Assert.Equal(punchId, deserialized.PunchId);
    }

    [Fact]
    public void HolePunchResponseMessage_Rejected_RoundTrip()
    {
        var original = new HolePunchResponseMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            InResponseTo = Guid.NewGuid(),
            Accepted = false,
            TargetEndpoint = string.Empty,
            PunchId = Guid.NewGuid()
        };

        var serialized = original.Serialize();
        var deserialized = KademliaMessage.Deserialize(serialized) as HolePunchResponseMessage;

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Accepted);
        Assert.Equal(string.Empty, deserialized.TargetEndpoint);
    }

    [Fact]
    public void HolePunchResponseMessage_HasCorrectType()
    {
        var message = new HolePunchResponseMessage
        {
            SenderId = _senderId,
            SenderPublicKey = _senderPublicKey,
            InResponseTo = Guid.NewGuid()
        };

        Assert.Equal(MessageType.HolePunchResponse, message.Type);
    }

    #endregion
}
