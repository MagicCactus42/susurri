using Susurri.Shared.Abstractions.Health;

namespace Susurri.Shared.Infrastructure.Health;

public sealed record HealthReport(
    HealthStatus Overall,
    IReadOnlyDictionary<string, HealthCheckResult> Checks);

/// <summary>
/// Aggregates registered <see cref="IHealthCheck"/> implementations into the
/// overall readiness picture surfaced at <c>/ready</c>.
/// </summary>
public sealed class HealthCheckService
{
    private readonly IReadOnlyList<IHealthCheck> _checks;

    public HealthCheckService(IEnumerable<IHealthCheck> checks)
    {
        _checks = checks.ToArray();
    }

    /// <summary>
    /// Liveness — true as long as the process can return a response. The HTTP
    /// server itself answers <c>/health</c> without invoking the checks, but
    /// callers in-process can use this to mirror the contract.
    /// </summary>
    public static HealthReport Alive => new(HealthStatus.Healthy,
        new Dictionary<string, HealthCheckResult> { ["process"] = HealthCheckResult.Healthy() });

    /// <summary>Readiness — all registered checks must report healthy.</summary>
    public async Task<HealthReport> CheckReadyAsync(CancellationToken ct)
    {
        var results = new Dictionary<string, HealthCheckResult>();
        foreach (var check in _checks)
        {
            HealthCheckResult result;
            try
            {
                result = await check.CheckAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = HealthCheckResult.Unhealthy($"check threw: {ex.GetType().Name}: {ex.Message}");
            }
            results[check.Name] = result;
        }

        var overall = results.Values.All(r => r.Status == HealthStatus.Healthy)
            ? HealthStatus.Healthy
            : HealthStatus.Unhealthy;

        return new HealthReport(overall, results);
    }
}
