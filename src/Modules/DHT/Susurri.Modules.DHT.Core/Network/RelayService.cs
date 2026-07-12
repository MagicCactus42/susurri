using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.Modules.DHT.Core.Network;

// Relays messages for nodes behind NAT
public sealed class RelayService : IAsyncDisposable
{
    private readonly ILogger<RelayService> _logger;
    private readonly RoutingTable _routingTable;
    private readonly ConcurrentDictionary<Guid, RelayCircuit> _circuits = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RelayResponseMessage>> _pendingRelays = new();
    private readonly RateLimiter _requestLimiter = new(maxTokens: 60, refillRatePerSecond: 20.0);

    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;
    private bool _disposed;

    // Configuration
    private static readonly TimeSpan CircuitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RelayRequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxCircuitsPerNode = 10;
    private const int MaxTotalCircuits = 1000;
    private const long MaxBytesPerCircuit = 100 * 1024 * 1024;

    public int ActiveCircuits => _circuits.Count;

    public RelayService(RoutingTable routingTable, ILogger<RelayService> logger)
    {
        _routingTable = routingTable;
        _logger = logger;
    }

    public Task StartAsync()
    {
        _cleanupCts = new CancellationTokenSource();
        _cleanupTask = CleanupLoopAsync(_cleanupCts.Token);
        _logger.LogInformation("Relay service started");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cleanupCts?.Cancel();
        if (_cleanupTask != null)
        {
            try { await _cleanupTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _circuits.Clear();
        _logger.LogInformation("Relay service stopped");
    }

    public async Task<RelayMessage?> HandleMessageAsync(RelayMessage message, IPEndPoint sender)
    {
        if (IsRateLimitedRequest(message) && !_requestLimiter.IsAllowed(sender))
        {
            _logger.LogWarning("Rate limited relay request from {Sender}", sender);
            return null;
        }

        return message switch
        {
            CircuitRequestMessage req => await HandleCircuitRequestAsync(req, sender).ConfigureAwait(false),
            RelayDataMessage data => await HandleRelayDataAsync(data, sender).ConfigureAwait(false),
            CircuitCloseMessage close => HandleCircuitClose(close, sender),
            RelayRequestMessage relay => await HandleRelayRequestAsync(relay, sender).ConfigureAwait(false),
            RelayResponseMessage response => HandleRelayResponse(response),
            _ => null
        };
    }

    private static bool IsRateLimitedRequest(RelayMessage message)
        => message is CircuitRequestMessage or RelayDataMessage or RelayRequestMessage;

    public async Task<byte[]?> RelayToNodeAsync(
        KademliaNode relayNode,
        KademliaId targetNodeId,
        byte[] payload,
        bool expectResponse = true)
    {
        var request = new RelayRequestMessage
        {
            TargetNodeId = targetNodeId,
            Payload = payload,
            ExpectResponse = expectResponse
        };

        if (!expectResponse)
        {
            await SendRelayMessageAsync(relayNode.EndPoint, request).ConfigureAwait(false);
            return null;
        }

        var tcs = new TaskCompletionSource<RelayResponseMessage>();
        _pendingRelays[request.MessageId] = tcs;

        try
        {
            await SendRelayMessageAsync(relayNode.EndPoint, request).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(RelayRequestTimeout);
            cts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task.ConfigureAwait(false);
            return response.Success ? response.Payload : null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Relay request to {Target} via {Relay} timed out",
                targetNodeId.ToString()[..16], relayNode.Id.ToString()[..16]);
            return null;
        }
        finally
        {
            _pendingRelays.TryRemove(request.MessageId, out _);
        }
    }

    public async Task<RelayCircuit?> EstablishCircuitAsync(
        KademliaNode relayNode,
        KademliaId targetNodeId)
    {
        var circuitId = Guid.NewGuid();
        var request = new CircuitRequestMessage
        {
            CircuitId = circuitId,
            TargetNodeId = targetNodeId,
            RequestedBandwidth = 0
        };

        var responseData = await SendAndWaitAsync(relayNode.EndPoint, request).ConfigureAwait(false);
        if (responseData == null) return null;

        var response = RelayMessage.Deserialize(responseData) as CircuitResponseMessage;
        if (response == null || !response.Accepted)
        {
            _logger.LogWarning("Circuit request rejected: {Reason}", response?.RejectReason);
            return null;
        }

        IPEndPoint? targetEndpoint = null;
        if (!string.IsNullOrEmpty(response.TargetEndpoint))
        {
            var parts = response.TargetEndpoint.Split(':');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out var port))
            {
                targetEndpoint = new IPEndPoint(ip, port);
            }
        }

