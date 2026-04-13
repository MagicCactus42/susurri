using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.DHT.Core.Abstractions;

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

        if (!int.TryParse(args[1], out var port))
        {
            ConsoleUi.PrintError("Invalid port number.");
            return true;
        }

        var host = args[0];
        ConsoleUi.PrintInfo($"Pinging {host}:{port}...");

        try
        {
            var nodeClient = _services.GetService<INodeClient>();
            if (nodeClient == null)
            {
                ConsoleUi.PrintError("Node client not available.");
                return true;
            }

            var response = await nodeClient.SendMessage(host, port, "PING");
            if (!string.IsNullOrEmpty(response))
                ConsoleUi.PrintSuccess($"Response: {response}");
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
