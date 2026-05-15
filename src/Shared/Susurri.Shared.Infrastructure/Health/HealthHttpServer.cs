using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Susurri.Shared.Abstractions.Health;

namespace Susurri.Shared.Infrastructure.Health;

/// <summary>
/// Lightweight HTTP health endpoint server backed by <see cref="HttpListener"/>.
/// Exposes <c>/health</c> (liveness, always 200) and <c>/ready</c> (readiness,
/// 200 when all <see cref="IHealthCheck"/>s pass, 503 otherwise).
///
/// Binds to localhost by default — only opt-in to a routable address for
/// container orchestration via <c>Health:ListenAddress</c>. <see cref="HttpListener"/>
/// was chosen over Kestrel to avoid pulling in ASP.NET Core for two endpoints.
/// </summary>
public sealed class HealthHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly HealthCheckService _checkService;
    private readonly ILogger<HealthHttpServer> _logger;
    private readonly string _bindUrl;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    public HealthHttpServer(
        HealthCheckService checkService,
        ILogger<HealthHttpServer> logger,
        string listenAddress = "127.0.0.1",
        int port = 7071)
    {
        _checkService = checkService;
        _logger = logger;
        _bindUrl = $"http://{listenAddress}:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_bindUrl);
    }

    public string BindUrl => _bindUrl;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _logger.LogInformation("Health endpoints listening on {BindUrl}", _bindUrl);
        _runLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Serial request handling: health endpoints are low-throughput (one
        // probe every N seconds) and serial handling means no in-flight
        // handler tasks holding references to a closed listener at shutdown.
        // If/when throughput matters, switch to a tracked-task model that
        // drains in DisposeAsync rather than fire-and-forget.
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            await HandleRequestAsync(context, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            switch (path)
            {
                case "/health":
                    await WriteJsonAsync(context.Response, 200,
                        "{\"status\":\"alive\"}", ct).ConfigureAwait(false);
                    break;

                case "/ready":
                    var report = await _checkService.CheckReadyAsync(ct).ConfigureAwait(false);
                    var statusCode = report.Overall == HealthStatus.Healthy ? 200 : 503;
                    var body = SerializeReadyResponse(report);
                    await WriteJsonAsync(context.Response, statusCode, body, ct).ConfigureAwait(false);
                    break;

                default:
                    await WriteJsonAsync(context.Response, 404,
                        "{\"error\":\"not-found\"}", ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling health request");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { /* response already closed; nothing to do */ }
        }
    }

    private static string SerializeReadyResponse(HealthReport report)
    {
        var payload = new
        {
            status = report.Overall == HealthStatus.Healthy ? "ready" : "not-ready",
            checks = report.Checks.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    status = kvp.Value.Status.ToString().ToLowerInvariant(),
                    message = kvp.Value.Message,
                }),
        };
        return JsonSerializer.Serialize(payload);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, string body, CancellationToken ct)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;

        _cts.Cancel();
        try { _listener.Stop(); } catch { /* listener may already be stopped */ }

        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); } catch { /* loop exited via cancellation */ }
        }

        _listener.Close();
        _cts.Dispose();
        _cts = null;
    }
}
