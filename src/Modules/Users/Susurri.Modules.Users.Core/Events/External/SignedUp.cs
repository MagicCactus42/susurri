using Susurri.Shared.Abstractions.Events;

namespace Susurri.Modules.Users.Core.Events.External;

public record SignedUp(byte[] PublicKey, string Username) : IEvent;