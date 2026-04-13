using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.IAM.Application.Commands;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Shared.Abstractions.Commands;

namespace Susurri.CLI.Commands;

internal sealed class LoginCommand : ICommand
{
    private readonly IServiceProvider _services;
    private readonly SessionState _session;

    public string Name => "login";
    public string Description => "Login with username and passphrase";
    public string HelpLine => "  login [username]     - Login with username and passphrase (6+ words)";

    public LoginCommand(IServiceProvider services, SessionState session)
    {
        _services = services;
        _session = session;
    }

    public async Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (_session.IsLoggedIn)
        {
            ConsoleUi.PrintWarning($"Already logged in as '{_session.CurrentUser}'. Use 'logout' first.");
            return true;
        }

        string? username = null;
        string? passphrase = null;
        var useCachedCredentials = false;

        var credentialsCache = _services.GetService<ICredentialsCache>();

        if (credentialsCache?.Exists() == true)
        {
            Console.WriteLine();
            ConsoleUi.PrintInfo("Cached credentials found.");
            Console.Write("  Enter cache password to use cached credentials (or press Enter to skip): ");
            var cachePassword = ConsoleInput.ReadPassword();

            if (!string.IsNullOrEmpty(cachePassword))
            {
                try
                {
                    var cached = credentialsCache.Load(cachePassword);
                    username = cached.Username;
                    passphrase = cached.Passphrase;
                    useCachedCredentials = true;
                    ConsoleUi.PrintSuccess("Loaded credentials from cache.");
                }
                catch (Exception ex)
                {
                    ConsoleUi.PrintWarning($"Could not load cached credentials: {ex.Message}");
                    ConsoleUi.PrintInfo("Proceeding with manual login...");
                }
            }
        }

        if (!useCachedCredentials)
        {
            if (args.Length >= 1)
            {
                username = args[0];
            }
            else
            {
                Console.Write("  Username: ");
                username = Console.ReadLine()?.Trim();
            }

            if (string.IsNullOrEmpty(username))
            {
                ConsoleUi.PrintError("Username cannot be empty.");
                return true;
            }

            Console.Write("  Passphrase (6+ word BIP39 mnemonic, use 'generate' command to create one): ");
            passphrase = ConsoleInput.ReadPassword();

            if (string.IsNullOrEmpty(passphrase))
            {
                ConsoleUi.PrintError("Passphrase cannot be empty.");
                return true;
            }
        }

        ConsoleUi.PrintInfo("Authenticating...");

        try
        {
            var commandDispatcher = _services.GetRequiredService<ICommandDispatcher>();

            var cacheCredentials = false;
            string? cachePassword = null;

            if (!useCachedCredentials && credentialsCache != null)
            {
                Console.WriteLine();
                Console.Write("  Save credentials locally for future logins? [y/N]: ");
                var saveResponse = Console.ReadLine()?.Trim().ToLowerInvariant();

                if (saveResponse == "y" || saveResponse == "yes")
                {
                    Console.Write("  Enter a password to protect cached credentials (8+ chars): ");
                    cachePassword = ConsoleInput.ReadPassword();

                    if (!string.IsNullOrEmpty(cachePassword) && cachePassword.Length >= 8)
                        cacheCredentials = true;
                    else
                        ConsoleUi.PrintWarning("Password too short. Credentials will not be cached.");
                }
            }

            await commandDispatcher.SendAsync(new Login(username!, passphrase!, cacheCredentials, cachePassword));

            _session.SetLoggedIn(username!);

            ConsoleUi.PrintSuccess($"Logged in as '{username}'.");
            ConsoleUi.PrintInfo("Your identity keys have been derived from your passphrase.");

            if (cacheCredentials)
                ConsoleUi.PrintSuccess("Credentials saved locally (encrypted).");
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Login failed: {ex.Message}");
        }

        return true;
    }
}
