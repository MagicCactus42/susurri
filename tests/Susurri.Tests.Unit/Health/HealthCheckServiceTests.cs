using Shouldly;
using Susurri.Shared.Abstractions.Health;
using Susurri.Shared.Infrastructure.Health;

namespace Susurri.Tests.Unit.Health;

public class HealthCheckServiceTests
{
    [Fact]
    public async Task CheckReadyAsync_AllChecksHealthy_ReturnsHealthyOverall()
    {
        var sut = new HealthCheckService(new IHealthCheck[]
        {
            new FakeCheck("db", HealthCheckResult.Healthy()),
            new FakeCheck("dht", HealthCheckResult.Healthy()),
        });

        var report = await sut.CheckReadyAsync(CancellationToken.None);

        report.Overall.ShouldBe(HealthStatus.Healthy);
        report.Checks.Keys.ShouldBe(new[] { "db", "dht" }, ignoreOrder: true);
    }

    [Fact]
    public async Task CheckReadyAsync_AnyCheckUnhealthy_ReturnsUnhealthyOverall()
    {
        var sut = new HealthCheckService(new IHealthCheck[]
        {
            new FakeCheck("db", HealthCheckResult.Healthy()),
            new FakeCheck("dht", HealthCheckResult.Unhealthy("listener down")),
        });

        var report = await sut.CheckReadyAsync(CancellationToken.None);

        report.Overall.ShouldBe(HealthStatus.Unhealthy);
        report.Checks["dht"].Message.ShouldBe("listener down");
    }

    [Fact]
    public async Task CheckReadyAsync_ThrowingCheck_RecordedAsUnhealthy_DoesNotAbortOthers()
    {
        var sut = new HealthCheckService(new IHealthCheck[]
        {
            new ThrowingCheck("explodes"),
            new FakeCheck("ok", HealthCheckResult.Healthy()),
        });

        var report = await sut.CheckReadyAsync(CancellationToken.None);

        report.Overall.ShouldBe(HealthStatus.Unhealthy);
        report.Checks["explodes"].Status.ShouldBe(HealthStatus.Unhealthy);
        report.Checks["explodes"].Message!.ShouldContain("InvalidOperationException");
        report.Checks["ok"].Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckReadyAsync_NoChecks_ReturnsHealthyOverall()
    {
        // Liveness-equivalent — no readiness gates means we're ready by default.
        var sut = new HealthCheckService(Array.Empty<IHealthCheck>());

        var report = await sut.CheckReadyAsync(CancellationToken.None);

        report.Overall.ShouldBe(HealthStatus.Healthy);
        report.Checks.ShouldBeEmpty();
    }

    [Fact]
    public void Alive_StaticProperty_ReportsHealthyProcess()
    {
        HealthCheckService.Alive.Overall.ShouldBe(HealthStatus.Healthy);
        HealthCheckService.Alive.Checks.ShouldContainKey("process");
    }

    private sealed class FakeCheck : IHealthCheck
    {
        private readonly HealthCheckResult _result;
        public FakeCheck(string name, HealthCheckResult result) { Name = name; _result = result; }
        public string Name { get; }
        public Task<HealthCheckResult> CheckAsync(CancellationToken ct) => Task.FromResult(_result);
    }

    private sealed class ThrowingCheck : IHealthCheck
    {
        public ThrowingCheck(string name) { Name = name; }
        public string Name { get; }
        public Task<HealthCheckResult> CheckAsync(CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
