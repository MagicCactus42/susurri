using Susurri.CLI.Tui;

namespace Susurri.CLI.Commands;

internal sealed class ChatsCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "chats";
    public string Description => "Open the full-screen chat browser";
    public string HelpLine => "  chats                - Browse conversations in a full-screen TUI";

    public ChatsCommand(SessionState session)
    {
        _session = session;
    }

    public async Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (_session.Chat == null || _session.Conversations == null)
        {
            ConsoleUi.PrintError("Not online. Use 'login' first.");
            return true;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            ConsoleUi.PrintError("chats needs an interactive terminal.");
            return true;
        }

        await new ChatsScreen(_session).RunAsync(ct).ConfigureAwait(false);
        return true;
    }
}
