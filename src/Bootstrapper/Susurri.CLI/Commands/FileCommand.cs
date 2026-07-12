using Susurri.CLI.Tui;
using Susurri.Modules.DHT.Core.Services;

namespace Susurri.CLI.Commands;

internal sealed class FileCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "file";
    public IReadOnlyCollection<string> Aliases => new[] { "sendfile" };
    public string Description => "Send and receive files";
    public string HelpLine => "  file <command>       - Send/receive files (send/accept/reject/list)";

    public FileCommand(SessionState session)
    {
        _session = session;
    }

    public async Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return true;
        }

        var chat = _session.Chat;
        if (chat == null)
        {
            ConsoleUi.PrintError("Not online. Use 'login' first.");
            return true;
        }

        // `sendfile <user> <path>` is a shorthand alias for `file send ...`.
        var invokedAsSendfile = args.Length > 0 && !IsSubcommand(args[0]);
        var sub = invokedAsSendfile ? "send" : (args.Length == 0 ? "list" : args[0].ToLowerInvariant());
        var rest = invokedAsSendfile ? args : args.Skip(1).ToArray();

        try
        {
            switch (sub)
            {
                case "send":
                    await SendAsync(chat, rest).ConfigureAwait(false);
                    break;
                case "accept":
                    await AcceptAsync(chat, rest).ConfigureAwait(false);
                    break;
                case "reject":
                    await RejectAsync(chat, rest).ConfigureAwait(false);
                    break;
                case "list":
                    List(chat);
                    break;
                default:
                    ConsoleUi.PrintWarning($"Unknown file command: {sub}");
                    PrintHelp();
                    break;
            }
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"File command failed: {ex.Message}");
        }

        return true;
    }

    private static bool IsSubcommand(string token) =>
        token is "send" or "accept" or "reject" or "list";

    private async Task SendAsync(ChatService chat, string[] args)
    {
        if (args.Length < 2)
        {
            ConsoleUi.PrintInfo("Usage: file send <username> <path>");
            return;
        }

        var recipient = args[0];
        var path = string.Join(' ', args.Skip(1));

        if (!File.Exists(path))
        {
            ConsoleUi.PrintError($"File not found: {path}");
            return;
        }

        var size = new FileInfo(path).Length;
        var result = await ConsoleUi.WithSpinnerAsync(
            $"offering {Path.GetFileName(path)} ({FormatBytes(size)}) to {recipient}",
            () => chat.SendFileAsync(recipient, path)).ConfigureAwait(false);

        if (result.Success)
        {
            ConsoleUi.PrintSuccess($"Offered '{Path.GetFileName(path)}' to {recipient} (transfer {Short(result.MessageId)}).");
            ConsoleUi.PrintInfo("They must accept before chunks start flowing. Track it with 'file list'.");
        }
        else
        {
            ConsoleUi.PrintError($"Send failed: {result.Error}");
        }
    }

    private async Task AcceptAsync(ChatService chat, string[] args)
    {
        if (args.Length < 1)
        {
            ConsoleUi.PrintInfo("Usage: file accept <transfer-id>");
            return;
        }

        var transfer = ResolveIncoming(chat, args[0]);
        if (transfer == null)
            return;

        await chat.AcceptFileTransferAsync(transfer.TransferId).ConfigureAwait(false);
        ConsoleUi.PrintSuccess($"Accepting '{transfer.FileName}' — it will land in {Downloads.Directory()} when complete.");
    }

    private async Task RejectAsync(ChatService chat, string[] args)
    {
        if (args.Length < 1)
        {
            ConsoleUi.PrintInfo("Usage: file reject <transfer-id>");
            return;
        }

        var transfer = ResolveIncoming(chat, args[0]);
        if (transfer == null)
            return;

        await chat.RejectFileTransferAsync(transfer.TransferId).ConfigureAwait(false);
        ConsoleUi.PrintSuccess($"Rejected '{transfer.FileName}'.");
    }

    private void List(ChatService chat)
    {
        var transfers = chat.GetActiveFileTransfers();
        if (transfers.Count == 0)
        {
            ConsoleUi.PrintInfo("No active transfers.");
            return;
        }

        Console.WriteLine();
        ConsoleUi.PrintHeader($"transfers · {transfers.Count}");
        foreach (var t in transfers)
        {
            var arrow = t.Direction == TransferDirection.Incoming
                ? ConsoleUi.Color("▼ in ", Palette.Green)
                : ConsoleUi.Color("▲ out", Palette.Accent);
            var pct = t.ChunkCount > 0 ? t.ChunksTransferred * 100 / t.ChunkCount : 0;
            Console.WriteLine(
                $"  {arrow} {ConsoleUi.Bold(t.FileName)} " +
                $"{ConsoleUi.Faint($"· {FormatBytes(t.FileSize)} · {t.Status.ToString().ToLowerInvariant()} · {pct}%")}");
            Console.WriteLine($"    {ConsoleUi.Faint(t.TransferId.ToString())}");
        }
    }

    private FileTransferInfo? ResolveIncoming(ChatService chat, string idPrefix)
    {
        var candidates = chat.GetActiveFileTransfers()
            .Where(t => t.Direction == TransferDirection.Incoming)
            .Where(t => t.TransferId.ToString().StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            ConsoleUi.PrintError($"No incoming transfer matching '{idPrefix}'.");
            return null;
        }

        if (candidates.Count > 1)
        {
            ConsoleUi.PrintError($"'{idPrefix}' is ambiguous — {candidates.Count} transfers match. Use more of the id.");
            return null;
        }

        return candidates[0];
    }

    private static string Short(Guid? id) => id?.ToString()[..8] ?? "?";

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static void PrintHelp()
    {
        ConsoleUi.PrintHeader("File Commands:");
        Console.WriteLine();
        Console.WriteLine("  file send <user> <path>          - Offer a file to a user (they must accept)");
        Console.WriteLine("  file accept <transfer-id>        - Accept an incoming offer (id prefix is enough)");
        Console.WriteLine("  file reject <transfer-id>        - Reject an incoming offer");
        Console.WriteLine("  file list                        - Show active transfers and progress");
        Console.WriteLine();
        Console.WriteLine("  sendfile <user> <path>           - Shorthand for 'file send'");
    }
}
