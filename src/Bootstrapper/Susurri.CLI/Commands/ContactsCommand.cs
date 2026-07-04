using Susurri.CLI.Tui;
using Susurri.Modules.DHT.Core.Contacts;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.CLI.Commands;

internal sealed class ContactsCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "contacts";
    public IReadOnlyCollection<string> Aliases => new[] { "contact" };
    public string Description => "Local contact book with pinned keys";
    public string HelpLine => "  contacts <command>   - Contact book (add/list/remove/rename/verify/check)";

    public ContactsCommand(SessionState session)
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

        if (chat.Contacts == null)
        {
            ConsoleUi.PrintError("Contact book unavailable for this session.");
            return true;
        }

        var sub = args.Length == 0 ? "list" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            switch (sub)
            {
                case "list":
                    List(chat.Contacts);
                    break;
                case "add":
                    await AddAsync(rest).ConfigureAwait(false);
                    break;
                case "remove":
                case "rm":
                    Remove(rest);
                    break;
                case "rename":
                    Rename(rest);
                    break;
                case "verify":
                    Verify(rest);
                    break;
                case "check":
                    await CheckAsync(rest).ConfigureAwait(false);
                    break;
                default:
                    ConsoleUi.PrintWarning($"Unknown contacts command: {sub}");
                    PrintHelp();
                    break;
            }
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Contacts command failed: {ex.Message}");
        }

        return true;
    }

    private void List(ContactBook contacts)
    {
        var all = contacts.All();
        if (all.Count == 0)
        {
            ConsoleUi.PrintInfo("Contact book is empty. Add someone with: contacts add <petname> <username>");
            return;
        }

        Console.WriteLine();
        ConsoleUi.PrintHeader($"contacts · {all.Count}");
        foreach (var c in all)
        {
            var badge = c.Verified
                ? ConsoleUi.Color("✓ verified", Palette.Green)
                : ConsoleUi.Faint("unverified");
            Console.WriteLine(
                $"  {ConsoleUi.Color("@", Palette.Mauve)} {ConsoleUi.Bold(c.Petname)} " +
                $"{ConsoleUi.Faint($"· {c.Username} ·")} {badge}");
            Console.WriteLine($"    {ConsoleUi.Faint(Convert.ToHexString(c.EncryptionPublicKey)[..16].ToLowerInvariant())}");
        }
    }

    private async Task AddAsync(string[] args)
    {
        if (args.Length < 2)
        {
            ConsoleUi.PrintInfo("Usage: contacts add <petname> <username>");
            return;
        }

        var petname = args[0];
        var username = args[1];

        if (!ValidatePetname(petname))
            return;

        var chat = _session.Chat!;
        var contacts = chat.Contacts!;

        if (contacts.FindByPetname(petname) != null)
        {
            ConsoleUi.PrintError($"Petname '{petname}' is already taken.");
            return;
        }

        var existing = contacts.FindByUsername(username);
        if (existing != null)
        {
            ConsoleUi.PrintError($"User '{username}' is already pinned as '{existing.Petname}'.");
            return;
        }

        var record = await ConsoleUi.WithSpinnerAsync($"looking up {username} in the DHT",
            () => chat.LookupPublicKeyFreshAsync(username)).ConfigureAwait(false);
        if (record == null)
        {
            ConsoleUi.PrintError($"User '{username}' not found (they must be online at least once to publish their key).");
            return;
        }

        var contact = new Contact
        {
            Petname = petname,
            Username = username,
            EncryptionPublicKey = record.EncryptionPublicKey,
            SigningPublicKey = record.SigningPublicKey,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (!contacts.Add(contact))
        {
            ConsoleUi.PrintError($"Could not add '{petname}'.");
            return;
        }

        ConsoleUi.PrintSuccess($"Pinned '{username}' as '{petname}'. Their key is now trusted over DHT lookups.");
        Console.WriteLine();
        PrintSafetyNumber(contact);
        ConsoleUi.PrintInfo("Compare this safety number out-of-band, then run: contacts verify " + petname);
    }

    private void Remove(string[] args)
    {
        if (args.Length < 1)
        {
            ConsoleUi.PrintInfo("Usage: contacts remove <petname>");
            return;
        }

        if (_session.Chat!.Contacts!.Remove(args[0]))
            ConsoleUi.PrintSuccess($"Removed '{args[0]}'.");
        else
            ConsoleUi.PrintError($"No contact named '{args[0]}'.");
    }

    private void Rename(string[] args)
    {
        if (args.Length < 2)
        {
            ConsoleUi.PrintInfo("Usage: contacts rename <petname> <new-petname>");
            return;
        }

        if (!ValidatePetname(args[1]))
            return;

        if (_session.Chat!.Contacts!.Rename(args[0], args[1]))
            ConsoleUi.PrintSuccess($"Renamed '{args[0]}' to '{args[1]}'.");
        else
            ConsoleUi.PrintError($"Could not rename — check that '{args[0]}' exists and '{args[1]}' is free.");
    }

    private void Verify(string[] args)
    {
        if (args.Length < 1)
        {
            ConsoleUi.PrintInfo("Usage: contacts verify <petname>");
            return;
        }

        var contacts = _session.Chat!.Contacts!;
        var contact = contacts.FindByPetname(args[0]);
        if (contact == null)
        {
            ConsoleUi.PrintError($"No contact named '{args[0]}'.");
            return;
        }

        PrintSafetyNumber(contact);
        Console.WriteLine($"  {ConsoleUi.Faint("both sides see the same number — compare it over a trusted channel")}");
        Console.Write("  Does the number match on both devices? [y/N]: ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (answer is "y" or "yes")
        {
            contacts.SetVerified(contact.Petname, true);
            ConsoleUi.PrintSuccess($"'{contact.Petname}' marked as verified.");
        }
        else
        {
            contacts.SetVerified(contact.Petname, false);
            ConsoleUi.PrintInfo("Left unverified.");
        }
    }

    private async Task CheckAsync(string[] args)
    {
        if (args.Length < 1)
        {
            ConsoleUi.PrintInfo("Usage: contacts check <petname>");
            return;
        }

        var chat = _session.Chat!;
        var contact = chat.Contacts!.FindByPetname(args[0]);
        if (contact == null)
        {
            ConsoleUi.PrintError($"No contact named '{args[0]}'.");
            return;
        }

        var record = await ConsoleUi.WithSpinnerAsync($"looking up {contact.Username} in the DHT",
            () => chat.LookupPublicKeyFreshAsync(contact.Username)).ConfigureAwait(false);
        if (record == null)
        {
            ConsoleUi.PrintWarning("No DHT record found right now — nothing to compare (pinned key stays in effect).");
            return;
        }

        if (record.EncryptionPublicKey.AsSpan().SequenceEqual(contact.EncryptionPublicKey) &&
            record.SigningPublicKey.AsSpan().SequenceEqual(contact.SigningPublicKey))
        {
            ConsoleUi.PrintSuccess("DHT record matches the pinned key.");
        }
        else
        {
            ConsoleUi.PrintError("DHT record does NOT match the pinned key — possible impersonation attempt.");
            ConsoleUi.PrintInfo("Messages keep using the pinned key. Re-verify with your contact before trusting the new key.");
        }
    }

    private void PrintSafetyNumber(Contact contact)
    {
        var chat = _session.Chat!;
        var number = SafetyNumber.Compute(
            chat.LocalPublicKey, chat.LocalSigningPublicKey,
            contact.EncryptionPublicKey, contact.SigningPublicKey);

        var groups = number.Split(' ');
        Console.WriteLine();
        ConsoleUi.PrintHeader($"safety number · {contact.Petname}");
        for (var i = 0; i < groups.Length; i += 4)
            Console.WriteLine($"    {ConsoleUi.Bold(string.Join("  ", groups.Skip(i).Take(4)))}");
        Console.WriteLine();
    }

    private static bool ValidatePetname(string petname)
    {
        if (petname.Length < 1 || petname.Length > SecurityLimits.MaxUsernameLength)
        {
            ConsoleUi.PrintError($"Petname must be 1-{SecurityLimits.MaxUsernameLength} characters.");
            return false;
        }

        if (petname.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            ConsoleUi.PrintError("Petname may only contain letters, digits, underscores, and hyphens.");
            return false;
        }

        return true;
    }

    private static void PrintHelp()
    {
        ConsoleUi.PrintHeader("Contact Commands:");
        Console.WriteLine();
        Console.WriteLine("  contacts list                    - List pinned contacts");
        Console.WriteLine("  contacts add <petname> <user>    - Pin a user's key under a local petname");
        Console.WriteLine("  contacts remove <petname>        - Remove a contact");
        Console.WriteLine("  contacts rename <old> <new>      - Rename a contact");
        Console.WriteLine("  contacts verify <petname>        - Compare safety numbers and mark verified");
        Console.WriteLine("  contacts check <petname>         - Compare the pinned key against the live DHT record");
    }
}
