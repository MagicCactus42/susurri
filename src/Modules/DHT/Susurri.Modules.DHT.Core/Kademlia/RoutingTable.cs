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
        if (node.Id == LocalId)
            return AddNodeResult.Updated;

        int bucketIndex = GetBucketIndex(node.Id);
        if (bucketIndex < 0)
            return AddNodeResult.Updated;

        return _buckets[bucketIndex].TryAdd(node);
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
            allNodes.AddRange(bucket.GetNodes());
        }

        // Sort by XOR distance to target
        return allNodes
            .OrderBy(n => n.Id.DistanceTo(target).CompareTo(default))
            .Take(count)
            .ToList();
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
        return _buckets.SelectMany(b => b.GetNodes()).ToList();
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

        var bucket = nonEmptyBuckets[Random.Shared.Next(nonEmptyBuckets.Count)];
        var nodes = bucket.GetNodes();
        return nodes.Count > 0 ? nodes[Random.Shared.Next(nodes.Count)] : null;
    }

    public IReadOnlyList<KademliaNode> GetRandomNodes(int count)
    {
        var allNodes = GetAllNodes().ToList();
        if (allNodes.Count <= count) return allNodes;

        // Fisher-Yates shuffle, take first 'count'
        for (int i = allNodes.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (allNodes[i], allNodes[j]) = (allNodes[j], allNodes[i]);
        }

        return allNodes.Take(count).ToList();
    }

    private int GetBucketIndex(KademliaId nodeId)
    {
        return LocalId.GetBucketIndex(nodeId);
    }
}
