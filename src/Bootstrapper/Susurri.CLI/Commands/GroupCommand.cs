namespace Susurri.CLI.Commands;

/// <summary>
/// Group-chat command surface. The underlying GroupManager integration is not yet
/// wired into the CLI; this command currently rejects subcommands rather than
/// printing fake success messages so callers don't get misled.
/// See Phase 2.6 / KNOWN-LIMITATIONS for tracking.
/// </summary>
internal sealed class GroupCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "group";
    public string Description => "Group chat management (not yet wired)";
    public string HelpLine => "  group <command>      - Group chat management (not yet wired in CLI)";

    public GroupCommand(SessionState session)
    {
        _session = session;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return Task.FromResult(true);
        }

        // Per Phase 2.6 acceptance: stubs must either implement or fail loudly.
        // The GroupManager library is functional, but its CLI binding is not.
        // Throwing here lets the registry's catch-block render the failure as
        // a red `Command failed:` rather than a misleading green warning.
        // Note: we no-op the login check above this throw so unauthenticated
        // users still get the same clear error rather than a misleading
        // "must be logged in" message.
        throw new NotImplementedException(
            $"`group {args[0]}` is not yet wired through the CLI. " +
            "The GroupManager library at Susurri.Modules.DHT.Core/Onion/GroupChat/ " +
            "is functional and can be used programmatically. " +
            "See KNOWN-LIMITATIONS.md for tracking.");
    }

    private static void PrintHelp()
    {
        ConsoleUi.PrintHeader("Group Commands (planned):");
        Console.WriteLine();
        Console.WriteLine("  group create <name>          - Create a new group");
        Console.WriteLine("  group list                   - List your groups");
        Console.WriteLine("  group invite <id> <pubkey>   - Generate invite for a user");
        Console.WriteLine("  group join <invite-code>     - Join a group using invite code");
        Console.WriteLine("  group leave <id>             - Leave a group");
        Console.WriteLine("  group help                   - Show this help");
        Console.WriteLine();
        ConsoleUi.PrintInfo("Group commands are not yet wired into the CLI; see KNOWN-LIMITATIONS.md.");
    }
}