        var circuit = new RelayCircuit
        {
            CircuitId = circuitId,
            RequesterEndpoint = relayNode.EndPoint,
            TargetNodeId = targetNodeId,
            TargetEndpoint = targetEndpoint
        };

        _circuits[circuitId] = circuit;
        return circuit;
    }

    public async Task SendThroughCircuitAsync(Guid circuitId, byte[] data)
    {
        if (!_circuits.TryGetValue(circuitId, out var circuit))
        {
            throw new InvalidOperationException("Circuit not found");
        }

        var message = new RelayDataMessage
        {
            CircuitId = circuitId,
            Data = data
        };

        await SendRelayMessageAsync(circuit.RequesterEndpoint, message).ConfigureAwait(false);
        circuit.LastActivity = DateTimeOffset.UtcNow;
        circuit.BytesRelayed += data.Length;
    }

    public async Task CloseCircuitAsync(Guid circuitId)
    {
        if (_circuits.TryRemove(circuitId, out var circuit))
        {
            var message = new CircuitCloseMessage { CircuitId = circuitId };
            await SendRelayMessageAsync(circuit.RequesterEndpoint, message).ConfigureAwait(false);
        }
    }

    private async Task<CircuitResponseMessage> HandleCircuitRequestAsync(CircuitRequestMessage request, IPEndPoint sender)
    {
        if (_circuits.Count >= MaxTotalCircuits)
        {
            return new CircuitResponseMessage
            {
                CircuitId = request.CircuitId,
                Accepted = false,
                RejectReason = "Max circuits reached"
            };
        }

        var fromNode = _circuits.Values.Count(c => c.RequesterEndpoint.Equals(sender));
        if (fromNode >= MaxCircuitsPerNode)
        {
            return new CircuitResponseMessage
            {
                CircuitId = request.CircuitId,
                Accepted = false,
                RejectReason = "Max circuits per node reached"
            };
        }

        var targetNodes = _routingTable.FindClosestNodes(request.TargetNodeId, 1);
        var targetNode = targetNodes.FirstOrDefault(n => n.Id == request.TargetNodeId);

        string? targetEndpoint = null;
        if (targetNode != null)
        {
            targetEndpoint = $"{targetNode.EndPoint.Address}:{targetNode.EndPoint.Port}";
        }

        var circuit = new RelayCircuit
        {
            CircuitId = request.CircuitId,
            RequesterEndpoint = sender,
            TargetNodeId = request.TargetNodeId,
            TargetEndpoint = targetNode?.EndPoint
        };

        _circuits[request.CircuitId] = circuit;

        _logger.LogInformation("Created relay circuit {CircuitId} from {Requester} to {Target}",
            request.CircuitId, sender, request.TargetNodeId.ToString()[..16]);

        return new CircuitResponseMessage
        {
            CircuitId = request.CircuitId,
            Accepted = true,
            TargetEndpoint = targetEndpoint
        };
    }

    private async Task<RelayMessage?> HandleRelayDataAsync(RelayDataMessage data, IPEndPoint sender)
    {
        if (!_circuits.TryGetValue(data.CircuitId, out var circuit))
        {
            _logger.LogWarning("Relay data for unknown circuit {CircuitId}", data.CircuitId);
            return null;
        }

        if (circuit.BytesRelayed + data.Data.Length > MaxBytesPerCircuit)
        {
            _logger.LogWarning("Circuit {CircuitId} exceeded byte limit", data.CircuitId);
            _circuits.TryRemove(data.CircuitId, out _);
            return new CircuitCloseMessage { CircuitId = data.CircuitId };
        }

        circuit.LastActivity = DateTimeOffset.UtcNow;
        circuit.BytesRelayed += data.Data.Length;

        if (sender.Equals(circuit.RequesterEndpoint))
        {
            if (circuit.TargetEndpoint != null)
            {
                await SendRawAsync(circuit.TargetEndpoint, data.Data).ConfigureAwait(false);
            }
        }
        else
        {
            await SendRawAsync(circuit.RequesterEndpoint, data.Data).ConfigureAwait(false);
        }

        return null;
    }

    private RelayMessage? HandleCircuitClose(CircuitCloseMessage close, IPEndPoint sender)
    {
        if (_circuits.TryRemove(close.CircuitId, out var circuit))
        {
            _logger.LogInformation("Circuit {CircuitId} closed", close.CircuitId);
        }
        return null;
    }

    private async Task<RelayResponseMessage> HandleRelayRequestAsync(RelayRequestMessage request, IPEndPoint sender)
    {
        var targetNodes = _routingTable.FindClosestNodes(request.TargetNodeId, 1);
        var targetNode = targetNodes.FirstOrDefault(n => n.Id == request.TargetNodeId);

        if (targetNode == null)
        {
            return new RelayResponseMessage
            {
                InResponseTo = request.MessageId,
                Success = false,
                Error = "Target node not found"
            };
        }

        try
        {
            if (request.ExpectResponse)
            {
                var response = await SendAndWaitAsync(targetNode.EndPoint, request.Payload).ConfigureAwait(false);
                return new RelayResponseMessage
                {
                    InResponseTo = request.MessageId,
                    Success = response != null,
                    Payload = response ?? Array.Empty<byte>(),
                    Error = response == null ? "No response from target" : null
                };
            }
            else
            {
                await SendRawAsync(targetNode.EndPoint, request.Payload).ConfigureAwait(false);
                return new RelayResponseMessage
                {
                    InResponseTo = request.MessageId,
                    Success = true
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to relay to {Target}", targetNode.EndPoint);
            return new RelayResponseMessage
            {
                InResponseTo = request.MessageId,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private RelayMessage? HandleRelayResponse(RelayResponseMessage response)
    {
        if (_pendingRelays.TryRemove(response.InResponseTo, out var tcs))
        {
            tcs.TrySetResult(response);
        }
        return null;
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);

                var expiredCircuits = _circuits.Values
                    .Where(c => c.IsExpired(CircuitTimeout))
                    .Select(c => c.CircuitId)
                    .ToList();

                foreach (var circuitId in expiredCircuits)
                {
                    if (_circuits.TryRemove(circuitId, out var circuit))
                    {
                        _logger.LogDebug("Expired circuit {CircuitId}", circuitId);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in relay cleanup");
            }
        }
    }

    private async Task SendRelayMessageAsync(IPEndPoint endpoint, RelayMessage message)
    {
        var data = message.Serialize();
        await SendRawAsync(endpoint, data).ConfigureAwait(false);
    }

    private async Task SendRawAsync(IPEndPoint endpoint, byte[] data)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);

            using var stream = client.GetStream();
            using var writer = new BinaryWriter(stream);

            writer.Write(data.Length);
            writer.Write(data);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send to {Endpoint}", endpoint);
            throw;
        }
    }

    private async Task<byte[]?> SendAndWaitAsync(IPEndPoint endpoint, RelayMessage message)
    {
        return await SendAndWaitAsync(endpoint, message.Serialize()).ConfigureAwait(false);
    }

    private async Task<byte[]?> SendAndWaitAsync(IPEndPoint endpoint, byte[] data)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);

            using var stream = client.GetStream();
            using var writer = new BinaryWriter(stream);
            using var reader = new BinaryReader(stream);

            writer.Write(data.Length);
            writer.Write(data);

            using var cts = new CancellationTokenSource(RelayRequestTimeout);

            var lengthTask = reader.ReadInt32Async(cts.Token);
            var length = await lengthTask.ConfigureAwait(false);
            return reader.ReadBytes(length);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Request to {Endpoint} failed", endpoint);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }
}

internal static class BinaryReaderExtensions
{
    public static async Task<int> ReadInt32Async(this BinaryReader reader, CancellationToken ct)
    {
        var bytes = new byte[4];
        var stream = reader.BaseStream;
        var read = await stream.ReadAsync(bytes, 0, 4, ct).ConfigureAwait(false);
        if (read < 4) throw new EndOfStreamException();
        return BitConverter.ToInt32(bytes, 0);
    }
}
