using System.Net;
using System.Net.Sockets;
using Susurri.Shared.Abstractions.Security;

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

            if (_nodes.Count >= _k)
                return AddNodeResult.BucketFull;

            if (ExceedsPrefixDiversityLocked(node))
                return AddNodeResult.Rejected;

            _nodes.AddLast(node);
            return AddNodeResult.Added;
        }
    }

    private bool ExceedsPrefixDiversityLocked(KademliaNode candidate)
    {
        var address = candidate.EndPoint.Address;
        if (!IsGlobalScope(address))
            return false;

        var prefix = PrefixKey(address);
        var same = _nodes.Count(n =>
            IsGlobalScope(n.EndPoint.Address) && PrefixKey(n.EndPoint.Address) == prefix);
        return same >= SecurityLimits.MaxBucketNodesPerPrefix;
    }

    private static string PrefixKey(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var significant = address.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 3;
        return Convert.ToHexString(bytes, 0, Math.Min(significant, bytes.Length));
    }

    private static bool IsGlobalScope(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 => false,
                10 => false,
                127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => false,
                192 when bytes[1] == 168 => false,
                100 when bytes[1] >= 64 && bytes[1] <= 127 => false,
                >= 224 => false,
                _ => true
            };
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return false;
        return (bytes[0] & 0xFE) != 0xFC;
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
            if (_nodes.Count == 0)
                return Array.Empty<KademliaNode>();

            var result = new List<KademliaNode>(_nodes.Count);
            for (var node = _nodes.Last; node != null; node = node.Previous)
                result.Add(node.Value);
            return result;
        }
    }

    public void CopyNodesTo(List<KademliaNode> destination)
    {
        lock (_lock)
        {
            for (var node = _nodes.Last; node != null; node = node.Previous)
                destination.Add(node.Value);
        }
    }

    public IEnumerable<KademliaNode> GetClosest(KademliaId target, int count)
    {
        lock (_lock)
        {
            return _nodes
                .OrderBy(n => n.Id.DistanceTo(target))
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
    BucketFull,
    Rejected
}
