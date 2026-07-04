using Susurri.CLI.Tui;
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
                case "rotate":
                    await RotateAsync(rest).ConfigureAwait(false);
                    break;
                case "kick":
                    await KickAsync(rest).ConfigureAwait(false);
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
        ConsoleUi.PrintHeader($"groups · {groups.Count}");
        foreach (var g in groups)
        {
            var role = g.IsOwner
                ? ConsoleUi.Color("owner", Palette.Yellow)
                : ConsoleUi.Faint("member");
            Console.WriteLine(
                $"  {ConsoleUi.Color("◆", Palette.Mauve)} {ConsoleUi.Bold(g.Name)} " +
                $"{ConsoleUi.Faint($"· {g.Members.Count} member(s) ·")} {role}");
            Console.WriteLine($"    {ConsoleUi.Faint(g.GroupId.ToString())}");
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
        var code = GroupInvite.Encode(group.Name, wrapped, group.OwnerSigningPublicKey);

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

        var (name, key, ownerSigningKey) = GroupInvite.Decode(args[0]);
        var info = _session.Chat!.JoinGroup(key, name, ownerSigningKey);
        if (info == null)
        {
            ConsoleUi.PrintError("Could not join — the invite is not addressed to your identity.");
            return;
        }

        ConsoleUi.PrintSuccess($"Joined group '{info.Name}'.");
        ConsoleUi.PrintInfo($"  Group ID: {info.GroupId}");
        if (ownerSigningKey.Length == 0)
            ConsoleUi.PrintWarning("Legacy invite without the owner's identity — automatic re-keys will be rejected; ask for a fresh invite.");
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

    private async Task RotateAsync(string[] args)
    {
        if (args.Length < 1 || !Guid.TryParse(args[0], out var groupId))
        {
            ConsoleUi.PrintInfo("Usage: group rotate <group-id>");
            return;
        }

        var chat = _session.Chat!;
        var group = chat.GetGroup(groupId);
        if (group == null)
        {
            ConsoleUi.PrintError("Group not found.");
            return;
        }

        if (!group.IsOwner)
        {
            ConsoleUi.PrintError("Only the group owner can rotate the key.");
            return;
        }

        var others = group.Members.Count(m => !m.PublicKey.SequenceEqual(chat.LocalPublicKey));
        var delivered = await ConsoleUi.WithSpinnerAsync("rotating and re-keying members in-band",
            () => chat.RotateGroupKeyAsync(groupId)).ConfigureAwait(false);

        ConsoleUi.PrintSuccess($"Rotated key for '{group.Name}' (version {group.Key.Version}).");
        ConsoleUi.PrintInfo($"  Re-key delivered to {delivered}/{others} member(s); offline members receive it on next login.");
    }

    private async Task KickAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[0], out var groupId))
        {
            ConsoleUi.PrintInfo("Usage: group kick <group-id> <petname|username|key-prefix>");
            return;
        }

        var chat = _session.Chat!;
        var group = chat.GetGroup(groupId);
        if (group == null)
        {
            ConsoleUi.PrintError("Group not found.");
            return;
        }

        if (!group.IsOwner)
        {
            ConsoleUi.PrintError("Only the group owner can remove members.");
            return;
        }

        var memberKey = await ResolveMemberAsync(group, args[1]).ConfigureAwait(false);
        if (memberKey == null)
        {
            ConsoleUi.PrintError($"'{args[1]}' does not match any group member.");
            return;
        }

        if (memberKey.SequenceEqual(chat.LocalPublicKey))
        {
            ConsoleUi.PrintError("You cannot kick yourself — use 'group leave'.");
            return;
        }

        var remaining = group.Members.Count(m =>
            !m.PublicKey.SequenceEqual(chat.LocalPublicKey) && !m.PublicKey.SequenceEqual(memberKey));
        var delivered = await ConsoleUi.WithSpinnerAsync("removing member and re-keying the group",
            () => chat.KickMemberAsync(groupId, memberKey)).ConfigureAwait(false);

        ConsoleUi.PrintSuccess($"Removed the member and rotated '{group.Name}' to key version {group.Key.Version}.");
        ConsoleUi.PrintInfo($"  Re-key delivered to {delivered}/{remaining} remaining member(s); the removed member is cut off.");
    }

    private async Task<byte[]?> ResolveMemberAsync(GroupInfo group, string target)
    {
        var chat = _session.Chat!;

        var contact = chat.Contacts?.Find(target);
        if (contact != null && IsMember(group, contact.EncryptionPublicKey))
            return contact.EncryptionPublicKey;

        var prefixMatch = group.Members.FirstOrDefault(m =>
            Convert.ToHexString(m.PublicKey).StartsWith(target, StringComparison.OrdinalIgnoreCase));
        if (prefixMatch != null)
            return prefixMatch.PublicKey;

        try
        {
            var record = await chat.GetPublicKeyAsync(target).ConfigureAwait(false);
            if (record != null && IsMember(group, record.EncryptionPublicKey))
                return record.EncryptionPublicKey;
        }
        catch
        {
        }

        return null;
    }

    private static bool IsMember(GroupInfo group, byte[] publicKey)
        => group.Members.Any(m => m.PublicKey.SequenceEqual(publicKey));

    private async Task SendAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[0], out var groupId))
        {
            ConsoleUi.PrintInfo("Usage: group send <group-id> <message>");
            return;
        }

        var content = string.Join(' ', args.Skip(1));

        if (_session.Conversations is { } store)
        {
            await store.SendGroupAsync(groupId, content).ConfigureAwait(false);
            ConsoleUi.PrintSuccess("Sent (see 'chats' for delivery status).");
            return;
        }

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
            Console.WriteLine(
                $"  {ConsoleUi.Faint($"{m.ReceivedAt.LocalDateTime:HH:mm:ss}")} " +
                $"{ConsoleUi.Color(ConsoleUi.Bold(sender), Palette.SenderColor(sender))} {m.Content}");
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
        Console.WriteLine("  group kick <group-id> <member>   - Remove a member and re-key (owner only)");
        Console.WriteLine("  group rotate <group-id>          - Rotate the group key in-band (owner only)");
        Console.WriteLine("  group send <group-id> <message>  - Send a message to the group");
        Console.WriteLine("  group msgs <group-id>            - Show received group messages");
    }
}
