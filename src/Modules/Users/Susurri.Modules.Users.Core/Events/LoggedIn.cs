using Susurri.Shared.Abstractions.Events;

namespace Susurri.Modules.Users.Core.Events;

public record LoggedIn(string Username) : IEvent; // maybe return username with auth token