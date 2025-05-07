using Susurri.Shared.Abstractions.Events;

namespace Susurri.Modules.Users.Core.Events.External;

public record CredentialsProvided(byte[] PublicKey, string Username) : IEvent;