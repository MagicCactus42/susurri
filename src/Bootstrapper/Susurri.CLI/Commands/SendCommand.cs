using Susurri.Modules.DHT.Core.Services;

namespace Susurri.CLI.Commands;

internal sealed class SendCommand : ICommand
{
    private static readonly TimeSpan AckWait = TimeSpan.FromSeconds(8);

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
        var chat = _session.Chat;
        var store = _session.Conversations;
        if (chat == null || store == null)
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

        var result = await ConsoleUi.WithSpinnerAsync($"onion-routing to {recipient}",
            () => store.SendDirectAsync(recipient, content)).ConfigureAwait(false);

        if (!result.Success)
        {
            ConsoleUi.PrintError($"Send failed: {result.Error}");
            return true;
        }

        var id = result.MessageId?.ToString()[..8];
        ConsoleUi.PrintSuccess($"Sent (id {id}).");

        var acknowledged = result.MessageId is { } messageId
            && await WaitForAckAsync(chat, messageId, ct).ConfigureAwait(false);

        if (acknowledged)
            ConsoleUi.PrintSuccess("Acknowledged by recipient.");
        else
            ConsoleUi.PrintInfo("Delivered to the network; no acknowledgement yet (see 'chats').");

        return true;
    }

    private static async Task<bool> WaitForAckAsync(ChatService chat, Guid messageId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Handler(Guid id)
        {
            if (id == messageId)
                tcs.TrySetResult(true);
            return Task.CompletedTask;
        }

        chat.OnMessageAcknowledged += Handler;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AckWait);
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            chat.OnMessageAcknowledged -= Handler;
        }
    }
}
