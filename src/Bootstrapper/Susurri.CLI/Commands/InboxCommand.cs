namespace Susurri.CLI.Commands;

internal sealed class InboxCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "inbox";
    public string Description => "Show received messages";
    public string HelpLine => "  inbox [username]     - Show received messages (optionally from one sender)";

    public InboxCommand(SessionState session)
    {
        _session = session;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (_session.Chat == null)
        {
            ConsoleUi.PrintError("Not online. Use 'login' first.");
            return Task.FromResult(true);
        }

        var messages = args.Length >= 1
            ? _session.Chat.GetMessagesFrom(args[0])
            : _session.Chat.GetMessages();

        if (messages.Count == 0)
        {
            ConsoleUi.PrintInfo("No messages.");
            return Task.FromResult(true);
        }

        Console.WriteLine();
        ConsoleUi.PrintHeader($"=== Inbox ({messages.Count}) ===");
        foreach (var m in messages)
        {
            var sender = m.SenderUsername ?? Convert.ToHexString(m.SenderPublicKey)[..16];
            Console.WriteLine($"  [{m.ReceivedAt.LocalDateTime:HH:mm:ss}] «{sender}» {m.Content}");
        }

        return Task.FromResult(true);
    }
}
