using Susurri.Shared.Abstractions.Health;

namespace Susurri.CLI.Health;

/// <summary>
/// Readiness check: the bootstrap-mode NodeServer is started and accepting.
/// Used in bootstrap mode only — interactive sessions don't expose health.
/// </summary>
internal sealed class NodeServerRunningCheck : IHealthCheck
{
    private readonly SessionState _session;

    public NodeServerRunningCheck(SessionState session)
    {
        _session = session;
    }

    public string Name => "node-server";

    public Task<HealthCheckResult> CheckAsync(CancellationToken ct)
    {
        var node = _session.DhtNode;
        if (node is null)
            return Task.FromResult(HealthCheckResult.Unhealthy("DHT node not started"));

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
