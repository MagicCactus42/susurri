using Susurri.Shared.Abstractions.Health;

namespace Susurri.CLI.Health;

/// <summary>
/// Readiness check: the bootstrap-mode Kademlia DHT node is started and
/// listening. Used in bootstrap mode only — interactive sessions don't
/// expose health.
/// </summary>
internal sealed class NodeServerRunningCheck : IHealthCheck
{
    private readonly SessionState _session;

    public NodeServerRunningCheck(SessionState session)
    {
        _session = session;
    }

    public string Name => "dht-node";

    public Task<HealthCheckResult> CheckAsync(CancellationToken ct)
    {
        var node = _session.DhtNode;
        if (node is null)
            return Task.FromResult(HealthCheckResult.Unhealthy("DHT node not started"));

        if (!node.IsRunning)
            return Task.FromResult(HealthCheckResult.Unhealthy("DHT node not listening"));

        return Task.FromResult(HealthCheckResult.Healthy($"listening, {node.KnownNodes} peer(s) known"));
    }
}
