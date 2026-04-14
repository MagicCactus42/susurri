using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Susurri.Modules.DHT.Core.Network;

/// <summary>
/// Tracks fire-and-forget background tasks so that:
/// (1) exceptions are caught and logged instead of vanishing into the
///     unobserved-task pool;
/// (2) <see cref="DisposeAsync"/> waits for them to finish so callers can
///     drain the work on shutdown.
/// </summary>
internal sealed class BackgroundTaskRunner : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();
    private readonly ILogger _logger;
    private bool _disposed;

    public BackgroundTaskRunner(ILogger logger)
    {
        _logger = logger;
    }

    public int ActiveCount => _tasks.Count;

    /// <summary>
    /// Runs <paramref name="work"/> on the threadpool. Exceptions are caught
    /// and logged; the task is tracked until completion. No-op if the runner
    /// is already disposed.
    /// </summary>
    public void Run(Func<Task> work, string description)
    {
        if (_disposed) return;

        var id = Guid.NewGuid();
        var task = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background task failed: {Description}", description);
            }
            finally
            {
                _tasks.TryRemove(id, out _);
            }
        });

        _tasks[id] = task;
    }

    /// <summary>
    /// Awaits in-flight tasks but races them against <paramref name="timeout"/>.
    /// Tasks that don't finish in time are abandoned (the runner is not disposed,
    /// so future Runs are still accepted; useful for staged shutdown).
    /// Returns true if every task drained cleanly, false if the timeout fired.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var snapshot = _tasks.Values.ToArray();
        if (snapshot.Length == 0) return true;

        var allTasks = Task.WhenAll(snapshot);
        var winner = await Task.WhenAny(allTasks, Task.Delay(timeout)).ConfigureAwait(false);

        if (winner != allTasks)
        {
            _logger.LogWarning(
                "Drain timeout ({Timeout}ms): {Remaining} background task(s) still in flight; abandoning",
                (int)timeout.TotalMilliseconds, _tasks.Count);
            return false;
        }

        // Surface that allTasks completed (catch any aggregated exceptions, already logged in Run)
        try { await allTasks.ConfigureAwait(false); }
        catch { }
        return true;
    }

    /// <summary>
    /// Awaits all in-flight background tasks. Subsequent calls are no-ops.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var snapshot = _tasks.Values.ToArray();
        if (snapshot.Length == 0) return;

        try
        {
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
        catch
        {
            // individual exceptions were logged in Run()
        }
    }
}
