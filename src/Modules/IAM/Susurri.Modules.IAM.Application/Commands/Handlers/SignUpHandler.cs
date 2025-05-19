using NSec.Cryptography;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Events;
using Susurri.Shared.Abstractions.Commands;
using Susurri.Shared.Abstractions.Messaging;
using Susurri.Shared.Abstractions.Time;

namespace Susurri.Modules.IAM.Application.Commands.Handlers;

public class SignUpHandler : ICommandHandler<SignUp>
{
    private readonly IClock _clock;
    private readonly IMessageBroker _messageBroker;
    private readonly IKeyGenerator _keyGenerator;

    public SignUpHandler(IClock clock, IMessageBroker messageBroker, IKeyGenerator keyGenerator)
    {
        _clock = clock;
        _messageBroker = messageBroker;
        _keyGenerator = keyGenerator;
    }
    
    public async Task HandleAsync(SignUp command)
    {
        var key = _keyGenerator.GenerateKeys(command.Passphrase);

        await _messageBroker.PublishAsync(new CredentialsProvided(key.PublicKey.Export(KeyBlobFormat.RawPublicKey), command.Username));
    }
}