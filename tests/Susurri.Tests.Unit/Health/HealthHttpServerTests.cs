using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Susurri.Shared.Abstractions.Health;
using Susurri.Shared.Infrastructure.Health;

namespace Susurri.Tests.Unit.Health;

// End-to-end behavioral tests: launch the HTTP server on an ephemeral port,
// hit it with HttpClient, assert the response codes and JSON payloads.
public class HealthHttpServerTests
{
    [Fact]
    public async Task HealthEndpoint_ReturnsAlive_WithoutInvokingChecks()
    {
        var checkInvoked = false;
        var service = new HealthCheckService(new IHealthCheck[]
        {
            new TrackingCheck("ignored", () => { checkInvoked = true; }),
        });

        await using var server = StartServer(service);

        using var http = new HttpClient { BaseAddress = new Uri(server.BindUrl) };
        var response = await http.GetAsync("health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("alive");
        checkInvoked.ShouldBeFalse("liveness must not depend on readiness checks");
    }

    [Fact]
    public async Task ReadyEndpoint_AllChecksHealthy_Returns200()
    {
        var service = new HealthCheckService(new IHealthCheck[]
        {
            new FakeCheck("db", HealthCheckResult.Healthy()),
            new FakeCheck("dht", HealthCheckResult.Healthy()),
        });

        await using var server = StartServer(service);
        using var http = new HttpClient { BaseAddress = new Uri(server.BindUrl) };

        var response = await http.GetAsync("ready");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetString().ShouldBe("ready");
        json.RootElement.GetProperty("checks").GetProperty("db").GetProperty("status").GetString().ShouldBe("healthy");
        json.RootElement.GetProperty("checks").GetProperty("dht").GetProperty("status").GetString().ShouldBe("healthy");
    }

    [Fact]
    public async Task ReadyEndpoint_AnyCheckUnhealthy_Returns503()
    {
        var service = new HealthCheckService(new IHealthCheck[]
        {
            new FakeCheck("db", HealthCheckResult.Unhealthy("connection refused")),
        });

        await using var server = StartServer(service);
        using var http = new HttpClient { BaseAddress = new Uri(server.BindUrl) };

        var response = await http.GetAsync("ready");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetString().ShouldBe("not-ready");
        json.RootElement.GetProperty("checks").GetProperty("db").GetProperty("message").GetString()
            .ShouldBe("connection refused");
    }

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        var service = new HealthCheckService(Array.Empty<IHealthCheck>());
        await using var server = StartServer(service);
        using var http = new HttpClient { BaseAddress = new Uri(server.BindUrl) };

        var response = await http.GetAsync("not-an-endpoint");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BindsToLoopbackByDefault()
    {
        var service = new HealthCheckService(Array.Empty<IHealthCheck>());
        await using var server = StartServer(service);

        server.BindUrl.ShouldStartWith("http://127.0.0.1:");
    }

    private static HealthHttpServer StartServer(HealthCheckService service)
    {
        var port = FindFreePort();
        var server = new HealthHttpServer(service, NullLogger<HealthHttpServer>.Instance, "127.0.0.1", port);
        server.Start();
        return server;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FakeCheck : IHealthCheck
    {
        private readonly HealthCheckResult _result;
        public FakeCheck(string name, HealthCheckResult result) { Name = name; _result = result; }
        public string Name { get; }
        public Task<HealthCheckResult> CheckAsync(CancellationToken ct) => Task.FromResult(_result);
    }

    private sealed class TrackingCheck : IHealthCheck
    {
        private readonly Action _onInvoke;
        public TrackingCheck(string name, Action onInvoke) { Name = name; _onInvoke = onInvoke; }
        public string Name { get; }
        public Task<HealthCheckResult> CheckAsync(CancellationToken ct)
        {
            _onInvoke();
            return Task.FromResult(HealthCheckResult.Healthy());
        }
    }
}
