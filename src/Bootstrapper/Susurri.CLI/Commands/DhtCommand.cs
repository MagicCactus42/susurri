using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.CLI.Network;
using Susurri.CLI.Tui;
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

    public async Task StartAsync(string[] args, bool stableIdentity = false)
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

        try
        {
            var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<KademliaDhtNode>();

            var udpEnabled = config.GetValue("DHT:Nat:Enabled", true);
            var useStun = config.GetValue("DHT:Nat:UseStun", false);
            var publicEndpoint = NodeConfig.ParseEndpoint(config["DHT:Nat:PublicEndpoint"]);
            var networkId = NodeConfig.NetworkId(config);

            Key encryptionKey;
            Key signingKey;
            if (stableIdentity)
            {
                (signingKey, encryptionKey) = BootstrapIdentity.Derive(config);
            }
            else
            {
                encryptionKey = Key.Create(KeyAgreementAlgorithm.X25519,
                    new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                signingKey = Key.Create(SignatureAlgorithm.Ed25519,
                    new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            }

            var node = new KademliaDhtNode(encryptionKey, logger, signingKey,
                natTraversal: null, enableUdpTransport: udpEnabled, useStun: useStun,
                publicUdpEndpoint: publicEndpoint, networkId: networkId);
            var cts = new CancellationTokenSource();

            await ConsoleUi.WithSpinnerAsync($"starting dht node on port {port}",
                () => node.StartAsync(port)).ConfigureAwait(false);
            _session.SetDhtNode(node, cts);

            if (stableIdentity)
            {
                var inputs = new BootstrapConfigInputs(
                    port,
                    config.GetValue("DHT:Bootstrap:EnableRelay", true),
                    config.GetValue("DHT:Bootstrap:EnableOfflineStorage", true),
                    config.GetValue("DHT:Bootstrap:StorageLimitMB", 256),
                    config.GetValue("DHT:Bootstrap:MaxConnections", 500),
                    networkId);
                _session.Attestation = NodeAttestation.Compute(
                    signingKey,
                    node.LocalId.ToString(),
                    node.SigningPublicKey,
                    node.EncryptionPublicKey,
                    inputs,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }

            if (seeds.Count > 0)
                await ConsoleUi.WithSpinnerAsync($"bootstrapping against {seeds.Count} seed node(s)",
                    () => node.BootstrapAsync(seeds)).ConfigureAwait(false);

            Console.WriteLine();
            ConsoleUi.Panel("dht node", new[]
            {
                ("state", "● running", Palette.Green),
                ("node id", node.LocalId.ToString()[..16], Palette.Mauve),
                ("port", $"{port} (tcp + udp)", Palette.Text),
                ("transport", $"{(udpEnabled ? "udp + tcp fallback" : "tcp only")}{(useStun ? " · stun" : "")}", Palette.Text),
                ("peers", node.KnownNodes.ToString(), node.KnownNodes > 0 ? Palette.Green : Palette.Red)
            }, Palette.Green);
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
