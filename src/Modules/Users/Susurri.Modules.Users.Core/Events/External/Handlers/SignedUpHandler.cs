using Microsoft.Extensions.Logging;
using Susurri.Modules.Users.Core.Abstractions;
using Susurri.Modules.Users.Core.Entities;
using Susurri.Shared.Abstractions.Events;
using Susurri.Shared.Abstractions.Time;

namespace Susurri.Modules.Users.Core.Events.External.Handlers;

public sealed class SignedUpHandler : IEventHandler<SignedUp>
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<SignedUpHandler> _logger;
    private readonly IClock _clock;

    public SignedUpHandler(IUserRepository userRepository, ILogger<SignedUpHandler> logger, IClock clock)
    {
        _userRepository = userRepository;
        _logger = logger;
        _clock = clock;
    }

    public async Task HandleAsync(SignedUp @event)
    {
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = @event.Username,
            PublicKey = @event.PublicKey,
            CreatedAt = _clock.CurrentTime(),
            LastSeenAt = _clock.CurrentTime()
        };
        
        await _userRepository.AddAsync(user);
        _logger.LogInformation($"User {user.Username} created with userId {user.UserId}");
    }
}