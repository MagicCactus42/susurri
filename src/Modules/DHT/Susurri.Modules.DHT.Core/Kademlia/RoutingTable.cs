using System.Security.Cryptography;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.DHT.Core.Kademlia;

// The Kademlia routing table containing 256 k-buckets.
// Each bucket i holds nodes with XOR distance in range [2^i, 2^(i+1)).
public sealed class RoutingTable
{
    private readonly KBucket[] _buckets;
    private readonly int _k;

    public KademliaId LocalId { get; }
    public int K => _k;
    public int TotalNodes => _buckets.Sum(b => b.Count);

    public RoutingTable(KademliaId localId, int k = 20)
    {
        LocalId = localId;
        _k = k;
        _buckets = new KBucket[KademliaId.BitLength];

        for (int i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new KBucket(i, k);
        }
    }

    public AddNodeResult TryAddNode(KademliaNode node)
    {
        if (!IsIdBoundToKey(node))
            return AddNodeResult.Rejected;

        if (node.Id == LocalId)
            return AddNodeResult.Updated;

        int bucketIndex = GetBucketIndex(node.Id);
        if (bucketIndex < 0)
            return AddNodeResult.Updated;

        return _buckets[bucketIndex].TryAdd(node);
    }

    private static bool IsIdBoundToKey(KademliaNode node)
    {
        return node.EncryptionPublicKey.Length == SecurityLimits.PublicKeySize
               && node.Id == KademliaId.FromPublicKey(node.EncryptionPublicKey);
    }

    public bool RemoveNode(KademliaId nodeId)
    {
        int bucketIndex = GetBucketIndex(nodeId);
        if (bucketIndex < 0) return false;

        return _buckets[bucketIndex].Remove(nodeId);
    }

    public IReadOnlyList<KademliaNode> FindClosestNodes(KademliaId target, int count = 0)
    {
        if (count <= 0) count = _k;

        var allNodes = new List<KademliaNode>();
        foreach (var bucket in _buckets)
        {
            bucket.CopyNodesTo(allNodes);
        }

        allNodes.Sort((a, b) => KademliaId.CompareDistances(a.Id, b.Id, target));

        if (allNodes.Count > count)
            allNodes.RemoveRange(count, allNodes.Count - count);
        return allNodes;
    }

    public IReadOnlyList<KademliaNode> GetBucketNodes(int bucketIndex)
    {
        if (bucketIndex < 0 || bucketIndex >= _buckets.Length)
            return Array.Empty<KademliaNode>();

        return _buckets[bucketIndex].GetNodes();
    }

    public KBucket GetBucket(KademliaId nodeId)
    {
        int index = GetBucketIndex(nodeId);
        return index >= 0 ? _buckets[index] : _buckets[0];
    }

    public KademliaNode? GetOldestNodeInBucket(KademliaId nodeId)
    {
        int index = GetBucketIndex(nodeId);
        return index >= 0 ? _buckets[index].GetOldestNode() : null;
    }

    public bool ReplaceOldestInBucket(KademliaId newNodeId, KademliaNode newNode)
    {
        int index = GetBucketIndex(newNodeId);
        return index >= 0 && _buckets[index].ReplaceOldest(newNode);
    }

    public void MarkNodeSeen(KademliaId nodeId)
    {
        int index = GetBucketIndex(nodeId);
        if (index >= 0)
        {
            _buckets[index].MarkSeen(nodeId);
        }
    }

    public IReadOnlyList<KademliaNode> GetAllNodes()
    {
        var allNodes = new List<KademliaNode>();
        foreach (var bucket in _buckets)
        {
            bucket.CopyNodesTo(allNodes);
        }
        return allNodes;
    }

    public bool ContainsNode(KademliaId nodeId)
    {
        int index = GetBucketIndex(nodeId);
        return index >= 0 && _buckets[index].Contains(nodeId);
    }

    public KademliaNode? GetRandomNode()
    {
        var nonEmptyBuckets = _buckets.Where(b => b.Count > 0).ToList();
        if (nonEmptyBuckets.Count == 0) return null;

        var bucket = nonEmptyBuckets[RandomNumberGenerator.GetInt32(nonEmptyBuckets.Count)];
        var nodes = bucket.GetNodes();
        return nodes.Count > 0 ? nodes[RandomNumberGenerator.GetInt32(nodes.Count)] : null;
    }

    public IReadOnlyList<KademliaNode> GetRandomNodes(int count)
    {
        var pool = new List<KademliaNode>();
        foreach (var bucket in _buckets)
        {
            bucket.CopyNodesTo(pool);
        }
        if (pool.Count <= count) return pool;

        // Partial Fisher-Yates: place `count` uniformly-random picks at the front.
        for (int i = 0; i < count; i++)
        {
            int j = i + RandomNumberGenerator.GetInt32(pool.Count - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        pool.RemoveRange(count, pool.Count - count);
        return pool;
    }

    private int GetBucketIndex(KademliaId nodeId)
    {
        return LocalId.GetBucketIndex(nodeId);
    }
}
