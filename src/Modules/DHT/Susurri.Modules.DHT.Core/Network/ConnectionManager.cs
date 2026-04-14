using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.NatTraversal;

namespace Susurri.Modules.DHT.Core.Network;

public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ILogger<ConnectionManager> _logger;
    private readonly RoutingTable _routingTable;
    private readonly RelayService _relayService;
    private readonly NatTraversalService? _natTraversal;
    private readonly Func<KademliaNode, HolePunchRequestMessage, Task<HolePunchResponseMessage?>>? _sendHolePunchRequest;

    private readonly ConcurrentDictionary<KademliaId, NodeConnection> _connections = new();

    // Configuration
    private static readonly TimeSpan DirectConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(10);
    private const int MaxRelayAttempts = 3;

    public ConnectionManager(
        RoutingTable routingTable,
        RelayService relayService,
        ILogger<ConnectionManager> logger,
        NatTraversalService? natTraversal = null,
        Func<KademliaNode, HolePunchRequestMessage, Task<HolePunchResponseMessage?>>? sendHolePunchRequest = null)
    {
        _routingTable = routingTable;
        _relayService = relayService;
        _logger = logger;
        _natTraversal = natTraversal;
        _sendHolePunchRequest = sendHolePunchRequest;
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

        var connection = await EstablishConnectionAsync(targetNode).ConfigureAwait(false);

        if (connection != null)
        {
            _connections[nodeId] = connection;
        }

        return connection;
    }

    public async Task<bool> SendAsync(KademliaId nodeId, byte[] data)
    {
        var connection = await GetConnectionAsync(nodeId).ConfigureAwait(false);
        if (connection == null)
        {
            return false;
        }

        try
        {
            await connection.SendAsync(data).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send to {NodeId}, will retry with new connection",
                nodeId.ToString()[..16]);

            _connections.TryRemove(nodeId, out _);
            connection = await GetConnectionAsync(nodeId).ConfigureAwait(false);

            if (connection != null)
            {
                try
                {
                    await connection.SendAsync(data).ConfigureAwait(false);
                    return true;
                }
                catch (Exception retryEx)
                {
                    _logger.LogDebug(retryEx, "Send retry to {NodeId} failed", nodeId.ToString()[..16]);
                }
            }

            return false;
        }
    }

    public async Task<byte[]?> SendAndReceiveAsync(KademliaId nodeId, byte[] data, TimeSpan timeout)
    {
        var connection = await GetConnectionAsync(nodeId).ConfigureAwait(false);
        if (connection == null)
        {
            return null;
        }

        try
        {
            return await connection.SendAndReceiveAsync(data, timeout).ConfigureAwait(false);
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
            await connection.DisposeAsync().ConfigureAwait(false);
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
            await CloseConnectionAsync(nodeId).ConfigureAwait(false);
        }
    }

    private async Task<NodeConnection?> EstablishConnectionAsync(KademliaNode targetNode)
    {
        _logger.LogDebug("Establishing connection to {NodeId}", targetNode.Id.ToString()[..16]);

        // Step 1: Try direct TCP connection
        var directConnection = await TryDirectConnectionAsync(targetNode).ConfigureAwait(false);
        if (directConnection != null)
        {
            _logger.LogDebug("Direct connection established to {NodeId}", targetNode.Id.ToString()[..16]);
            return directConnection;
        }

        // Step 2: Try UDP hole punching (if NAT traversal is available and capable)
        _logger.LogDebug("Direct connection failed to {NodeId}, trying hole punch", targetNode.Id.ToString()[..16]);

        var holePunchConnection = await TryHolePunchConnectionAsync(targetNode).ConfigureAwait(false);
        if (holePunchConnection != null)
        {
            _logger.LogDebug("Hole punch connection established to {NodeId}", targetNode.Id.ToString()[..16]);
            return holePunchConnection;
        }

        // Step 3: Fall back to relay
        _logger.LogDebug("Hole punch failed to {NodeId}, trying relay", targetNode.Id.ToString()[..16]);

        var relayConnection = await TryRelayConnectionAsync(targetNode).ConfigureAwait(false);
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
            await client.ConnectAsync(targetNode.EndPoint.Address, targetNode.EndPoint.Port, cts.Token).ConfigureAwait(false);

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

    private async Task<NodeConnection?> TryHolePunchConnectionAsync(KademliaNode targetNode)
    {
        if (_natTraversal == null || !_natTraversal.CanHolePunch || _sendHolePunchRequest == null)
            return null;

        try
        {
            var punchId = Guid.NewGuid();
            var localEndpointStr = _natTraversal.GetPublicEndpointString();

            if (string.IsNullOrEmpty(localEndpointStr))
                return null;

            // Send hole punch request to the target via an intermediary node
            var intermediary = FindIntermediaryFor(targetNode);
            if (intermediary == null)
            {
                _logger.LogDebug("No intermediary available for hole punch to {NodeId}",
                    targetNode.Id.ToString()[..16]);
                return null;
            }

            var request = new HolePunchRequestMessage
            {
                SenderId = _routingTable.LocalId,
                SenderPublicKey = Array.Empty<byte>(), // filled by caller
                TargetNodeId = targetNode.Id,
                InitiatorEndpoint = localEndpointStr,
                PunchId = punchId
            };

            var response = await _sendHolePunchRequest(intermediary, request).ConfigureAwait(false);

            if (response == null || !response.Accepted)
            {
                _logger.LogDebug("Hole punch request rejected or timed out for {NodeId}",
                    targetNode.Id.ToString()[..16]);
                return null;
            }

            var remoteEndpoint = NatTraversalService.ParseEndpoint(response.TargetEndpoint);
            if (remoteEndpoint == null)
            {
                _logger.LogDebug("Invalid target endpoint in hole punch response: {Ep}",
                    response.TargetEndpoint);
                return null;
            }

            // Both sides now punch simultaneously
            var result = await _natTraversal.HolePunchAsync(punchId, remoteEndpoint).ConfigureAwait(false);
            if (result == null)
                return null;

            return new NodeConnection
            {
                NodeId = targetNode.Id,
                Type = ConnectionType.HolePunched,
                UdpClient = result.Client,
                Endpoint = result.RemoteEndPoint
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hole punch to {NodeId} failed", targetNode.Id.ToString()[..16]);
            return null;
        }
    }

    private KademliaNode? FindIntermediaryFor(KademliaNode targetNode)
    {
        // Find a node that is likely to know the target - pick from our routing table
        // excluding the target itself
        return _routingTable.GetRandomNodes(5)
            .FirstOrDefault(n => n.Id != targetNode.Id);
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
                var circuit = await _relayService.EstablishCircuitAsync(relayNode, targetNode.Id).ConfigureAwait(false);

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

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
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
    public UdpClient? UdpClient { get; init; }
    public IPEndPoint? Endpoint { get; init; }

    public KademliaNode? RelayNode { get; init; }
    public RelayCircuit? RelayCircuit { get; init; }
    public RelayService? RelayService { get; init; }

    public bool IsValid => Type switch
    {
        ConnectionType.Direct => TcpClient?.Connected ?? false,
        ConnectionType.HolePunched => UdpClient != null && Endpoint != null,
        ConnectionType.Relayed => RelayCircuit != null,
        _ => false
    };

    public async Task SendAsync(byte[] data)
    {
        LastUsed = DateTimeOffset.UtcNow;

        switch (Type)
        {
            case ConnectionType.Direct:
                await SendDirectAsync(data).ConfigureAwait(false);
                break;

            case ConnectionType.HolePunched:
                await SendUdpAsync(data).ConfigureAwait(false);
                break;

            case ConnectionType.Relayed:
                await SendRelayedAsync(data).ConfigureAwait(false);
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
                return await SendAndReceiveDirectAsync(data, timeout).ConfigureAwait(false);

            case ConnectionType.HolePunched:
                return await SendAndReceiveUdpAsync(data, timeout).ConfigureAwait(false);

            case ConnectionType.Relayed:
                return await SendAndReceiveRelayedAsync(data, timeout).ConfigureAwait(false);

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
        await stream.FlushAsync().ConfigureAwait(false);
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
        await stream.FlushAsync().ConfigureAwait(false);

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

    private async Task SendUdpAsync(byte[] data)
    {
        if (UdpClient == null || Endpoint == null)
            throw new InvalidOperationException("UDP not connected");

        // UDP framing: 4-byte length prefix + data
        var framed = new byte[4 + data.Length];
        BitConverter.TryWriteBytes(framed.AsSpan(0, 4), data.Length);
        data.CopyTo(framed, 4);

        await UdpClient.SendAsync(framed, framed.Length, Endpoint).ConfigureAwait(false);
    }

    private async Task<byte[]?> SendAndReceiveUdpAsync(byte[] data, TimeSpan timeout)
    {
        if (UdpClient == null || Endpoint == null)
            throw new InvalidOperationException("UDP not connected");

        await SendUdpAsync(data).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            var result = await UdpClient.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (result.Buffer.Length < 4)
                return null;

            var length = BitConverter.ToInt32(result.Buffer, 0);
            if (length <= 0 || length > result.Buffer.Length - 4)
                return null;

            return result.Buffer.AsSpan(4, length).ToArray();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task SendRelayedAsync(byte[] data)
    {
        if (RelayCircuit == null || RelayService == null)
            throw new InvalidOperationException("Not connected via relay");

        await RelayService.SendThroughCircuitAsync(RelayCircuit.CircuitId, data).ConfigureAwait(false);
    }

    private async Task<byte[]?> SendAndReceiveRelayedAsync(byte[] data, TimeSpan timeout)
    {
        if (RelayNode == null || RelayService == null)
            throw new InvalidOperationException("Not connected via relay");

        return await RelayService.RelayToNodeAsync(RelayNode, NodeId, data, expectResponse: true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (TcpClient != null)
        {
            TcpClient.Close();
            TcpClient.Dispose();
        }

        UdpClient?.Dispose();

        if (RelayCircuit != null && RelayService != null)
        {
            await RelayService.CloseCircuitAsync(RelayCircuit.CircuitId).ConfigureAwait(false);
        }
    }
}

public enum ConnectionType
{
    Direct,
    HolePunched,
    Relayed
}
