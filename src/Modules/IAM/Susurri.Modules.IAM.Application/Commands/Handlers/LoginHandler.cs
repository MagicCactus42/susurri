using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Events;
using Susurri.Shared.Abstractions.Commands;
using Susurri.Shared.Abstractions.Messaging;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.IAM.Application.Commands.Handlers;

public class LoginHandler : ICommandHandler<Login>
{
    private readonly IMessageBroker _messageBroker;
    private readonly IKeyGenerator _keyGenerator;
    private readonly IInMemoryCredentialsCache _inMemoryCredentialsCache;
    private readonly ICredentialsCache _credentialsCache;
    private readonly ILogger<LoginHandler> _logger;

    public LoginHandler(
        IMessageBroker messageBroker,
        IKeyGenerator keyGenerator,
        IInMemoryCredentialsCache inMemoryCredentialsCache,
        ICredentialsCache credentialsCache,
        ILogger<LoginHandler> logger)
    {
        _messageBroker = messageBroker;
        _keyGenerator = keyGenerator;
        _inMemoryCredentialsCache = inMemoryCredentialsCache;
        _credentialsCache = credentialsCache;
        _logger = logger;
    }

    public async Task HandleAsync(Login command)
    {
        ValidateUsername(command.Username);
        ValidatePassphrase(command.Passphrase);

        var key = _keyGenerator.GenerateKeys(command.Passphrase);
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        _inMemoryCredentialsCache.Set(command.Username, command.Passphrase, publicKey);

        if (command.CacheCredentials && !string.IsNullOrEmpty(command.CachePassword))
        {
            ValidateCachePassword(command.CachePassword);
            await _credentialsCache.SaveAsync(command.Username, command.Passphrase, command.CachePassword);
            _logger.LogInformation("Credentials cached for user {Username}", command.Username);
        }

        await _messageBroker.PublishAsync(new CredentialsProvided(publicKey, command.Username));

        _logger.LogInformation("User {Username} logged in successfully", command.Username);
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be empty", nameof(username));

        if (username.Length < 3 || username.Length > 32)
            throw new ArgumentException("Username must be between 3 and 32 characters", nameof(username));

        foreach (var c in username)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                throw new ArgumentException("Username can only contain letters, digits, underscores, and hyphens", nameof(username));
        }
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("Passphrase cannot be empty", nameof(passphrase));

        var words = passphrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length < SecurityLimits.MinPassphraseWords)
            throw new ArgumentException(
                $"Passphrase must contain at least {SecurityLimits.MinPassphraseWords} words. " +
                "Use a BIP39 mnemonic or generate one with the 'generate' command.",
                nameof(passphrase));

        if (words.Length > SecurityLimits.MaxPassphraseWords)
            throw new ArgumentException(
                $"Passphrase cannot exceed {SecurityLimits.MaxPassphraseWords} words",
                nameof(passphrase));
    }

    private static void ValidateCachePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Cache password cannot be empty", nameof(password));

        if (password.Length < SecurityLimits.MinCachePasswordLength)
            throw new ArgumentException(
                $"Cache password must be at least {SecurityLimits.MinCachePasswordLength} characters",
                nameof(password));
    }
}
