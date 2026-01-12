using Susurri.Modules.Users.Core.Abstractions;
using Susurri.Modules.Users.Core.Exceptions;
using Susurri.Shared.Abstractions.Events;
using Susurri.Shared.Abstractions.Messaging;

namespace Susurri.Modules.Users.Core.Events.External.Handlers;

public sealed class CredentialsProvidedHandler : IEventHandler<CredentialsProvided>
{
    private readonly IUserRepository _userRepository;
    private readonly IMessageBroker _messageBroker;

    public CredentialsProvidedHandler(IUserRepository userRepository, IMessageBroker messageBroker)
    {
        _userRepository = userRepository;
        _messageBroker = messageBroker;
    }

    public async Task HandleAsync(CredentialsProvided @event)
    {
        var targetKey = await _userRepository.GetKeyByUsernameAsync(@event.Username);

        if (targetKey is null)
        {
            await _messageBroker.PublishAsync(new SignedUp(@event.PublicKey, @event.Username));
        }

        if (@event.PublicKey.Equals(targetKey))
        {
            await _messageBroker.PublishAsync(new LoggedIn(@event.Username, @event.PublicKey));
        }
    }
}