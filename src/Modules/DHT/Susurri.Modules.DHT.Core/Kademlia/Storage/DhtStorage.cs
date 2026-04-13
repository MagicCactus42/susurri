using System.Collections.Concurrent;

namespace Susurri.Modules.DHT.Core.Kademlia.Storage;

public sealed class DhtStorage : IDhtStorage
{
    private readonly ConcurrentDictionary<KademliaId, StoredValue> _store = new();
    private readonly ConcurrentDictionary<KademliaId, List<OfflineMessage>> _offlineMessages = new();
    private readonly object _cleanupLock = new();
    private DateTimeOffset _lastCleanup = DateTimeOffset.UtcNow;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private const int MaxStoredValues = 10_000;
    private const int MaxOfflineRecipients = 5_000;
    private const long MaxTotalStorageBytes = 256 * 1024 * 1024; // 256 MB
    private long _estimatedTotalBytes;

    public void Store(KademliaId key, byte[] value, TimeSpan? ttl = null)
    {
        if (_store.Count >= MaxStoredValues || Interlocked.Read(ref _estimatedTotalBytes) >= MaxTotalStorageBytes)
        {
            TryCleanup();
            if (_store.Count >= MaxStoredValues
                || Interlocked.Read(ref _estimatedTotalBytes) + value.Length > MaxTotalStorageBytes)
                return;
        }

        var expiry = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : (DateTimeOffset?)null;
        var newStored = new StoredValue(value, expiry);

        _store.AddOrUpdate(
            key,
            _ =>
            {
                Interlocked.Add(ref _estimatedTotalBytes, value.Length);
                return newStored;
            },
            (_, existing) =>
            {
                Interlocked.Add(ref _estimatedTotalBytes, value.Length - existing.Value.Length);
                return newStored;
            });

        TryCleanup();
    }

    public byte[]? Get(KademliaId key)
    {
        TryCleanup();

        if (_store.TryGetValue(key, out var stored))
        {
            if (!stored.IsExpired)
            {
                return stored.Value;
            }

            if (_store.TryRemove(key, out var removed))
            {
                Interlocked.Add(ref _estimatedTotalBytes, -removed.Value.Length);
            }
        }

        return null;
    }

    public bool Contains(KademliaId key)
    {
        if (_store.TryGetValue(key, out var stored))
        {
            if (!stored.IsExpired)
            {
                return true;
            }

            if (_store.TryRemove(key, out var removed))
            {
                Interlocked.Add(ref _estimatedTotalBytes, -removed.Value.Length);
            }
        }

        return false;
    }

    public bool Remove(KademliaId key)
    {
        if (_store.TryRemove(key, out var removed))
        {
            Interlocked.Add(ref _estimatedTotalBytes, -removed.Value.Length);
            return true;
        }
        return false;
    }

    public void StoreOfflineMessage(KademliaId recipientKeyHash, byte[] encryptedMessage, TimeSpan? ttl = null)
    {
        if (_offlineMessages.Count >= MaxOfflineRecipients
            || Interlocked.Read(ref _estimatedTotalBytes) >= MaxTotalStorageBytes)
        {
            TryCleanup();
            if (_offlineMessages.Count >= MaxOfflineRecipients)
                return;
        }

        var expiry = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : DateTimeOffset.UtcNow.AddDays(7);
        var message = new OfflineMessage(encryptedMessage, DateTimeOffset.UtcNow, expiry);

        _offlineMessages.AddOrUpdate(
            recipientKeyHash,
            _ => new List<OfflineMessage> { message },
            (_, list) =>
            {
                lock (list)
                {
                    if (list.Count < 100)
                    {
                        list.Add(message);
                    }
                }
                return list;
            });

        Interlocked.Add(ref _estimatedTotalBytes, encryptedMessage.Length);
        TryCleanup();
    }

