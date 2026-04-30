using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Tests.Integration;

namespace Susurri.Tests.E2E;

/// <summary>
/// Wires up an in-memory cluster of OnionRouters whose outbound transport
/// is short-circuited via OnionRouter.TestSendOverride — bytes that would
/// normally hit a TCP socket are dispatched directly to the target router's
/// ProcessIncomingAsync. This exercises the full ProcessIncoming → HandleRelay
/// → forward → HandleFinalHop → HandleDelivery → HandleAck chain end-to-end
/// with real cryptography, only the network bytes are routed in-process.
///
/// Cluster layout: index 0 = "alice" (sender), 1..N-2 = relays, N-1 = "bob"
/// (recipient). Pick the path explicitly when building onion packets.
/// </summary>
internal sealed class OnionTestbed : IAsyncDisposable
{
    private readonly DhtCluster _cluster;
    private readonly Dictionary<int, OnionRouter> _routersByPort;
    private readonly List<OnionRouter> _routersInOrder;

    public IReadOnlyList<KademliaDhtNode> Nodes => _cluster.Nodes;
    public IReadOnlyList<OnionRouter> Routers => _routersInOrder;
    public IReadOnlyList<NodeKeyMaterial> Keys => _cluster.Keys;

    private OnionTestbed(DhtCluster cluster, Dictionary<int, OnionRouter> byPort, List<OnionRouter> inOrder)
    {
        _cluster = cluster;
        _routersByPort = byPort;
        _routersInOrder = inOrder;
    }

    public OnionRouter Alice => _routersInOrder[0];
    public KademliaDhtNode AliceNode => Nodes[0];
    public NodeKeyMaterial AliceKeys => _cluster.Keys[0];

    public OnionRouter Bob => _routersInOrder[^1];
    public KademliaDhtNode BobNode => Nodes[^1];
    public NodeKeyMaterial BobKeys => _cluster.Keys[^1];

    /// <summary>
    /// Returns the relay nodes between alice (0) and bob (N-1). Use these
    /// when building an onion path.
    /// </summary>
    public IReadOnlyList<KademliaNode> RelayPath()
    {
        // Build KademliaNode entries from the live cluster nodes. The endpoint
        // here is the listener's bound endpoint (0.0.0.0:port); TestSendOverride
        // looks them up by port number, ignoring address, so 0.0.0.0 is fine.
        var path = new List<KademliaNode>();
        for (int i = 1; i < Nodes.Count - 1; i++)
        {
            var n = Nodes[i];
            path.Add(new KademliaNode(n.LocalId, n.EncryptionPublicKey, n.LocalEndPoint!));
        }
        return path;
    }

    public static async Task<OnionTestbed> StartAsync(int count = 5)
    {
        if (count < 3)
            throw new ArgumentOutOfRangeException(nameof(count), "Need ≥3 nodes (alice + ≥1 relay + bob)");

        var cluster = await DhtCluster.StartAsync(count).ConfigureAwait(false);

        var byPort = new Dictionary<int, OnionRouter>();
        var inOrder = new List<OnionRouter>();
        foreach (var node in cluster.Nodes)
        {
            var router = new OnionRouter(
                node.EncryptionKey,
                node,
                NullLogger<OnionRouter>.Instance);
            byPort[node.LocalEndPoint!.Port] = router;
            inOrder.Add(router);
        }

        // Each router's outbound transport is replaced with an in-memory
        // dispatch that looks up the target router by destination port.
        // Captures `byPort` so every router shares the same routing table.
        foreach (var router in inOrder)
        {
            router.TestSendOverride = async (endpoint, payload) =>
            {
                if (byPort.TryGetValue(endpoint.Port, out var target))
                {
                    // Use the destination's port as the "from" address —
                    // OnionRouter.ProcessIncomingAsync ignores it apart from
                    // logging and rate-limit keying, but it must be valid.
                    await target.ProcessIncomingAsync(
                        payload,
                        new IPEndPoint(IPAddress.Loopback, endpoint.Port))
                        .ConfigureAwait(false);
                }
                // else: drop silently (mirrors prod behavior on connect failure)
            };
        }

        return new OnionTestbed(cluster, byPort, inOrder);
    }

    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Builds a self-signed ChatMessage suitable for onion routing.
/// </summary>
internal static class TestChatMessage
{
    public static ChatMessage CreateSigned(NodeKeyMaterial senderKeys, string content)
    {
        var msg = new ChatMessage
        {
            SenderPublicKey = senderKeys.EncryptionPublicKey,
            SenderSigningPublicKey = senderKeys.SigningPublicKey,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        msg.Signature = SignatureAlgorithm.Ed25519.Sign(senderKeys.Signing, msg.GetSignableData());
        return msg;
    }
}

/// <summary>
/// Captures invocations of an OnionRouter event so tests can wait for them.
/// </summary>
internal sealed class EventCapture<T>
{
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<T> All { get; } = new();

    public Task<T> First => _tcs.Task;

    public Task HandleAsync(T value)
    {
        All.Enqueue(value);
        _tcs.TrySetResult(value);
        return Task.CompletedTask;
    }

    /// <summary>Awaits the first invocation with a timeout (defaults 5s).</summary>
    public async Task<T> WaitFirstAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        cts.Token.Register(() => _tcs.TrySetException(
            new TimeoutException($"Event of type {typeof(T).Name} did not fire within timeout")));
        return await _tcs.Task.ConfigureAwait(false);
    }
}

[CollectionDefinition("OnionE2E", DisableParallelization = true)]
public class OnionE2ECollection
{
}
