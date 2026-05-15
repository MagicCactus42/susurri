namespace Susurri.Shared.Abstractions.Health;

public enum HealthStatus
{
    Healthy,
    Unhealthy,
}

public sealed record HealthCheckResult(HealthStatus Status, string? Message = null)
{
    public static HealthCheckResult Healthy(string? message = null)
        => new(HealthStatus.Healthy, message);

    public static HealthCheckResult Unhealthy(string message)
        => new(HealthStatus.Unhealthy, message);
}

/// <summary>
/// Readiness check contributed to <c>/ready</c>. Implementations should be
/// non-blocking; treat <paramref name="ct"/> as a hard deadline.
/// </summary>
public interface IHealthCheck
{
    /// <summary>Stable identifier, surfaced in the JSON response.</summary>
    string Name { get; }

    Task<HealthCheckResult> CheckAsync(CancellationToken ct);
}
