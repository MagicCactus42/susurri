using System.Net;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Xunit;

namespace Susurri.Tests.Unit.Network;

/// <summary>
/// Unit tests for relay protocol message serialization and deserialization.
/// </summary>
public class RelayProtocolTests
{
    #region CircuitRequestMessage Tests

    [Fact]
    public void CircuitRequestMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var original = new CircuitRequestMessage
        {
            CircuitId = Guid.NewGuid(),
            TargetNodeId = KademliaId.Random(),
            RequestedBandwidth = 1024 * 100
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as CircuitRequestMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.MessageId, deserialized.MessageId);
        Assert.Equal(original.CircuitId, deserialized.CircuitId);
        Assert.Equal(original.TargetNodeId, deserialized.TargetNodeId);
        Assert.Equal(original.RequestedBandwidth, deserialized.RequestedBandwidth);
    }

    [Fact]
    public void CircuitRequestMessage_HasCorrectType()
    {
        var message = new CircuitRequestMessage
        {
            CircuitId = Guid.NewGuid(),
            TargetNodeId = KademliaId.Random()
        };

        Assert.Equal(RelayMessageType.CircuitRequest, message.Type);
    }

    #endregion

    #region CircuitResponseMessage Tests

    [Fact]
    public void CircuitResponseMessage_Accepted_RoundTrip()
    {
        // Arrange
        var original = new CircuitResponseMessage
        {
            CircuitId = Guid.NewGuid(),
            Accepted = true,
            TargetEndpoint = "192.168.1.100:8080"
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as CircuitResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.CircuitId, deserialized.CircuitId);
        Assert.True(deserialized.Accepted);
        Assert.Null(deserialized.RejectReason);
        Assert.Equal("192.168.1.100:8080", deserialized.TargetEndpoint);
    }

    [Fact]
    public void CircuitResponseMessage_Rejected_PreservesReason()
    {
        // Arrange
        var original = new CircuitResponseMessage
        {
            CircuitId = Guid.NewGuid(),
            Accepted = false,
            RejectReason = "Max circuits reached"
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as CircuitResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized.Accepted);
        Assert.Equal("Max circuits reached", deserialized.RejectReason);
    }

    [Fact]
    public void CircuitResponseMessage_HasCorrectType()
    {
        var message = new CircuitResponseMessage { CircuitId = Guid.NewGuid() };
        Assert.Equal(RelayMessageType.CircuitResponse, message.Type);
    }

    #endregion

    #region RelayDataMessage Tests

    [Fact]
    public void RelayDataMessage_RoundTrip_PreservesData()
    {
        // Arrange
        var testData = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var original = new RelayDataMessage
        {
            CircuitId = Guid.NewGuid(),
            Data = testData
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as RelayDataMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.CircuitId, deserialized.CircuitId);
        Assert.Equal(testData, deserialized.Data);
    }

    [Fact]
    public void RelayDataMessage_LargePayload_RoundTrip()
    {
        // Arrange
        var testData = new byte[1024 * 64]; // 64KB
        Random.Shared.NextBytes(testData);

        var original = new RelayDataMessage
        {
            CircuitId = Guid.NewGuid(),
            Data = testData
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as RelayDataMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(testData, deserialized.Data);
    }

    [Fact]
    public void RelayDataMessage_EmptyData_RoundTrip()
    {
        // Arrange
        var original = new RelayDataMessage
        {
            CircuitId = Guid.NewGuid(),
            Data = Array.Empty<byte>()
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as RelayDataMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Data);
    }

    [Fact]
    public void RelayDataMessage_HasCorrectType()
    {
        var message = new RelayDataMessage { CircuitId = Guid.NewGuid() };
        Assert.Equal(RelayMessageType.RelayData, message.Type);
    }

    #endregion

    #region CircuitCloseMessage Tests

    [Fact]
    public void CircuitCloseMessage_RoundTrip()
    {
        // Arrange
        var circuitId = Guid.NewGuid();
        var original = new CircuitCloseMessage { CircuitId = circuitId };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as CircuitCloseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(circuitId, deserialized.CircuitId);
    }

    [Fact]
    public void CircuitCloseMessage_HasCorrectType()
    {
        var message = new CircuitCloseMessage { CircuitId = Guid.NewGuid() };
        Assert.Equal(RelayMessageType.CircuitClose, message.Type);
    }

    #endregion

    #region RelayRequestMessage Tests

    [Fact]
    public void RelayRequestMessage_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var original = new RelayRequestMessage
        {
            TargetNodeId = KademliaId.Random(),
            Payload = payload,
            ExpectResponse = true
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as RelayRequestMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.TargetNodeId, deserialized.TargetNodeId);
        Assert.Equal(payload, deserialized.Payload);
        Assert.True(deserialized.ExpectResponse);
    }

    [Fact]
    public void RelayRequestMessage_NoResponse_RoundTrip()
    {
        // Arrange
        var original = new RelayRequestMessage
        {
            TargetNodeId = KademliaId.Random(),
            Payload = new byte[] { 0x01 },
            ExpectResponse = false
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as RelayRequestMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized.ExpectResponse);
    }

    [Fact]
    public void RelayRequestMessage_HasCorrectType()
    {
        var message = new RelayRequestMessage { TargetNodeId = KademliaId.Random() };
        Assert.Equal(RelayMessageType.RelayRequest, message.Type);
    }

    #endregion

    #region RelayResponseMessage Tests

    [Fact]
    public void RelayResponseMessage_Success_RoundTrip()
    {
        // Arrange
        var responsePayload = new byte[] { 0x11, 0x22, 0x33 };
        var original = new RelayResponseMessage
        {
            InResponseTo = Guid.NewGuid(),
            Success = true,
            Payload = responsePayload
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as RelayResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.InResponseTo, deserialized.InResponseTo);
        Assert.True(deserialized.Success);
        Assert.Equal(responsePayload, deserialized.Payload);
        Assert.Null(deserialized.Error);
    }

    [Fact]
    public void RelayResponseMessage_Failure_PreservesError()
    {
        // Arrange
        var original = new RelayResponseMessage
        {
            InResponseTo = Guid.NewGuid(),
            Success = false,
            Error = "Connection refused"
        };

        // Act
        var serialized = original.Serialize();
        var deserialized = RelayMessage.Deserialize(serialized) as RelayResponseMessage;

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized.Success);
        Assert.Equal("Connection refused", deserialized.Error);
    }

    [Fact]
    public void RelayResponseMessage_HasCorrectType()
    {
        var message = new RelayResponseMessage { InResponseTo = Guid.NewGuid() };
        Assert.Equal(RelayMessageType.RelayResponse, message.Type);
    }

    #endregion

    #region RelayCircuit Tests

    [Fact]
    public void RelayCircuit_IsExpired_ReturnsFalse_WhenActive()
    {
        // Arrange
        var circuit = new RelayCircuit
        {
            CircuitId = Guid.NewGuid(),
            RequesterEndpoint = new IPEndPoint(IPAddress.Loopback, 8080),
            TargetNodeId = KademliaId.Random(),
            LastActivity = DateTimeOffset.UtcNow
        };

        // Act
        var isExpired = circuit.IsExpired(TimeSpan.FromMinutes(5));

        // Assert
        Assert.False(isExpired);
    }

    [Fact]
    public void RelayCircuit_IsExpired_ReturnsTrue_WhenInactive()
    {
        // Arrange
        var circuit = new RelayCircuit
        {
            CircuitId = Guid.NewGuid(),
            RequesterEndpoint = new IPEndPoint(IPAddress.Loopback, 8080),
            TargetNodeId = KademliaId.Random(),
            LastActivity = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        // Act
        var isExpired = circuit.IsExpired(TimeSpan.FromMinutes(5));

        // Assert
        Assert.True(isExpired);
    }

    [Fact]
    public void RelayCircuit_BytesRelayed_TracksUsage()
    {
        // Arrange
        var circuit = new RelayCircuit
        {
            CircuitId = Guid.NewGuid(),
            RequesterEndpoint = new IPEndPoint(IPAddress.Loopback, 8080),
            TargetNodeId = KademliaId.Random()
        };

        // Act
        circuit.BytesRelayed += 1024;
        circuit.BytesRelayed += 2048;

        // Assert
        Assert.Equal(3072, circuit.BytesRelayed);
    }

    #endregion

    #region Deserialize Error Handling Tests

    [Fact]
    public void Deserialize_UnknownType_ThrowsException()
    {
        // Arrange
        var data = new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => RelayMessage.Deserialize(data));
    }

    #endregion

    #region Message ID Tests

    [Fact]
    public void AllMessages_HaveUniqueMessageIds()
    {
        // Arrange
        var messages = new RelayMessage[]
        {
            new CircuitRequestMessage { CircuitId = Guid.NewGuid(), TargetNodeId = KademliaId.Random() },
            new CircuitResponseMessage { CircuitId = Guid.NewGuid() },
            new RelayDataMessage { CircuitId = Guid.NewGuid() },
            new CircuitCloseMessage { CircuitId = Guid.NewGuid() },
            new RelayRequestMessage { TargetNodeId = KademliaId.Random() },
            new RelayResponseMessage { InResponseTo = Guid.NewGuid() }
        };

        // Act
        var messageIds = messages.Select(m => m.MessageId).ToList();

        // Assert
        Assert.Equal(messageIds.Count, messageIds.Distinct().Count());
    }

    #endregion
}
