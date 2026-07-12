using Susurri.CLI.Tui;

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

    private static readonly (string Title, string[] Names)[] Groups =
    {
        ("messaging", new[] { "login", "logout", "send", "inbox", "chats", "group", "contacts", "history", "file" }),
        ("network", new[] { "dht", "ping", "status" }),
        ("identity", new[] { "generate", "clearcache" }),
        ("app", new[] { "help", "version", "clear", "exit" })
    };

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var all = _registry.All.ToDictionary(c => c.Name);
        var listed = new HashSet<string>();

        foreach (var (title, names) in Groups)
        {
            var cmds = names.Where(all.ContainsKey).Select(n => all[n]).ToList();
            if (cmds.Count == 0)
                continue;

            ConsoleUi.PrintHeader(title);
            foreach (var cmd in cmds)
            {
                PrintHelpLine(cmd.HelpLine);
                listed.Add(cmd.Name);
            }
            Console.WriteLine();
        }

        var rest = _registry.All.Where(c => !listed.Contains(c.Name)).OrderBy(c => c.Name).ToList();
        if (rest.Count > 0)
        {
            ConsoleUi.PrintHeader("other");
            foreach (var cmd in rest)
                PrintHelpLine(cmd.HelpLine);
            Console.WriteLine();
        }

        ConsoleUi.PrintHeader("bootstrap mode");
        PrintHelpLine("  --bootstrap, -b      - Start as headless bootstrap node (DHT + relay only)");
        PrintHelpLine("  --port, -p <port>    - Set listening port (default: 7070)");
        Console.WriteLine();
        Console.WriteLine($"  {ConsoleUi.Faint("example:")} {ConsoleUi.Color("susurri --bootstrap --port 7070", Palette.Text)}");
        return Task.FromResult(true);
    }

    private static void PrintHelpLine(string helpLine)
    {
        var idx = helpLine.IndexOf(" - ", StringComparison.Ordinal);
        if (idx < 0)
        {
            Console.WriteLine(helpLine);
            return;
        }

        var usage = helpLine[..idx];
        var desc = helpLine[(idx + 3)..];
        Console.WriteLine($"{ConsoleUi.Color(usage, Palette.Accent)} {ConsoleUi.Faint("·")} {ConsoleUi.Faint(desc)}");
    }
}
