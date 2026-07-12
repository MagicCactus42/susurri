using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Network;
using Susurri.Modules.DHT.Core.Onion;
using Susurri.Modules.DHT.Core.Services;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Crypto;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.CLI.Commands;

internal sealed class LoginCommand : ICommand
{
    private readonly IServiceProvider _services;
    private readonly SessionState _session;

    public string Name => "login";
    public string Description => "Derive your identity and go online";
    public string HelpLine => "  login [username] [port]  - Derive identity from your passphrase and connect";

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

        var port = 0;
        if (args.Length >= 2 && int.TryParse(args[1], out var customPort))
            port = customPort;

        var (username, passphrase, cachePin, wantsCache) = ResolveCredentials(args);
        if (username == null || passphrase == null)
            return true;

        if (!ValidateUsername(username) || !ValidatePassphrase(passphrase))
            return true;

        ConsoleUi.PrintInfo("Deriving identity keys (PBKDF2, this takes a moment)...");

        try
        {
            var cryptoGen = _services.GetRequiredService<ICryptoKeyGenerator>();
            var salt = Identity.DeriveSalt(username);
            var keyPair = cryptoGen.GenerateKeyPair(passphrase, salt);

            var config = _services.GetRequiredService<IConfiguration>();
            var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
            var options = NodeConfig.ChatOptions(config);

            // ChatService takes ownership of the two keys (its DHT node disposes
            // them), so we do not dispose keyPair here.
            var chat = new ChatService(
                keyPair.EncryptionKey,
                loggerFactory.CreateLogger<ChatService>(),
                loggerFactory.CreateLogger<KademliaDhtNode>(),
                loggerFactory.CreateLogger<OnionRouter>(),
                loggerFactory.CreateLogger<RelayService>(),
                loggerFactory.CreateLogger<ConnectionManager>(),
                keyPair.SigningKey,
                options);

            chat.OnMessageReceived += received =>
            {
                ConsoleUi.PrintIncoming(received.SenderUsername ?? Convert.ToHexString(received.SenderPublicKey)[..16], received.Content);
                return Task.CompletedTask;
            };

            chat.OnGroupMessageReceived += received =>
            {
                var sender = received.SenderUsername ?? Convert.ToHexString(received.SenderPublicKey)[..16];
                ConsoleUi.PrintIncoming($"{received.GroupName}/{sender}", received.Content);
                return Task.CompletedTask;
            };

            var seeds = NodeConfig.Seeds(config, Array.Empty<string>())
                .Select(e => $"{e.Address}:{e.Port}");

            await chat.StartAsync(port, username, seeds).ConfigureAwait(false);
            _session.SetChat(username, chat);

            if (wantsCache && !string.IsNullOrEmpty(cachePin))
            {
                var cache = _services.GetService<ICredentialsCache>();
                if (cache != null)
                {
                    await cache.SaveAsync(username, passphrase, cachePin).ConfigureAwait(false);
                    ConsoleUi.PrintSuccess("Credentials saved locally (encrypted).");
                }
            }

            ConsoleUi.PrintSuccess($"Online as '{username}'.");
            ConsoleUi.PrintInfo($"  Listening on port {chat.LocalPort} (TCP + UDP)");
            ConsoleUi.PrintInfo($"  Peers: {chat.PeerCount}");
            if (chat.PeerCount == 0)
                ConsoleUi.PrintWarning("No peers yet — set DHT:BootstrapNodes or pass a seed so you can reach others.");
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Login failed: {ex.Message}");
            await _session.ClearChatAsync().ConfigureAwait(false);
        }

        return true;
    }

    private (string? Username, string? Passphrase, string? CachePin, bool WantsCache) ResolveCredentials(string[] args)
    {
        var credentialsCache = _services.GetService<ICredentialsCache>();

        if (credentialsCache?.Exists() == true && args.Length == 0)
        {
            ConsoleUi.PrintInfo("Cached credentials found.");
            Console.Write("  Cache password (or Enter to skip): ");
            var pin = ConsoleInput.ReadPassword();
            if (!string.IsNullOrEmpty(pin))
            {
                try
                {
                    var cached = credentialsCache.Load(pin);
                    return (cached.Username, cached.Passphrase, null, false);
                }
                catch (Exception ex)
                {
                    ConsoleUi.PrintWarning($"Could not load cached credentials: {ex.Message}");
                }
            }
        }

        string? username;
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
            return (null, null, null, false);
        }

        Console.Write("  Passphrase (6+ word BIP39 mnemonic; use 'generate' to create one): ");
        var passphrase = ConsoleInput.ReadPassword();
        if (string.IsNullOrEmpty(passphrase))
        {
            ConsoleUi.PrintError("Passphrase cannot be empty.");
            return (null, null, null, false);
        }

        string? cachePin = null;
        var wantsCache = false;
        if (credentialsCache != null && credentialsCache.Exists() == false)
        {
            Console.Write("  Save credentials locally for next time? [y/N]: ");
            var save = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (save is "y" or "yes")
            {
                Console.Write("  Password to protect cached credentials (8+ chars): ");
                cachePin = ConsoleInput.ReadPassword();
                wantsCache = !string.IsNullOrEmpty(cachePin) && cachePin.Length >= 8;
                if (!wantsCache)
                    ConsoleUi.PrintWarning("Password too short — credentials will not be cached.");
            }
        }

        return (username, passphrase, cachePin, wantsCache);
    }

    private static bool ValidateUsername(string username)
    {
        if (username.Length < SecurityLimits.MinUsernameLength || username.Length > SecurityLimits.MaxUsernameLength)
        {
            ConsoleUi.PrintError($"Username must be {SecurityLimits.MinUsernameLength}-{SecurityLimits.MaxUsernameLength} characters.");
            return false;
        }

        if (username.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            ConsoleUi.PrintError("Username may only contain letters, digits, underscores, and hyphens.");
            return false;
        }

        return true;
    }

    private static bool ValidatePassphrase(string passphrase)
    {
        var words = passphrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < SecurityLimits.MinPassphraseWords)
        {
            ConsoleUi.PrintError($"Passphrase must be at least {SecurityLimits.MinPassphraseWords} words. Use 'generate' to create one.");
            return false;
        }
        if (words.Length > SecurityLimits.MaxPassphraseWords)
        {
            ConsoleUi.PrintError($"Passphrase cannot exceed {SecurityLimits.MaxPassphraseWords} words.");
            return false;
        }
        return true;
    }
}