    public IReadOnlyList<byte[]> GetOfflineMessages(KademliaId recipientKeyHash)
    {
        TryCleanup();

        if (_offlineMessages.TryRemove(recipientKeyHash, out var messages))
        {
            lock (messages)
            {
                long releasedBytes = messages.Sum(m => (long)m.Data.Length);
                Interlocked.Add(ref _estimatedTotalBytes, -releasedBytes);

                var validMessages = messages
                    .Where(m => !m.IsExpired)
                    .OrderBy(m => m.StoredAt)
                    .Select(m => m.Data)
                    .ToList();
                return validMessages;
            }
        }

        return Array.Empty<byte[]>();
    }

    public int GetOfflineMessageCount(KademliaId recipientKeyHash)
    {
        if (_offlineMessages.TryGetValue(recipientKeyHash, out var messages))
        {
            lock (messages)
            {
                return messages.Count(m => !m.IsExpired);
            }
        }
        return 0;
    }

    public IEnumerable<(KademliaId Key, byte[] Value)> GetAllForRepublish()
    {
        TryCleanup();

        foreach (var kvp in _store)
        {
            if (!kvp.Value.IsExpired)
            {
                yield return (kvp.Key, kvp.Value.Value);
            }
        }
    }

    public StorageStats GetStats()
    {
        int valueCount = 0;
        int offlineMessageCount = 0;
        long totalBytes = 0;

        foreach (var kvp in _store)
        {
            if (!kvp.Value.IsExpired)
            {
                valueCount++;
                totalBytes += kvp.Value.Value.Length;
            }
        }

        foreach (var kvp in _offlineMessages)
        {
            lock (kvp.Value)
            {
                offlineMessageCount += kvp.Value.Count(m => !m.IsExpired);
                totalBytes += kvp.Value.Where(m => !m.IsExpired).Sum(m => m.Data.Length);
            }
        }

        return new StorageStats(valueCount, offlineMessageCount, totalBytes);
    }

    private void TryCleanup()
    {
        if (DateTimeOffset.UtcNow - _lastCleanup < CleanupInterval)
            return;

        lock (_cleanupLock)
        {
            if (DateTimeOffset.UtcNow - _lastCleanup < CleanupInterval)
                return;

            var expiredKeys = _store
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (_store.TryRemove(key, out var removed))
                {
                    Interlocked.Add(ref _estimatedTotalBytes, -removed.Value.Length);
                }
            }

            foreach (var kvp in _offlineMessages)
            {
                lock (kvp.Value)
                {
                    long expiredBytes = kvp.Value.Where(m => m.IsExpired).Sum(m => (long)m.Data.Length);
                    if (expiredBytes > 0)
                    {
                        Interlocked.Add(ref _estimatedTotalBytes, -expiredBytes);
                    }
                    kvp.Value.RemoveAll(m => m.IsExpired);
                }
            }

            var emptyLists = _offlineMessages
                .Where(kvp => kvp.Value.Count == 0)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in emptyLists)
            {
                _offlineMessages.TryRemove(key, out _);
            }

            // Self-healing safety net: recompute the total every cleanup pass to correct any drift.
            Interlocked.Exchange(ref _estimatedTotalBytes, RecomputeTotalBytes());

            _lastCleanup = DateTimeOffset.UtcNow;
        }
    }

    private long RecomputeTotalBytes()
    {
        long total = 0;
        foreach (var kvp in _store)
        {
            if (!kvp.Value.IsExpired)
                total += kvp.Value.Value.Length;
        }
        foreach (var kvp in _offlineMessages)
        {
            lock (kvp.Value)
            {
                foreach (var m in kvp.Value)
                {
                    if (!m.IsExpired)
                        total += m.Data.Length;
                }
            }
        }
        return total;
    }

    private sealed record StoredValue(byte[] Value, DateTimeOffset? Expiry)
    {
        public bool IsExpired => Expiry.HasValue && DateTimeOffset.UtcNow > Expiry.Value;
    }

    private sealed record OfflineMessage(byte[] Data, DateTimeOffset StoredAt, DateTimeOffset Expiry)
    {
        public bool IsExpired => DateTimeOffset.UtcNow > Expiry;
    }
}

public sealed record StorageStats(int ValueCount, int OfflineMessageCount, long TotalBytes);
