using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Susurri.Modules.DHT.Core.Network;
using Xunit;

namespace Susurri.Tests.Unit.Network;

public class BackgroundTaskRunnerTests
{
    [Fact]
    public async Task DrainAsync_OnEmptyRunner_ReturnsTrueImmediately()
    {
        var runner = new BackgroundTaskRunner(NullLogger.Instance);

        var drained = await runner.DrainAsync(TimeSpan.FromSeconds(1));

        drained.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainAsync_AwaitsAllInFlightTasks()
    {
        var runner = new BackgroundTaskRunner(NullLogger.Instance);
        var startedCount = 0;
        var finishedCount = 0;

        for (int i = 0; i < 10; i++)
        {
            runner.Run(async () =>
            {
                Interlocked.Increment(ref startedCount);
                await Task.Delay(50);
                Interlocked.Increment(ref finishedCount);
            }, $"task-{i}");
        }

        var drained = await runner.DrainAsync(TimeSpan.FromSeconds(5));

        drained.ShouldBeTrue();
        finishedCount.ShouldBe(10);
        runner.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task DrainAsync_ReturnsFalseWhenTimeoutFires()
    {
        var runner = new BackgroundTaskRunner(NullLogger.Instance);
        using var hang = new CancellationTokenSource();

        runner.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), hang.Token); }
            catch (OperationCanceledException) { }
        }, "hang");

        var drained = await runner.DrainAsync(TimeSpan.FromMilliseconds(100));

        drained.ShouldBeFalse();

        // cleanup so xunit doesn't hold the test runner open
        hang.Cancel();
    }

    [Fact]
    public async Task DrainAsync_DoesNotDisableRun()
    {
        var runner = new BackgroundTaskRunner(NullLogger.Instance);

        await runner.DrainAsync(TimeSpan.FromMilliseconds(100));

        var ran = false;
        runner.Run(() => { ran = true; return Task.CompletedTask; }, "after-drain");

        await runner.DrainAsync(TimeSpan.FromSeconds(2));

        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task ExceptionsInBackgroundTasksAreCaughtNotPropagated()
    {
        var runner = new BackgroundTaskRunner(NullLogger.Instance);

        runner.Run(() => throw new InvalidOperationException("boom"), "thrower");

        // DisposeAsync awaits the snapshot unconditionally (no timeout race),
        // which makes the assertion below deterministic even when the
        // threadpool is saturated by parallel test runs.
        await runner.DisposeAsync();

        runner.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var runner = new BackgroundTaskRunner(NullLogger.Instance);

        await runner.DisposeAsync();
        await runner.DisposeAsync();

        // After dispose, Run should be a no-op.
        var ran = false;
        runner.Run(() => { ran = true; return Task.CompletedTask; }, "post-dispose");
        await Task.Delay(100);

        ran.ShouldBeFalse();
    }
}
