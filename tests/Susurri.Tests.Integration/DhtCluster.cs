using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.Tests.Integration;

/// <summary>
/// In-process N-node Kademlia DHT cluster for integration tests.
/// Each node binds an ephemeral port on the loopback interface; bootstrap
/// is sequential so each new node learns about the previous ones via the
/// seed node's routing table propagation.
/// </summary>
public sealed class DhtCluster : IAsyncDisposable
{
    private readonly List<KademliaDhtNode> _nodes;
    private readonly List<NodeKeyMaterial> _keys;
    private bool _disposed;

    public IReadOnlyList<KademliaDhtNode> Nodes => _nodes;
    public IReadOnlyList<NodeKeyMaterial> Keys => _keys;

    private DhtCluster(List<KademliaDhtNode> nodes, List<NodeKeyMaterial> keys)
    {
        _nodes = nodes;
        _keys = keys;
    }

    /// <summary>
    /// Spins up <paramref name="count"/> nodes, bootstraps nodes 1..N against
    /// node 0, and waits for routing-table convergence.
    /// </summary>
    public static async Task<DhtCluster> StartAsync(
        int count,
        bool exportableKeys = false,
        TimeSpan? convergenceTimeout = null)
    {
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count), "Cluster needs ≥2 nodes");

        var nodes = new List<KademliaDhtNode>();
        var keys = new List<NodeKeyMaterial>();

        for (int i = 0; i < count; i++)
        {
            var keyMaterial = NodeKeyMaterial.Create(exportableKeys);
            keys.Add(keyMaterial);

            var node = new KademliaDhtNode(
                keyMaterial.Encryption,
                NullLogger<KademliaDhtNode>.Instance,
                keyMaterial.Signing);

            await node.StartAsync(0).ConfigureAwait(false);
            nodes.Add(node);
        }

        // Mutual all-to-all bootstrap — every node calls BootstrapAsync with
        // every other node's listening endpoint. This works around a known
        // production bug: KademliaDhtNode.HandleClientAsync writes the
        // *inbound TCP source port* (ephemeral) into the routing table for
        // the sender, not the sender's actual listening port. Mutual bootstrap
        // ensures every entry is also overwritten via the BootstrapAsync path,
        // which honors the caller-supplied (correct) endpoint. Production
        // would do a single chain-bootstrap; tests need full mesh to exercise
        // FIND_NODE / STORE / GetOffline directly between any pair of nodes.
        // See KNOWN-LIMITATIONS.md for the underlying SenderPort wire-format gap.
        for (int i = 0; i < nodes.Count; i++)
        {
            var others = nodes
                .Where((_, j) => j != i)
                .Select(LoopbackEndpoint)
                .ToArray();
            await nodes[i].BootstrapAsync(others).ConfigureAwait(false);
        }

        var cluster = new DhtCluster(nodes, keys);
        await cluster.WaitForConvergenceAsync(
            minPeersPerNode: count - 1,
            timeout: convergenceTimeout ?? TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        return cluster;
    }

    /// <summary>
    /// Polls every 100 ms until each node knows at least <paramref name="minPeersPerNode"/>
    /// peers, or the timeout fires. Returns silently on success; throws on timeout.
    /// </summary>
    public async Task WaitForConvergenceAsync(int minPeersPerNode, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_nodes.All(n => n.KnownNodes >= minPeersPerNode))
                return;

            await Task.Delay(100).ConfigureAwait(false);
        }

        var diag = string.Join(", ", _nodes.Select((n, i) => $"node[{i}]={n.KnownNodes}"));
        throw new TimeoutException(
            $"Cluster failed to converge within {timeout.TotalSeconds}s; " +
            $"required {minPeersPerNode} peers/node; observed [{diag}]");
    }

    /// <summary>
    /// Returns a connectable endpoint for the given node. The listener binds
    /// IPAddress.Any (0.0.0.0), which isn't a valid connect target — use the
    /// loopback address with the OS-assigned port instead.
    /// </summary>
    public static IPEndPoint LoopbackEndpoint(KademliaDhtNode node)
    {
        var port = node.LocalEndPoint?.Port
            ?? throw new InvalidOperationException("Node has no LocalEndPoint; was StartAsync awaited?");
        return new IPEndPoint(IPAddress.Loopback, port);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var node in _nodes)
        {
            try { await node.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
        }

        // KademliaDhtNode.DisposeAsync disposes the encryption + signing keys
        // it owns. The exportable-key copies in NodeKeyMaterial were imported
        // separately and don't share lifetime with the node.
    }
}

/// <summary>
/// Holds a node's encryption + signing keys. When created with
/// <c>exportable: true</c>, the raw private-key bytes are also captured so
/// the same identity can be re-instantiated after a node "restart".
/// </summary>
public sealed class NodeKeyMaterial
{
    public Key Encryption { get; }
    public Key Signing { get; }
    public byte[]? EncryptionPrivateBytes { get; }
    public byte[]? SigningPrivateBytes { get; }
    public byte[] EncryptionPublicKey { get; }
    public byte[] SigningPublicKey { get; }

    private NodeKeyMaterial(
        Key encryption, Key signing,
        byte[]? encryptionPrivateBytes, byte[]? signingPrivateBytes)
    {
        Encryption = encryption;
        Signing = signing;
        EncryptionPrivateBytes = encryptionPrivateBytes;
        SigningPrivateBytes = signingPrivateBytes;
        EncryptionPublicKey = encryption.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        SigningPublicKey = signing.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public static NodeKeyMaterial Create(bool exportable)
    {
        var policy = exportable
            ? KeyExportPolicies.AllowPlaintextExport
            : KeyExportPolicies.None;

        var encParams = new KeyCreationParameters { ExportPolicy = policy };
        var signParams = new KeyCreationParameters { ExportPolicy = policy };

        var encryption = Key.Create(KeyAgreementAlgorithm.X25519, encParams);
        var signing = Key.Create(SignatureAlgorithm.Ed25519, signParams);

        byte[]? encBytes = null;
        byte[]? signBytes = null;
        if (exportable)
        {
            encBytes = encryption.Export(KeyBlobFormat.RawPrivateKey);
            signBytes = signing.Export(KeyBlobFormat.RawPrivateKey);
        }

        return new NodeKeyMaterial(encryption, signing, encBytes, signBytes);
    }

    /// <summary>
    /// Reconstitutes a fresh pair of <see cref="Key"/> objects from the
    /// exported private bytes. The returned keys have an independent lifetime
    /// from the original (which may already be disposed). Throws if this
    /// material wasn't created with <c>exportable: true</c>.
    /// </summary>
    public (Key encryption, Key signing) ReimportKeys()
    {
        if (EncryptionPrivateBytes == null || SigningPrivateBytes == null)
            throw new InvalidOperationException(
                "Cannot reimport: this NodeKeyMaterial was not created with exportable=true");

        var encryption = Key.Import(KeyAgreementAlgorithm.X25519,
            EncryptionPrivateBytes, KeyBlobFormat.RawPrivateKey);
        var signing = Key.Import(SignatureAlgorithm.Ed25519,
            SigningPrivateBytes, KeyBlobFormat.RawPrivateKey);
        return (encryption, signing);
    }
}

/// <summary>
/// Forces all DHT integration tests into a single non-parallel collection so
/// multiple clusters don't fight for ports / threads / threadpool slots.
/// </summary>
[CollectionDefinition("DhtIntegration", DisableParallelization = true)]
public class DhtIntegrationCollection
{
}
