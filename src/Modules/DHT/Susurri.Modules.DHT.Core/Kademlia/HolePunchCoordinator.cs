using System.Net;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Kademlia.Protocol;
using Susurri.Modules.DHT.Core.NatTraversal;
using Susurri.Modules.DHT.Core.Network;

namespace Susurri.Modules.DHT.Core.Kademlia;

/// <summary>
/// Coordinates UDP hole-punch handshakes between peers. When this node is the
/// target, replies with its own public endpoint and starts its punching task.
/// When this node is an intermediary, forwards the request to the target.
/// Extracted from <see cref="KademliaDhtNode"/> to isolate NAT-traversal logic.
/// </summary>
internal sealed class HolePunchCoordinator
{
    private readonly RoutingTable _routingTable;
    private readonly KademliaId _localId;
    private readonly byte[] _encryptionPublicKey;
    private readonly Func<IPEndPoint, KademliaMessage, Task<KademliaMessage?>> _sendRequest;
    private readonly Func<string> _getPublicEndpoint;
    private readonly Action<Guid, IPEndPoint> _startPunch;
    private readonly ILogger _logger;

    public HolePunchCoordinator(
        RoutingTable routingTable,
        KademliaId localId,
        byte[] encryptionPublicKey,
        Func<IPEndPoint, KademliaMessage, Task<KademliaMessage?>> sendRequest,
        Func<string> getPublicEndpoint,
        Action<Guid, IPEndPoint> startPunch,
        ILogger logger)
    {
        _routingTable = routingTable;
        _localId = localId;
        _encryptionPublicKey = encryptionPublicKey;
        _sendRequest = sendRequest;
        _getPublicEndpoint = getPublicEndpoint;
        _startPunch = startPunch;
        _logger = logger;
    }

    /// <summary>
    /// Sends a hole-punch request through an intermediary and returns its response.
    /// Used by ConnectionManager to coordinate with a remote peer.
    /// </summary>
    public async Task<HolePunchResponseMessage?> SendRequestAsync(
        KademliaNode intermediary,
        HolePunchRequestMessage request)
    {
        var rebound = new HolePunchRequestMessage
        {
            MessageId = request.MessageId,
            SenderId = _localId,
            SenderPublicKey = _encryptionPublicKey,
            TargetNodeId = request.TargetNodeId,
            InitiatorEndpoint = request.InitiatorEndpoint,
            PunchId = request.PunchId
        };

        return await _sendRequest(intermediary.EndPoint, rebound).ConfigureAwait(false) as HolePunchResponseMessage;
    }

    public async Task<HolePunchResponseMessage> HandleAsync(
        HolePunchRequestMessage request,
        IPEndPoint sender)
    {
        if (request.TargetNodeId == _localId)
        {
            return await HandleAsTargetAsync(request).ConfigureAwait(false);
        }

        return await HandleAsIntermediaryAsync(request).ConfigureAwait(false);
    }

    private Task<HolePunchResponseMessage> HandleAsTargetAsync(HolePunchRequestMessage request)
    {
        var myEndpoint = _getPublicEndpoint();
        if (string.IsNullOrEmpty(myEndpoint))
        {
            // We don't know our own public UDP endpoint, so we can't be punched to.
            return Task.FromResult(Reject(request, accepted: false));
        }

        var remoteEndpoint = NatTraversalService.ParseEndpoint(request.InitiatorEndpoint);
        if (remoteEndpoint != null)
        {
            // Start punching toward the initiator so the hole is open by the time
            // it receives our response and starts punching back.
            _startPunch(request.PunchId, remoteEndpoint);
        }

        return Task.FromResult(new HolePunchResponseMessage
        {
            SenderId = _localId,
            SenderPublicKey = _encryptionPublicKey,
            InResponseTo = request.MessageId,
            Accepted = true,
            TargetEndpoint = myEndpoint,
            PunchId = request.PunchId
        });
    }

    private async Task<HolePunchResponseMessage> HandleAsIntermediaryAsync(HolePunchRequestMessage request)
    {
        var targetNodes = _routingTable.FindClosestNodes(request.TargetNodeId, 1);
        var targetNode = targetNodes.FirstOrDefault(n => n.Id == request.TargetNodeId);

        if (targetNode == null)
        {
            return Reject(request, accepted: false);
        }

        try
        {
            var response = await _sendRequest(targetNode.EndPoint, request).ConfigureAwait(false) as HolePunchResponseMessage;

            if (response != null)
                return response;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to forward hole punch request to target");
        }

        return Reject(request, accepted: false);
    }

    private HolePunchResponseMessage Reject(HolePunchRequestMessage request, bool accepted)
    {
        return new HolePunchResponseMessage
        {
            SenderId = _localId,
            SenderPublicKey = _encryptionPublicKey,
            InResponseTo = request.MessageId,
            Accepted = accepted,
            PunchId = request.PunchId
        };
    }
}
