using Shouldly;
using Susurri.Modules.DHT.Core.Network;
using Xunit;

namespace Susurri.Tests.Unit.Network;

public class MessageReplayCacheTests
{
    [Fact]
    public void TryRecord_ReturnsTrueForNewId()
    {
        var cache = new MessageReplayCache();
        cache.TryRecord(Guid.NewGuid()).ShouldBeTrue();
    }

    [Fact]
    public void TryRecord_ReturnsFalseForReplay()
    {
        var cache = new MessageReplayCache();
        var id = Guid.NewGuid();

        cache.TryRecord(id).ShouldBeTrue();
        cache.TryRecord(id).ShouldBeFalse();
    }

    [Fact]
    public void TryRecord_DistinctIdsAllAccepted()
    {
        var cache = new MessageReplayCache();

        for (int i = 0; i < 1000; i++)
        {
            cache.TryRecord(Guid.NewGuid()).ShouldBeTrue();
        }

        cache.Count.ShouldBe(1000);
    }

    [Fact]
    public void EvictsOldestWhenOverCapacity()
    {
        var cache = new MessageReplayCache(capacity: 100);

        var first = Guid.NewGuid();
        cache.TryRecord(first).ShouldBeTrue();

        for (int i = 0; i < 200; i++)
        {
            cache.TryRecord(Guid.NewGuid()).ShouldBeTrue();
        }

        cache.Count.ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public void IsTimestampFresh_AcceptsCurrentTime()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MessageReplayCache.IsTimestampFresh(now, TimeSpan.FromMinutes(5)).ShouldBeTrue();
    }

    [Fact]
    public void IsTimestampFresh_AcceptsWithinTolerance()
    {
        var sixtySecondsAgo = DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeSeconds();
        MessageReplayCache.IsTimestampFresh(sixtySecondsAgo, TimeSpan.FromMinutes(5)).ShouldBeTrue();
    }

    [Fact]
    public void IsTimestampFresh_RejectsStale()
    {
        var tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        MessageReplayCache.IsTimestampFresh(tenMinutesAgo, TimeSpan.FromMinutes(5)).ShouldBeFalse();
    }

    [Fact]
    public void IsTimestampFresh_RejectsFuture()
    {
        var tenMinutesAhead = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        MessageReplayCache.IsTimestampFresh(tenMinutesAhead, TimeSpan.FromMinutes(5)).ShouldBeFalse();
    }
}
