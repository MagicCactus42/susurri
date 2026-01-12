using System.Net;

namespace Susurri.Modules.DHT.Core.Kademlia;

public sealed class KademliaNode : IEquatable<KademliaNode>
{
    public KademliaId Id { get; }
    public byte[] EncryptionPublicKey { get; }
    public IPEndPoint EndPoint { get; }
    public DateTimeOffset LastSeen { get; private set; }
    public int FailureCount { get; private set; }

    public KademliaNode(KademliaId id, byte[] encryptionPublicKey, IPEndPoint endPoint)
    {
        Id = id;
        EncryptionPublicKey = encryptionPublicKey ?? throw new ArgumentNullException(nameof(encryptionPublicKey));
        EndPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
        LastSeen = DateTimeOffset.UtcNow;
        FailureCount = 0;
    }

    public static KademliaNode Create(byte[] encryptionPublicKey, IPEndPoint endPoint)
    {
        var id = KademliaId.FromPublicKey(encryptionPublicKey);
        return new KademliaNode(id, encryptionPublicKey, endPoint);
    }

    public void MarkSeen()
    {
        LastSeen = DateTimeOffset.UtcNow;
        FailureCount = 0;
    }

    public void MarkFailed()
    {
        FailureCount++;
    }

    public bool IsStale(TimeSpan staleThreshold)
    {
        return DateTimeOffset.UtcNow - LastSeen > staleThreshold;
    }

    public bool Equals(KademliaNode? other)
    {
        if (other is null) return false;
        return Id == other.Id;
    }

    public override bool Equals(object? obj) => obj is KademliaNode node && Equals(node);

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => $"Node({Id.ToString()[..16]}...@{EndPoint})";
}
