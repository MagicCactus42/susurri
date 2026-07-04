using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Susurri.CLI.Commands;
using Susurri.CLI.Health;
using Susurri.Modules.DHT.Core.Node;
using Susurri.Shared.Abstractions.Health;
using Susurri.Shared.Infrastructure.Health;

namespace Susurri.CLI;

/// <summary>
/// The CLI's instance state and main loop. Owns the service provider, session
/// state, and command registry; replaces the static fields that used to live
/// on Program.cs.
/// </summary>
internal sealed class CliApplication : IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SessionState _session;
    private readonly CommandRegistry _registry;

    private CliApplication(IServiceProvider serviceProvider, SessionState session, CommandRegistry registry)
    {
        _serviceProvider = serviceProvider;
        _session = session;
        _registry = registry;
    }

    public static CliApplication Create(bool bootstrapMode)
    {
        var sp = CliHost.Build(bootstrapMode);
        var session = new SessionState();
        var registry = new CommandRegistry();

        registry.Register(new LoginCommand(sp, session));
        registry.Register(new LogoutCommand(session));
        registry.Register(new SendCommand(session));
        registry.Register(new InboxCommand(session));
        registry.Register(new StatusCommand(session));
        registry.Register(new DhtCommand(sp, session));
        registry.Register(new PingCommand(sp));
        registry.Register(new GenerateCommand(sp));
        registry.Register(new ClearCacheCommand(sp));
        registry.Register(new GroupCommand(session));
        registry.Register(new ChatsCommand(session));
        registry.Register(new ContactsCommand(session));
        registry.Register(new HistoryCommand(session));
        registry.Register(new VersionCommand());
        registry.Register(new ClearScreenCommand());
        registry.Register(new ExitCommand());
        registry.Register(new HelpCommand(registry));

        return new CliApplication(sp, session, registry);
    }

    public async Task RunInteractiveAsync(CancellationToken ct)
    {
        if (_registry.TryGet("help", out var help) && help != null)
            await help.ExecuteAsync(Array.Empty<string>(), ct).ConfigureAwait(false);

        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            ConsoleUi.PrintPrompt(_session.CurrentUser);

            string? raw;
            try
            {
                // ReadLineAsync(ct) honors the cancellation token on .NET 7+;
                // on Linux TTYs the underlying read may not unblock until the
                // user presses Enter, but the loop condition will exit cleanly.
                raw = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (raw == null) break; // EOF (stdin closed or piped input exhausted)
            var input = raw.Trim();

            if (string.IsNullOrEmpty(input))
                continue;

            try
            {
                var keepRunning = await _registry.DispatchAsync(input, ct).ConfigureAwait(false);
                if (!keepRunning)
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsoleUi.PrintError($"Command failed: {ex.Message}");
            }

            Console.WriteLine();
        }
    }

    public async Task RunSingleCommandAsync(string input, CancellationToken ct)
    {
        try
        {
            await _registry.DispatchAsync(input, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Command failed: {ex.Message}");
        }
    }

    public async Task RunBootstrapAsync(int port, CancellationToken ct)
    {
        ConsoleUi.PrintInfo("Starting in BOOTSTRAP NODE mode...");
        ConsoleUi.PrintInfo("This node will serve as a DHT bootstrap for the Susurri network.");
        ConsoleUi.PrintInfo("No identity/login required. Node operates as DHT + relay only.");
        Console.WriteLine();

        var dhtCommand = (DhtCommand)_registry.All.First(c => c is DhtCommand);
        await dhtCommand.StartAsync(new[] { port.ToString() }).ConfigureAwait(false);

        if (_session.DhtNode == null)
        {
            ConsoleUi.PrintError("Failed to start bootstrap node.");
            return;
        }

        ConsoleUi.PrintSuccess($"Bootstrap node running on port {port}");

        await using var healthServer = StartHealthServerIfEnabled();

        ConsoleUi.PrintInfo("Press Ctrl+C to stop.");
        Console.WriteLine();

        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Drain the bootstrap node before returning so the process exits cleanly.
        ConsoleUi.PrintInfo("Stopping bootstrap node...");
        await _session.DisposeAsync().ConfigureAwait(false);
        ConsoleUi.PrintInfo("Bootstrap node stopped.");
    }

    private IAsyncDisposable StartHealthServerIfEnabled()
    {
        var config = _serviceProvider.GetRequiredService<IConfiguration>();
        var enabled = config.GetValue("Health:Enabled", false);
        if (!enabled)
            return NoopAsyncDisposable.Instance;

        var address = config["Health:ListenAddress"] ?? "127.0.0.1";
        var healthPort = config.GetValue("Health:Port", 7071);

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        var checks = new IHealthCheck[] { new NodeServerRunningCheck(_session) };
        var service = new HealthCheckService(checks);
        var server = new HealthHttpServer(service, loggerFactory.CreateLogger<HealthHttpServer>(), address, healthPort);

        try
        {
            server.Start();
            ConsoleUi.PrintSuccess($"Health endpoints listening on {server.BindUrl}");
            return server;
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintWarning($"Failed to start health endpoints: {ex.Message}");
            return NoopAsyncDisposable.Instance;
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static readonly NoopAsyncDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();

        if (_serviceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();
    }
}
