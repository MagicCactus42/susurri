using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Xunit;

namespace Susurri.Tests.Unit.Network;

/// <summary>
/// Unit tests for ConnectionManager.
/// </summary>
public class ConnectionManagerTests
{
    private readonly ILogger<ConnectionManager> _logger;
    private readonly ILogger<RelayService> _relayLogger;

    public ConnectionManagerTests()
    {
        _logger = Substitute.For<ILogger<ConnectionManager>>();
        _relayLogger = Substitute.For<ILogger<RelayService>>();
    }

    [Fact]
    public async Task GetConnectionAsync_NodeNotInRoutingTable_ReturnsNull()
    {
        // Arrange
        var localId = KademliaId.Random();
        var routingTable = new RoutingTable(localId);
        var relayService = new RelayService(routingTable, _relayLogger);
        await using var manager = new ConnectionManager(routingTable, relayService, _logger);

        var unknownNodeId = KademliaId.Random();

        // Act
        var connection = await manager.GetConnectionAsync(unknownNodeId);

        // Assert
        Assert.Null(connection);
    }

    [Fact]
    public async Task CloseConnectionAsync_NonExistentConnection_DoesNotThrow()
    {
        // Arrange
        var localId = KademliaId.Random();
        var routingTable = new RoutingTable(localId);
        var relayService = new RelayService(routingTable, _relayLogger);
        await using var manager = new ConnectionManager(routingTable, relayService, _logger);

        // Act & Assert - should not throw
        await manager.CloseConnectionAsync(KademliaId.Random());
    }

    [Fact]
    public async Task SendAsync_NodeNotFound_ReturnsFalse()
    {
        // Arrange
        var localId = KademliaId.Random();
        var routingTable = new RoutingTable(localId);
        var relayService = new RelayService(routingTable, _relayLogger);
        await using var manager = new ConnectionManager(routingTable, relayService, _logger);

        // Act
        var result = await manager.SendAsync(KademliaId.Random(), new byte[] { 0x01 });

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SendAndReceiveAsync_NodeNotFound_ReturnsNull()
    {
        // Arrange
        var localId = KademliaId.Random();
        var routingTable = new RoutingTable(localId);
        var relayService = new RelayService(routingTable, _relayLogger);
        await using var manager = new ConnectionManager(routingTable, relayService, _logger);

        // Act
        var result = await manager.SendAndReceiveAsync(
            KademliaId.Random(),
            new byte[] { 0x01 },
            TimeSpan.FromSeconds(1));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CleanupIdleConnectionsAsync_NoConnections_DoesNotThrow()
    {
        // Arrange
        var localId = KademliaId.Random();
        var routingTable = new RoutingTable(localId);
        var relayService = new RelayService(routingTable, _relayLogger);
        await using var manager = new ConnectionManager(routingTable, relayService, _logger);

        // Act & Assert - should not throw
        await manager.CleanupIdleConnectionsAsync();
    }

    [Fact]
    public async Task DisposeAsync_DisposesCleanly()
    {
        // Arrange
        var localId = KademliaId.Random();
        var routingTable = new RoutingTable(localId);
        var relayService = new RelayService(routingTable, _relayLogger);
        var manager = new ConnectionManager(routingTable, relayService, _logger);

        // Act & Assert - should not throw
        await manager.DisposeAsync();
    }
}

/// <summary>
/// Unit tests for NodeConnection.
/// </summary>
public class NodeConnectionTests
{
    [Fact]
    public void IsValid_DirectConnection_NoTcpClient_ReturnsFalse()
    {
        // Arrange
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Direct,
            TcpClient = null
        };

        // Assert
        Assert.False(connection.IsValid);
    }

    [Fact]
    public void IsValid_RelayedConnection_NoCircuit_ReturnsFalse()
    {
        // Arrange
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Relayed,
            RelayCircuit = null
        };

        // Assert
        Assert.False(connection.IsValid);
    }

    [Fact]
    public void IsValid_RelayedConnection_WithCircuit_ReturnsTrue()
    {
        // Arrange
        var circuit = new RelayCircuit
        {
            CircuitId = Guid.NewGuid(),
            RequesterEndpoint = new IPEndPoint(IPAddress.Loopback, 8080),
            TargetNodeId = KademliaId.Random()
        };

        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Relayed,
            RelayCircuit = circuit
        };

        // Assert
        Assert.True(connection.IsValid);
    }

    [Fact]
    public void LastUsed_DefaultsToNow()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Direct
        };
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(connection.LastUsed >= before);
        Assert.True(connection.LastUsed <= after);
    }

    [Fact]
    public async Task SendAsync_DirectWithoutTcpClient_ThrowsInvalidOperation()
    {
        // Arrange
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Direct,
            TcpClient = null
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.SendAsync(new byte[] { 0x01 }));
    }

    [Fact]
    public async Task SendAsync_RelayedWithoutCircuit_ThrowsInvalidOperation()
    {
        // Arrange
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Relayed,
            RelayCircuit = null,
            RelayService = null
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.SendAsync(new byte[] { 0x01 }));
    }

    [Fact]
    public async Task SendAndReceiveAsync_DirectWithoutTcpClient_ThrowsInvalidOperation()
    {
        // Arrange
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Direct,
            TcpClient = null
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.SendAndReceiveAsync(new byte[] { 0x01 }, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task SendAndReceiveAsync_RelayedWithoutRelayNode_ThrowsInvalidOperation()
    {
        // Arrange
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Relayed,
            RelayNode = null,
            RelayService = null
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.SendAndReceiveAsync(new byte[] { 0x01 }, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DisposeAsync_WithNoResources_DoesNotThrow()
    {
        // Arrange
        var connection = new NodeConnection
        {
            NodeId = KademliaId.Random(),
            Type = ConnectionType.Direct
        };

        // Act & Assert
        await connection.DisposeAsync();
    }
}

/// <summary>
/// Unit tests for ConnectionType enum.
/// </summary>
public class ConnectionTypeTests
{
    [Fact]
    public void ConnectionType_HasExpectedValues()
    {
        Assert.Equal(0, (int)ConnectionType.Direct);
        Assert.Equal(1, (int)ConnectionType.Relayed);
    }
}
