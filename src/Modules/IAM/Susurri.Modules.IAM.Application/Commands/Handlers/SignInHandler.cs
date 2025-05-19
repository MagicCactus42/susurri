using Susurri.Shared.Abstractions.Commands;
using Susurri.Shared.Abstractions.Messaging;
using Susurri.Shared.Abstractions.Time;

namespace Susurri.Modules.IAM.Application.Commands.Handlers;

public class SignInHandler : ICommandHandler<SignIn>
{
    private readonly IClock _clock;
    private readonly IMessageBroker _messageBroker;

    public SignInHandler(IClock clock, IMessageBroker messageBroker)
    {
        _clock = clock;
        _messageBroker = messageBroker;
    }

    public Task HandleAsync(SignIn command)
    {
        return Task.CompletedTask;
    }
}