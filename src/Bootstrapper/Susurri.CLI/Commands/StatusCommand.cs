using Susurri.CLI.Tui;

namespace Susurri.CLI.Commands;

internal sealed class StatusCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "status";
    public string Description => "Show current status";
    public string HelpLine => "  status               - Show current status";

    public StatusCommand(SessionState session)
    {
        _session = session;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        Console.WriteLine();

        if (_session.Chat is { } chat)
        {
            var peers = chat.PeerCount;
            ConsoleUi.Panel("identity", new[]
            {
                ("state", "● online", Palette.Green),
                ("user", _session.CurrentUser ?? "?", Palette.Accent),
                ("port", $"{chat.LocalPort} (tcp + udp)", Palette.Text),
                ("peers", peers.ToString(), peers > 0 ? Palette.Green : Palette.Red),
                ("relays", chat.ActiveRelays.ToString(), Palette.Text),
                ("inbox", $"{chat.GetMessages().Count} message(s)", Palette.Text)
            });
        }
        else
        {
            ConsoleUi.Box("identity", new[] { ConsoleUi.Faint("not logged in — use 'login' to go online") });
        }

        Console.WriteLine();

        if (_session.DhtNode is { } node)
        {
            ConsoleUi.Panel("dht node", new[]
            {
                ("state", "● running", Palette.Green),
                ("node id", node.LocalId.ToString()[..16], Palette.Mauve),
                ("peers", node.KnownNodes.ToString(), node.KnownNodes > 0 ? Palette.Green : Palette.Red)
            });
        }
        else
        {
            ConsoleUi.Box("dht node", new[] { ConsoleUi.Faint("stopped — 'dht start' to run a headless node") });
        }

        return Task.FromResult(true);
    }
}
