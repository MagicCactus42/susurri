namespace Susurri.CLI.Commands;

internal sealed class SendCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "send";
    public string Description => "Send a message to a user";
    public string HelpLine => "  send <username> <message>  - Send an encrypted message";

    public SendCommand(SessionState session)
    {
        _session = session;
    }

    public async Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (_session.Chat == null)
        {
            ConsoleUi.PrintError("Not online. Use 'login' first.");
            return true;
        }

        if (args.Length < 2)
        {
            ConsoleUi.PrintInfo("Usage: send <username> <message>");
            return true;
        }

        var recipient = args[0];
        var content = string.Join(' ', args.Skip(1));

        ConsoleUi.PrintInfo($"Sending to {recipient}...");
        var result = await _session.Chat.SendMessageAsync(recipient, content).ConfigureAwait(false);

        if (result.Success)
            ConsoleUi.PrintSuccess($"Sent (id {result.MessageId?.ToString()[..8]}).");
        else
            ConsoleUi.PrintError($"Send failed: {result.Error}");

        return true;
    }
}
