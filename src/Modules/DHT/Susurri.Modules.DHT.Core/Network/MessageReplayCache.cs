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
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;
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
        Sweep();

        var now = DateTimeOffset.UtcNow;

        if (_seen.TryGetValue(messageId, out var seenAt) && now - seenAt < _ttl)
        {
            return false;
        }

        _seen[messageId] = now;

        if (_seen.Count > _capacity)
        {
            EvictOldest();
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

    private void Sweep()
    {
        if (DateTimeOffset.UtcNow - _lastSweep < SweepInterval)
            return;

        _lastSweep = DateTimeOffset.UtcNow;
        var cutoff = DateTimeOffset.UtcNow - _ttl;

        foreach (var kvp in _seen)
        {
            if (kvp.Value < cutoff)
            {
                _seen.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void EvictOldest()
    {
        var toEvict = _seen
            .OrderBy(kvp => kvp.Value)
            .Take(_seen.Count - _capacity + (_capacity / 10))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
        {
            _seen.TryRemove(key, out _);
        }
    }
}
