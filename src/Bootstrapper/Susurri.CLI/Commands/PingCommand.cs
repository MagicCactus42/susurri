using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.DHT.Core.Kademlia;

namespace Susurri.CLI.Commands;

internal sealed class PingCommand : ICommand
{
    private readonly IServiceProvider _services;

    public string Name => "ping";
    public string Description => "Ping a DHT node";
    public string HelpLine => "  ping <host> <port>   - Ping a DHT node";

    public PingCommand(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            ConsoleUi.PrintInfo("Usage: ping <host> <port>");
            return true;
        }

        if (!int.TryParse(args[1], out var port) || port <= 0 || port > 65535)
        {
            ConsoleUi.PrintError("Invalid port number.");
            return true;
        }

        if (!IPAddress.TryParse(args[0], out var address))
        {
            ConsoleUi.PrintError("Invalid host address (expected an IP address).");
            return true;
        }

        var endpoint = new IPEndPoint(address, port);
        ConsoleUi.PrintInfo($"Pinging {endpoint}...");

        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<KademliaDhtNode>();

        var encryptionKey = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        await using var probe = new KademliaDhtNode(encryptionKey, logger);

        try
        {
            var alive = await probe.PingEndpointAsync(endpoint).ConfigureAwait(false);
            if (alive)
                ConsoleUi.PrintSuccess($"PONG from {endpoint}");
            else
                ConsoleUi.PrintWarning("No response received.");
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Ping failed: {ex.Message}");
        }

        return true;
    }
}
