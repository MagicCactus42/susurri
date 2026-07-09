using System.Net;
using Microsoft.Extensions.Configuration;
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

        var config = _services.GetRequiredService<IConfiguration>();

        var port = config.GetValue("DHT:DefaultPort", 7070);
        if (args.Length > 0 && int.TryParse(args[0], out var customPort))
            port = customPort;

        // Seeds come from both the CLI args and DHT:BootstrapNodes in config, so a
        // node can be pointed at a network by configuration alone.
        var seeds = NodeConfig.Seeds(config, args.Skip(1));

        ConsoleUi.PrintInfo($"Starting DHT node on port {port}...");

        try
        {
            var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<KademliaDhtNode>();

            var udpEnabled = config.GetValue("DHT:Nat:Enabled", true);
            var useStun = config.GetValue("DHT:Nat:UseStun", false);
            var publicEndpoint = NodeConfig.ParseEndpoint(config["DHT:Nat:PublicEndpoint"]);
            var networkId = NodeConfig.NetworkId(config);

            var encryptionKey = Key.Create(KeyAgreementAlgorithm.X25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            var signingKey = Key.Create(SignatureAlgorithm.Ed25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

            var node = new KademliaDhtNode(encryptionKey, logger, signingKey,
                natTraversal: null, enableUdpTransport: udpEnabled, useStun: useStun,
                publicUdpEndpoint: publicEndpoint, networkId: networkId);
            var cts = new CancellationTokenSource();

            await node.StartAsync(port).ConfigureAwait(false);
            _session.SetDhtNode(node, cts);

            if (seeds.Count > 0)
            {
                ConsoleUi.PrintInfo($"Bootstrapping against {seeds.Count} seed node(s)...");
                await node.BootstrapAsync(seeds).ConfigureAwait(false);
            }

            ConsoleUi.PrintSuccess("DHT node started.");
            ConsoleUi.PrintInfo($"  Node ID:   {node.LocalId.ToString()[..16]}");
            ConsoleUi.PrintInfo($"  Port:      {port} (TCP + UDP)");
            ConsoleUi.PrintInfo($"  Transport: {(udpEnabled ? "UDP + TCP fallback" : "TCP only")}{(useStun ? ", STUN" : "")}");
            ConsoleUi.PrintInfo($"  Peers:     {node.KnownNodes}");
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
