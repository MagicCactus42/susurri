namespace Susurri.Modules.DHT.Core.Kademlia.Storage;

public interface IDhtStorage
{
    void Store(KademliaId key, byte[] value, TimeSpan? ttl = null);
    byte[]? Get(KademliaId key);
    bool Contains(KademliaId key);
    bool Remove(KademliaId key);
    void StoreOfflineMessage(KademliaId recipientKeyHash, byte[] encryptedMessage, TimeSpan? ttl = null);
    IReadOnlyList<byte[]> GetOfflineMessages(KademliaId recipientKeyHash);
    int GetOfflineMessageCount(KademliaId recipientKeyHash);
    IEnumerable<(KademliaId Key, byte[] Value)> GetAllForRepublish();
    StorageStats GetStats();
}
