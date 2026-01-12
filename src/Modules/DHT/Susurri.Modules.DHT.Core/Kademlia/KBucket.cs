namespace Susurri.Modules.DHT.Core.Kademlia;

// A k-bucket holds up to k nodes at a specific distance range.
// Implements LRU eviction: least recently seen nodes are candidates for replacement.
public sealed class KBucket
{
    private readonly LinkedList<KademliaNode> _nodes;
    private readonly int _k;
    private readonly object _lock = new();

    public int Index { get; }
    public int Capacity => _k;

    public int Count
    {
        get
        {
            lock (_lock) return _nodes.Count;
        }
    }

    public bool IsFull
    {
        get
        {
            lock (_lock) return _nodes.Count >= _k;
        }
    }

    public KBucket(int index, int k = 20)
    {
        Index = index;
        _k = k;
        _nodes = new LinkedList<KademliaNode>();
    }

    public AddNodeResult TryAdd(KademliaNode node)
    {
        lock (_lock)
        {
            var existingNode = _nodes.FirstOrDefault(n => n.Id == node.Id);
            if (existingNode != null)
            {
                // Move to end (most recently seen) per Kademlia LRU
                _nodes.Remove(existingNode);
                existingNode.MarkSeen();
                _nodes.AddLast(existingNode);
                return AddNodeResult.Updated;
            }

            if (_nodes.Count < _k)
            {
                _nodes.AddLast(node);
                return AddNodeResult.Added;
            }

            return AddNodeResult.BucketFull;
        }
    }

    public KademliaNode? GetOldestNode()
    {
        lock (_lock)
        {
            return _nodes.First?.Value;
        }
    }

    public bool ReplaceOldest(KademliaNode newNode)
    {
        lock (_lock)
        {
            if (_nodes.Count == 0) return false;

            _nodes.RemoveFirst();
            _nodes.AddLast(newNode);
            return true;
        }
    }

    public bool Remove(KademliaId nodeId)
    {
        lock (_lock)
        {
            var node = _nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;
            _nodes.Remove(node);
            return true;
        }
    }

    public IReadOnlyList<KademliaNode> GetNodes()
    {
        lock (_lock)
        {
            return _nodes.Reverse().ToList();
        }
    }

    public IEnumerable<KademliaNode> GetClosest(KademliaId target, int count)
    {
        lock (_lock)
        {
            return _nodes
                .OrderBy(n => n.Id.DistanceTo(target).CompareTo(default))
                .Take(count)
                .ToList();
        }
    }

    public void MarkSeen(KademliaId nodeId)
    {
        lock (_lock)
        {
            var node = _nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node != null)
            {
                _nodes.Remove(node);
                node.MarkSeen();
                _nodes.AddLast(node);
            }
        }
    }

    public bool Contains(KademliaId nodeId)
    {
        lock (_lock)
        {
            return _nodes.Any(n => n.Id == nodeId);
        }
    }
}

public enum AddNodeResult
{
    Added,
    Updated,
    BucketFull
}
