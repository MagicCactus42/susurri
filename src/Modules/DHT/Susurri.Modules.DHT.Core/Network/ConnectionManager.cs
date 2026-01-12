using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.Modules.DHT.Core.Network;

public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ILogger<ConnectionManager> _logger;
    private readonly RoutingTable _routingTable;
    private readonly RelayService _relayService;

    private readonly ConcurrentDictionary<KademliaId, NodeConnection> _connections = new();

    // Configuration
    private static readonly TimeSpan DirectConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(10);
    private const int MaxRelayAttempts = 3;

    public ConnectionManager(
        RoutingTable routingTable,
        RelayService relayService,
        ILogger<ConnectionManager> logger)
    {
        _routingTable = routingTable;
        _relayService = relayService;
        _logger = logger;
    }

    public async Task<NodeConnection?> GetConnectionAsync(KademliaId nodeId)
    {
        if (_connections.TryGetValue(nodeId, out var existing) && existing.IsValid)
        {
            existing.LastUsed = DateTimeOffset.UtcNow;
            return existing;
        }

        var nodes = _routingTable.FindClosestNodes(nodeId, 1);
        var targetNode = nodes.FirstOrDefault(n => n.Id == nodeId);

        if (targetNode == null)
        {
            _logger.LogWarning("Node {NodeId} not found in routing table", nodeId.ToString()[..16]);
            return null;
        }

        var connection = await EstablishConnectionAsync(targetNode);

        if (connection != null)
        {
            _connections[nodeId] = connection;
        }

        return connection;
    }

    public async Task<bool> SendAsync(KademliaId nodeId, byte[] data)
    {
        var connection = await GetConnectionAsync(nodeId);
        if (connection == null)
        {
            return false;
        }

        try
        {
            await connection.SendAsync(data);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send to {NodeId}, will retry with new connection",
                nodeId.ToString()[..16]);

            _connections.TryRemove(nodeId, out _);
            connection = await GetConnectionAsync(nodeId);

            if (connection != null)
            {
                try
                {
                    await connection.SendAsync(data);
                    return true;
                }
                catch { }
            }

            return false;
        }
    }

    public async Task<byte[]?> SendAndReceiveAsync(KademliaId nodeId, byte[] data, TimeSpan timeout)
    {
        var connection = await GetConnectionAsync(nodeId);
        if (connection == null)
        {
            return null;
        }

        try
        {
            return await connection.SendAndReceiveAsync(data, timeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request to {NodeId} failed", nodeId.ToString()[..16]);
            _connections.TryRemove(nodeId, out _);
            return null;
        }
    }

    public async Task CloseConnectionAsync(KademliaId nodeId)
    {
        if (_connections.TryRemove(nodeId, out var connection))
        {
            await connection.DisposeAsync();
        }
    }

    public async Task CleanupIdleConnectionsAsync()
    {
        var idleConnections = _connections
            .Where(kvp => DateTimeOffset.UtcNow - kvp.Value.LastUsed > ConnectionIdleTimeout)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var nodeId in idleConnections)
        {
            await CloseConnectionAsync(nodeId);
        }
    }

    private async Task<NodeConnection?> EstablishConnectionAsync(KademliaNode targetNode)
    {
        _logger.LogDebug("Establishing connection to {NodeId}", targetNode.Id.ToString()[..16]);

        var directConnection = await TryDirectConnectionAsync(targetNode);
        if (directConnection != null)
        {
            _logger.LogDebug("Direct connection established to {NodeId}", targetNode.Id.ToString()[..16]);
            return directConnection;
        }

        _logger.LogDebug("Direct connection failed to {NodeId}, trying relay", targetNode.Id.ToString()[..16]);

        var relayConnection = await TryRelayConnectionAsync(targetNode);
        if (relayConnection != null)
        {
            _logger.LogDebug("Relay connection established to {NodeId}", targetNode.Id.ToString()[..16]);
            return relayConnection;
        }

        _logger.LogWarning("Failed to establish any connection to {NodeId}", targetNode.Id.ToString()[..16]);
        return null;
    }

    private async Task<NodeConnection?> TryDirectConnectionAsync(KademliaNode targetNode)
    {
        try
        {
            var client = new TcpClient();

            using var cts = new CancellationTokenSource(DirectConnectTimeout);
            await client.ConnectAsync(targetNode.EndPoint.Address, targetNode.EndPoint.Port, cts.Token);

            return new NodeConnection
            {
                NodeId = targetNode.Id,
                Type = ConnectionType.Direct,
                TcpClient = client,
                Endpoint = targetNode.EndPoint
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct connection to {Endpoint} failed", targetNode.EndPoint);
            return null;
        }
    }

    private async Task<NodeConnection?> TryRelayConnectionAsync(KademliaNode targetNode)
    {
        var relayNodes = _routingTable.GetRandomNodes(MaxRelayAttempts * 2)
            .Where(n => n.Id != targetNode.Id)
            .Take(MaxRelayAttempts)
            .ToList();

        if (relayNodes.Count == 0)
        {
            _logger.LogWarning("No relay nodes available");
            return null;
        }

        foreach (var relayNode in relayNodes)
        {
            try
            {
                var circuit = await _relayService.EstablishCircuitAsync(relayNode, targetNode.Id);

                if (circuit != null)
                {
                    return new NodeConnection
                    {
                        NodeId = targetNode.Id,
                        Type = ConnectionType.Relayed,
                        RelayNode = relayNode,
                        RelayCircuit = circuit,
                        RelayService = _relayService
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Relay through {RelayId} failed", relayNode.Id.ToString()[..16]);
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }
        _connections.Clear();
    }
}

public sealed class NodeConnection : IAsyncDisposable
{
    public KademliaId NodeId { get; init; }
    public ConnectionType Type { get; init; }
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;

    public TcpClient? TcpClient { get; init; }
    public IPEndPoint? Endpoint { get; init; }

    public KademliaNode? RelayNode { get; init; }
    public RelayCircuit? RelayCircuit { get; init; }
    public RelayService? RelayService { get; init; }

    public bool IsValid => Type switch
    {
        ConnectionType.Direct => TcpClient?.Connected ?? false,
        ConnectionType.Relayed => RelayCircuit != null,
        _ => false
    };

    public async Task SendAsync(byte[] data)
    {
        LastUsed = DateTimeOffset.UtcNow;

        switch (Type)
        {
            case ConnectionType.Direct:
                await SendDirectAsync(data);
                break;

            case ConnectionType.Relayed:
                await SendRelayedAsync(data);
                break;

            default:
                throw new InvalidOperationException($"Unknown connection type: {Type}");
        }
    }

    public async Task<byte[]?> SendAndReceiveAsync(byte[] data, TimeSpan timeout)
    {
        LastUsed = DateTimeOffset.UtcNow;

        switch (Type)
        {
            case ConnectionType.Direct:
                return await SendAndReceiveDirectAsync(data, timeout);

            case ConnectionType.Relayed:
                return await SendAndReceiveRelayedAsync(data, timeout);

            default:
                throw new InvalidOperationException($"Unknown connection type: {Type}");
        }
    }

    private async Task SendDirectAsync(byte[] data)
    {
        if (TcpClient == null || !TcpClient.Connected)
            throw new InvalidOperationException("Not connected");

        var stream = TcpClient.GetStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(data.Length);
        writer.Write(data);
        await stream.FlushAsync();
    }

    private async Task<byte[]?> SendAndReceiveDirectAsync(byte[] data, TimeSpan timeout)
    {
        if (TcpClient == null || !TcpClient.Connected)
            throw new InvalidOperationException("Not connected");

        var stream = TcpClient.GetStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(data.Length);
        writer.Write(data);
        await stream.FlushAsync();

        TcpClient.ReceiveTimeout = (int)timeout.TotalMilliseconds;

        try
        {
            var length = reader.ReadInt32();
            return reader.ReadBytes(length);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task SendRelayedAsync(byte[] data)
    {
        if (RelayCircuit == null || RelayService == null)
            throw new InvalidOperationException("Not connected via relay");

        await RelayService.SendThroughCircuitAsync(RelayCircuit.CircuitId, data);
    }

    private async Task<byte[]?> SendAndReceiveRelayedAsync(byte[] data, TimeSpan timeout)
    {
        if (RelayNode == null || RelayService == null)
            throw new InvalidOperationException("Not connected via relay");

        return await RelayService.RelayToNodeAsync(RelayNode, NodeId, data, expectResponse: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (TcpClient != null)
        {
            TcpClient.Close();
            TcpClient.Dispose();
        }

        if (RelayCircuit != null && RelayService != null)
        {
            await RelayService.CloseCircuitAsync(RelayCircuit.CircuitId);
        }
    }
}

public enum ConnectionType
{
    Direct,
    Relayed
}
