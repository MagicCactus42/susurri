using Susurri.CLI.Tui;

namespace Susurri.CLI.Commands;

internal sealed class HistoryCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "history";
    public string Description => "Encrypted local chat history";
    public string HelpLine => "  history [on|off]     - Persist chats locally, encrypted with your identity key";

    public HistoryCommand(SessionState session)
    {
        _session = session;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var history = _session.History;
        if (history == null || _session.Conversations == null)
        {
            ConsoleUi.PrintError("Not online. Use 'login' first.");
            return Task.FromResult(true);
        }

        var sub = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        switch (sub)
        {
            case "status":
                PrintStatus(history);
                break;
            case "on":
                history.Enable();
                _session.Conversations.SaveNow();
                ConsoleUi.PrintSuccess("History persistence enabled — conversations survive restarts, encrypted at rest.");
                break;
            case "off":
                Console.Write("  Delete the stored history and stop persisting? [y/N]: ");
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (answer is "y" or "yes")
                {
                    history.Disable();
                    ConsoleUi.PrintSuccess("History persistence disabled — local store wiped.");
                }
                else
                {
                    ConsoleUi.PrintInfo("Operation cancelled.");
                }
                break;
            default:
                ConsoleUi.PrintInfo("Usage: history [on|off|status]");
                break;
        }

        return Task.FromResult(true);
    }

    private void PrintStatus(HistoryStore history)
    {
        if (!history.Enabled)
        {
            ConsoleUi.PrintInfo("History persistence is OFF — conversations live in memory only.");
            ConsoleUi.PrintInfo("Enable with: history on");
            return;
        }

        var conversations = _session.Conversations!.Snapshot();
        var messages = conversations.Sum(c => c.Entries.Count);
        ConsoleUi.PrintSuccess("History persistence is ON (encrypted with a key derived from your passphrase).");
        ConsoleUi.PrintInfo($"  {conversations.Count} conversation(s), {messages} message(s), {history.SizeBytes} bytes on disk.");
    }
}
