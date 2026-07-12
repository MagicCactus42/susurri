using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Susurri.CLI.Network;
using Susurri.CLI.Tui;
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

        try
        {
            var cryptoGen = _services.GetRequiredService<ICryptoKeyGenerator>();
            var salt = Identity.DeriveSalt(username);
            var keyPair = await ConsoleUi.WithSpinnerAsync("deriving identity keys (PBKDF2, 600k rounds)",
                () => Task.Run(() => cryptoGen.GenerateKeyPair(passphrase, salt))).ConfigureAwait(false);

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
                options,
                keyPair.LocalStoreKey,
                loggerFactory.CreateLogger<FileTransferService>());

            chat.OnMessageReceived += received =>
            {
                if (!_session.TuiActive)
                    ConsoleLineReader.Shared.WriteInterrupting(() =>
                        ConsoleUi.PrintIncoming(received.SenderUsername ?? Convert.ToHexString(received.SenderPublicKey)[..16], received.Content));
                return Task.CompletedTask;
            };

            chat.OnGroupMessageReceived += received =>
            {
                if (!_session.TuiActive)
                {
                    var sender = received.SenderUsername ?? Convert.ToHexString(received.SenderPublicKey)[..16];
                    ConsoleLineReader.Shared.WriteInterrupting(() =>
                        ConsoleUi.PrintIncoming($"{received.GroupName}/{sender}", received.Content));
                }
                return Task.CompletedTask;
            };

            chat.OnFileTransferRequested += info =>
            {
                if (!_session.TuiActive)
                {
                    ConsoleLineReader.Shared.WriteInterrupting(() =>
                    {
                        Console.WriteLine();
                        ConsoleUi.PrintInfo(
                            $"📎 Incoming file '{info.FileName}' ({FileCommand.FormatBytes(info.FileSize)}) " +
                            $"— accept with 'file accept {info.TransferId.ToString()[..8]}' or 'file reject ...'");
                    });
                }
                return Task.CompletedTask;
            };

            chat.OnFileTransferCompleted += done =>
            {
                if (_session.TuiActive)
                    return Task.CompletedTask;
                try
                {
                    var path = Downloads.Save(done.FileName, done.FileData);
                    ConsoleLineReader.Shared.WriteInterrupting(() =>
                        ConsoleUi.PrintSuccess($"Received '{done.FileName}' → {path}"));
                }
                catch (Exception ex)
                {
                    ConsoleLineReader.Shared.WriteInterrupting(() =>
                        ConsoleUi.PrintError($"Received '{done.FileName}' but could not save it: {ex.Message}"));
                }
                return Task.CompletedTask;
            };

            chat.OnFileTransferFailed += (_, reason) =>
            {
                if (!_session.TuiActive)
                    ConsoleLineReader.Shared.WriteInterrupting(() =>
                        ConsoleUi.PrintWarning($"File transfer failed: {reason}"));
                return Task.CompletedTask;
            };

            var history = keyPair.LocalStoreKey != null
                ? new HistoryStore(keyPair.LocalStoreKey, chat.LocalPublicKey)
                : null;
            var conversations = new ConversationStore(chat, username, history);

            var seedEndpoints = NodeConfig.Seeds(config, Array.Empty<string>());
            var seeds = seedEndpoints.Select(e => $"{e.Address}:{e.Port}");

            await ConsoleUi.WithSpinnerAsync("joining the network (dht bootstrap + onion setup)",
                () => chat.StartAsync(port, username, seeds)).ConfigureAwait(false);
            _session.SetChat(username, chat, conversations, history);

            await VerifyPinnedSeedsAsync(seedEndpoints, ct).ConfigureAwait(false);

            if (wantsCache && !string.IsNullOrEmpty(cachePin))
            {
                var cache = _services.GetService<ICredentialsCache>();
                if (cache != null)
                {
                    await cache.SaveAsync(username, passphrase, cachePin).ConfigureAwait(false);
                    ConsoleUi.PrintSuccess("Credentials saved locally (encrypted).");
                }
            }

            Console.WriteLine();
            ConsoleUi.Panel("online", new[]
            {
                ("user", username, Palette.Accent),
                ("port", $"{chat.LocalPort} (tcp + udp)", Palette.Text),
                ("peers", chat.PeerCount.ToString(), chat.PeerCount > 0 ? Palette.Green : Palette.Red),
                ("history", history?.Enabled == true ? "on (encrypted)" : "off ('history on' to persist)", Palette.Text)
            }, Palette.Green);
            if (chat.PeerCount == 0)
                ConsoleUi.PrintWarning("No peers yet — set DHT:BootstrapNodes or pass a seed so you can reach others.");
            else
                Console.WriteLine($"  {ConsoleUi.Faint("try 'chats' for the full-screen browser")}");
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Login failed: {ex.Message}");
            await _session.ClearChatAsync().ConfigureAwait(false);
        }

        return true;
    }

    private static async Task VerifyPinnedSeedsAsync(IEnumerable<System.Net.IPEndPoint> seeds, CancellationToken ct)
    {
        IReadOnlyList<AttestationResult> results;
        try
        {
            results = await BootstrapVerifier.VerifyPinnedAsync(seeds, ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        foreach (var result in results)
        {
            switch (result.Status)
            {
                case AttestationStatus.Verified:
                    ConsoleUi.PrintSuccess($"bootstrap {result.Endpoint} verified — attestation matches the pinned fingerprint {result.FingerprintShort}");
                    break;
                case AttestationStatus.Unreachable:
                    ConsoleUi.PrintWarning($"bootstrap {result.Endpoint} is pinned but its attestation endpoint is unreachable — could not verify");
                    break;
                case AttestationStatus.FingerprintMismatch:
                    ConsoleUi.PrintError($"bootstrap {result.Endpoint} FINGERPRINT MISMATCH — the node's config or binary differs from the pin; treat with suspicion");
                    break;
                case AttestationStatus.KeyMismatch:
                    ConsoleUi.PrintError($"bootstrap {result.Endpoint} IDENTITY KEY MISMATCH — this is not the node you pinned; possible impersonation");
                    break;
                case AttestationStatus.SignatureInvalid:
                    ConsoleUi.PrintError($"bootstrap {result.Endpoint} SIGNATURE INVALID — the attestation is not signed by the pinned key");
                    break;
                default:
                    ConsoleUi.PrintWarning($"bootstrap {result.Endpoint} returned a malformed attestation");
                    break;
            }
        }
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
