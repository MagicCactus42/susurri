using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Events;
using Susurri.Shared.Abstractions.Commands;
using Susurri.Shared.Abstractions.Messaging;
using Susurri.Shared.Abstractions.Time;

namespace Susurri.Modules.IAM.Application.Commands.Handlers;

public class LoginHandler : ICommandHandler<Login>
{
    private readonly IClock _clock;
    private readonly IMessageBroker _messageBroker;
    private readonly IKeyGenerator _keyGenerator;

    public LoginHandler(IClock clock, IMessageBroker messageBroker, IKeyGenerator keyGenerator)
    {
        _clock = clock;
        _messageBroker = messageBroker;
        _keyGenerator = keyGenerator;
    }
    
    public async Task HandleAsync(Login command)
    {
        var key = _keyGenerator.GenerateKeys(command.Passphrase);
        var username = command.Username;

        await _messageBroker.PublishAsync(new CredentialsProvided(key.PublicKey.Export(KeyBlobFormat.RawPublicKey), username));
    }
}