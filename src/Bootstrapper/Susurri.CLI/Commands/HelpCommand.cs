namespace Susurri.CLI.Commands;

internal sealed class HelpCommand : ICommand
{
    private readonly CommandRegistry _registry;

    public string Name => "help";
    public IReadOnlyCollection<string> Aliases => new[] { "?" };
    public string Description => "Show this help";
    public string HelpLine => "  help                 - Show this help";

    public HelpCommand(CommandRegistry registry)
    {
        _registry = registry;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ConsoleUi.PrintHeader("Available Commands:");
        Console.WriteLine();
        foreach (var cmd in _registry.All.OrderBy(c => c.Name))
            Console.WriteLine(cmd.HelpLine);
        Console.WriteLine();
        ConsoleUi.PrintHeader("Bootstrap Mode:");
        Console.WriteLine();
        Console.WriteLine("  --bootstrap, -b      - Start as headless bootstrap node (DHT + relay only)");
        Console.WriteLine("  --port, -p <port>    - Set listening port (default: 7070)");
        Console.WriteLine();
        Console.WriteLine("  Example: susurri --bootstrap --port 7070");
        return Task.FromResult(true);
    }
}
