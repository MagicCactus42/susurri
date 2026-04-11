using System.Collections.Concurrent;
using System.Net;

namespace Susurri.Modules.DHT.Core.Network;

public sealed class RateLimiter
{
    private readonly int _maxTokens;
    private readonly double _refillRate;
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private DateTimeOffset _lastCleanup = DateTimeOffset.UtcNow;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    public RateLimiter(int maxTokens = 50, double refillRatePerSecond = 10.0)
    {
        _maxTokens = maxTokens;
        _refillRate = refillRatePerSecond;
    }

    public bool IsAllowed(IPEndPoint endpoint)
    {
        var key = endpoint.Address.ToString();
        TryCleanup();

        var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(_maxTokens, _refillRate));
        return bucket.TryConsume();
    }

    public bool IsAllowed(string ipAddress)
    {
        TryCleanup();
        var bucket = _buckets.GetOrAdd(ipAddress, _ => new TokenBucket(_maxTokens, _refillRate));
        return bucket.TryConsume();
    }

    private void TryCleanup()
    {
        if (DateTimeOffset.UtcNow - _lastCleanup < CleanupInterval)
            return;

        _lastCleanup = DateTimeOffset.UtcNow;

        var staleKeys = _buckets
            .Where(kvp => kvp.Value.IsStale(CleanupInterval))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            _buckets.TryRemove(key, out _);
        }
    }

    private sealed class TokenBucket
    {
        private double _tokens;
        private readonly int _maxTokens;
        private readonly double _refillRate;
        private DateTimeOffset _lastRefill;
        private readonly object _lock = new();

        public TokenBucket(int maxTokens, double refillRate)
        {
            _maxTokens = maxTokens;
            _refillRate = refillRate;
            _tokens = maxTokens;
            _lastRefill = DateTimeOffset.UtcNow;
        }

        public bool TryConsume()
        {
            lock (_lock)
            {
                Refill();

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return true;
                }

                return false;
            }
        }

        public bool IsStale(TimeSpan threshold)
        {
            lock (_lock)
            {
                return DateTimeOffset.UtcNow - _lastRefill > threshold;
            }
        }

        private void Refill()
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;
            _tokens = Math.Min(_maxTokens, _tokens + elapsed * _refillRate);
            _lastRefill = now;
        }
    }
}
