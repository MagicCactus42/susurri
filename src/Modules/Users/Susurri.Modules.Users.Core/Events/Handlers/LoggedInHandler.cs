using Susurri.Shared.Abstractions.Events;

namespace Susurri.Modules.Users.Core.Events.Handlers;

internal sealed class LoggedInHandler : IEventHandler<LoggedIn>
{
    public async Task HandleAsync(LoggedIn @event) // TODO: here create jwt once its implemented
    {
    }
}