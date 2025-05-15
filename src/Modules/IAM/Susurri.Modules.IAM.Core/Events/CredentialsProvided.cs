using Susurri.Shared.Abstractions.Events;

namespace Susurri.Modules.IAM.Core.Events;

public record CredentialsProvided(byte[] PublicKey, string Username) : IEvent;