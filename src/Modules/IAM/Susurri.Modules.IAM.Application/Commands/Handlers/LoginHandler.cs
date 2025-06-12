using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Events;
using Susurri.Shared.Abstractions.Commands;
using Susurri.Shared.Abstractions.Messaging;

namespace Susurri.Modules.IAM.Application.Commands.Handlers;

public class LoginHandler : ICommandHandler<Login>
{
    private readonly IMessageBroker _messageBroker;
    private readonly IKeyGenerator _keyGenerator;
    private readonly ICredentialsCache _credentialsCache;
    private readonly IInMemoryCredentialsCache _inMemoryCredentialsCache;
    private readonly ILogger<LoginHandler> _logger;

    public LoginHandler(IMessageBroker messageBroker, IKeyGenerator keyGenerator, ICredentialsCache credentialsCache, IInMemoryCredentialsCache inMemoryCredentialsCache, ILogger<LoginHandler> logger)
    {
        _messageBroker = messageBroker;
        _keyGenerator = keyGenerator;
        _credentialsCache = credentialsCache;
        _inMemoryCredentialsCache = inMemoryCredentialsCache;
        _logger = logger;
    }
    
    public async Task HandleAsync(Login command)
    {
        var key = _keyGenerator.GenerateKeys(command.Passphrase);
        var username = command.Username;
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        
        _inMemoryCredentialsCache.Set(username, command.Passphrase, publicKey);
        
        await _credentialsCache.SaveAsync(username, command.Passphrase, "0000"); // make feature switch - maybe user doesnt want to save private key
        
        await _messageBroker.PublishAsync(new CredentialsProvided(publicKey, username));
    }
}