using System.Collections.Concurrent;

namespace Susurri.Modules.DHT.Core.Network;

/// <summary>
/// Tracks recently seen message IDs to drop replayed packets.
/// Bounded by both time (TTL) and capacity (oldest evicted first).
/// </summary>
public sealed class MessageReplayCache
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private long _lastSweepTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int _count;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public MessageReplayCache(TimeSpan? ttl = null, int capacity = 100_000)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _capacity = capacity;
    }

    /// <summary>
    /// Returns false if the messageId has been seen within the TTL window;
    /// true otherwise (and records it as seen).
    /// </summary>
    public bool TryRecord(Guid messageId)
    {
        MaybeSweep();

        var now = DateTimeOffset.UtcNow;

        if (_seen.TryGetValue(messageId, out var seenAt))
        {
            if (now - seenAt < _ttl)
                return false;

            _seen[messageId] = now;
            return true;
        }

        if (_seen.TryAdd(messageId, now))
        {
            if (Interlocked.Increment(ref _count) > _capacity)
                EvictOldest();
        }
        else
        {
            _seen[messageId] = now;
        }

        return true;
    }

    /// <summary>
    /// Validates that the timestamp is within ±tolerance of now. Used in conjunction
    /// with TryRecord to bound the replay window for messages that carry a timestamp.
    /// </summary>
    public static bool IsTimestampFresh(long unixSecondsTimestamp, TimeSpan tolerance)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var delta = Math.Abs(now - unixSecondsTimestamp);
        return delta <= (long)tolerance.TotalSeconds;
    }

    public int Count => _seen.Count;

    private void MaybeSweep()
    {
        var now = DateTimeOffset.UtcNow;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < SweepInterval.Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
            return;

        PruneExpired(now);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var cutoff = now - _ttl;
        foreach (var kvp in _seen)
        {
            if (kvp.Value < cutoff && _seen.TryRemove(kvp.Key, out _))
                Interlocked.Decrement(ref _count);
        }
    }

    private void EvictOldest()
    {
        PruneExpired(DateTimeOffset.UtcNow);

        var surplus = Volatile.Read(ref _count) - _capacity + (_capacity / 10);
        if (surplus <= 0)
            return;

        var oldest = _seen
            .OrderBy(kvp => kvp.Value)
            .Take(surplus)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldest)
        {
            if (_seen.TryRemove(key, out _))
                Interlocked.Decrement(ref _count);
        }
    }
}
