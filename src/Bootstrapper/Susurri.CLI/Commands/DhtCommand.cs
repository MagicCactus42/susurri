using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.CLI.Commands;

internal sealed class DhtCommand : ICommand
{
    private readonly IServiceProvider _services;
    private readonly SessionState _session;

    public string Name => "dht";
    public string Description => "DHT node management";
    public string HelpLine => "  dht <command>        - DHT node management (see 'dht help')";

    public DhtCommand(IServiceProvider services, SessionState session)
    {
        _services = services;
        _session = session;
    }

    public async Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return true;
        }

        var subcommand = args[0].ToLowerInvariant();

        switch (subcommand)
        {
            case "start":
            case "deploy":
                await StartAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
                break;

            case "stop":
                await StopAsync().ConfigureAwait(false);
                break;

            case "status":
                PrintStatus();
                break;

            case "help":
                PrintHelp();
                break;

            default:
                ConsoleUi.PrintWarning($"Unknown DHT command: {subcommand}");
                PrintHelp();
                break;
        }

        return true;
    }

    public async Task StartAsync(string[] args)
    {
        if (_session.DhtNode != null)
        {
            ConsoleUi.PrintWarning("DHT node is already running.");
            return;
        }

        var port = 7070;
        if (args.Length > 0 && int.TryParse(args[0], out var customPort))
            port = customPort;

        var seeds = args.Skip(1).Select(ParseEndpoint).Where(e => e != null).Select(e => e!).ToList();

        ConsoleUi.PrintInfo($"Starting DHT node on port {port}...");

        try
        {
            var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<KademliaDhtNode>();

            var encryptionKey = Key.Create(KeyAgreementAlgorithm.X25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            var signingKey = Key.Create(SignatureAlgorithm.Ed25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

            var node = new KademliaDhtNode(encryptionKey, logger, signingKey);
            var cts = new CancellationTokenSource();

            await node.StartAsync(port).ConfigureAwait(false);
            _session.SetDhtNode(node, cts);

            if (seeds.Count > 0)
            {
                ConsoleUi.PrintInfo($"Bootstrapping against {seeds.Count} seed node(s)...");
                await node.BootstrapAsync(seeds).ConfigureAwait(false);
            }

            ConsoleUi.PrintSuccess("DHT node started.");
            ConsoleUi.PrintInfo($"  Node ID: {node.LocalId.ToString()[..16]}");
            ConsoleUi.PrintInfo($"  Port:    {port}");
            ConsoleUi.PrintInfo($"  Peers:   {node.KnownNodes}");
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Failed to start DHT node: {ex.Message}");
            _session.ClearDhtNode();
        }
    }

    private async Task StopAsync()
    {
        if (_session.DhtNode == null)
        {
            ConsoleUi.PrintWarning("DHT node is not running.");
            return;
        }

        ConsoleUi.PrintInfo("Stopping DHT node (draining in-flight handlers)...");
        await _session.DhtNode.StopAsync().ConfigureAwait(false);
        _session.DhtCts?.Cancel();
        _session.ClearDhtNode();
        ConsoleUi.PrintSuccess("DHT node stopped.");
    }

    private void PrintStatus()
    {
        if (_session.DhtNode == null)
        {
            ConsoleUi.PrintInfo("DHT node: STOPPED");
        }
        else
        {
            ConsoleUi.PrintInfo("DHT node: RUNNING");
            ConsoleUi.PrintInfo($"  Node ID: {_session.DhtNode.LocalId.ToString()[..16]}");
            ConsoleUi.PrintInfo($"  Peers:   {_session.DhtNode.KnownNodes}");
        }
    }

    private static IPEndPoint? ParseEndpoint(string endpoint)
    {
        var parts = endpoint.Split(':');
        if (parts.Length == 2 &&
            IPAddress.TryParse(parts[0], out var ip) &&
            int.TryParse(parts[1], out var port))
        {
            return new IPEndPoint(ip, port);
        }
        return null;
    }

    private static void PrintHelp()
    {
        ConsoleUi.PrintHeader("DHT Commands:");
        Console.WriteLine();
        Console.WriteLine("  dht start [port] [seed-ip:port ...]  - Start a Kademlia DHT node (default port: 7070)");
        Console.WriteLine("  dht deploy [port] [seeds...]         - Alias for 'dht start'");
        Console.WriteLine("  dht stop                             - Stop DHT node");
        Console.WriteLine("  dht status                           - Show DHT node status");
        Console.WriteLine("  dht help                             - Show this help");
    }
}
