using Susurri.Modules.DHT.Core.Onion.GroupChat;

namespace Susurri.CLI.Commands;

internal sealed class GroupCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "group";
    public string Description => "Group chat management";
    public string HelpLine => "  group <command>      - Group chat (create/list/invite/join/leave/send)";

    public GroupCommand(SessionState session)
    {
        _session = session;
    }

    public async Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
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

        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            switch (sub)
            {
                case "create":
                    Create(rest);
                    break;
                case "list":
                    List();
                    break;
                case "invite":
                    await InviteAsync(rest).ConfigureAwait(false);
                    break;
                case "join":
                    Join(rest);
                    break;
                case "leave":
                    Leave(rest);
                    break;
                case "send":
                    await SendAsync(rest).ConfigureAwait(false);
                    break;
                case "msgs":
                case "inbox":
                    ShowMessages(rest);
                    break;
                default:
                    ConsoleUi.PrintWarning($"Unknown group command: {sub}");
                    PrintHelp();
                    break;
            }
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Group command failed: {ex.Message}");
        }

        return true;
    }

    private void Create(string[] args)
    {
        if (args.Length < 1)
        {
            ConsoleUi.PrintInfo("Usage: group create <name>");
            return;
        }

        var info = _session.Chat!.CreateGroup(string.Join(' ', args));
        ConsoleUi.PrintSuccess($"Created group '{info.Name}'.");
        ConsoleUi.PrintInfo($"  Group ID: {info.GroupId}");
        ConsoleUi.PrintInfo("  Invite members with: group invite <group-id> <username>");
    }

    private void List()
    {
        var groups = _session.Chat!.GetGroups();
        if (groups.Count == 0)
        {
            ConsoleUi.PrintInfo("You are not in any groups.");
            return;
        }

        Console.WriteLine();
        ConsoleUi.PrintHeader($"=== Groups ({groups.Count}) ===");
        foreach (var g in groups)
        {
            var role = g.IsOwner ? "owner" : "member";
            Console.WriteLine($"  {g.GroupId}  {g.Name}  ({g.Members.Count} member(s), {role})");
        }
    }

    private async Task InviteAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[0], out var groupId))
        {
            ConsoleUi.PrintInfo("Usage: group invite <group-id> <username>");
            return;
        }

        var username = args[1];
        var record = await _session.Chat!.GetPublicKeyAsync(username).ConfigureAwait(false);
        if (record == null)
        {
            ConsoleUi.PrintError($"User '{username}' not found (they must be online at least once to publish their key).");
            return;
        }

        var group = _session.Chat.GetGroup(groupId);
        if (group == null)
        {
            ConsoleUi.PrintError("Group not found.");
            return;
        }

        var wrapped = _session.Chat.InviteMember(groupId, record.EncryptionPublicKey);
        var code = GroupInvite.Encode(group.Name, wrapped);

        ConsoleUi.PrintSuccess($"Invited '{username}' to '{group.Name}'. Send them this invite code:");
        Console.WriteLine();
        Console.WriteLine($"  {code}");
    }

    private void Join(string[] args)
    {
        if (args.Length < 1)
        {
            ConsoleUi.PrintInfo("Usage: group join <invite-code>");
            return;
        }

        var (name, key) = GroupInvite.Decode(args[0]);
        var info = _session.Chat!.JoinGroup(key, name);
        if (info == null)
        {
            ConsoleUi.PrintError("Could not join — the invite is not addressed to your identity.");
            return;
        }

        ConsoleUi.PrintSuccess($"Joined group '{info.Name}'.");
        ConsoleUi.PrintInfo($"  Group ID: {info.GroupId}");
    }

    private void Leave(string[] args)
    {
        if (args.Length < 1 || !Guid.TryParse(args[0], out var groupId))
        {
            ConsoleUi.PrintInfo("Usage: group leave <group-id>");
            return;
        }

        _session.Chat!.LeaveGroup(groupId);
        ConsoleUi.PrintSuccess("Left the group.");
    }

    private async Task SendAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[0], out var groupId))
        {
            ConsoleUi.PrintInfo("Usage: group send <group-id> <message>");
            return;
        }

        var content = string.Join(' ', args.Skip(1));
        var delivered = await _session.Chat!.SendGroupMessageAsync(groupId, content).ConfigureAwait(false);
        ConsoleUi.PrintSuccess($"Sent to {delivered} member(s).");
    }

    private void ShowMessages(string[] args)
    {
        if (args.Length < 1 || !Guid.TryParse(args[0], out var groupId))
        {
            ConsoleUi.PrintInfo("Usage: group msgs <group-id>");
            return;
        }

        var messages = _session.Chat!.GetGroupMessages(groupId);
        if (messages.Count == 0)
        {
            ConsoleUi.PrintInfo("No messages in this group.");
            return;
        }

        Console.WriteLine();
        ConsoleUi.PrintHeader($"=== Group messages ({messages.Count}) ===");
        foreach (var m in messages)
        {
            var sender = m.SenderUsername ?? Convert.ToHexString(m.SenderPublicKey)[..16];
            Console.WriteLine($"  [{m.ReceivedAt.LocalDateTime:HH:mm:ss}] «{sender}» {m.Content}");
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.PrintHeader("Group Commands:");
        Console.WriteLine();
        Console.WriteLine("  group create <name>              - Create a new group");
        Console.WriteLine("  group list                       - List your groups");
        Console.WriteLine("  group invite <group-id> <user>   - Invite a user (prints an invite code)");
        Console.WriteLine("  group join <invite-code>         - Join a group from an invite code");
        Console.WriteLine("  group leave <group-id>           - Leave a group");
        Console.WriteLine("  group send <group-id> <message>  - Send a message to the group");
        Console.WriteLine("  group msgs <group-id>            - Show received group messages");
    }
}
