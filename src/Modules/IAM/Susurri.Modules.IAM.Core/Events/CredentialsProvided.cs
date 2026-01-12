using Susurri.Shared.Abstractions.Events;
using Susurri.Shared.Abstractions.Messaging;

namespace Susurri.Modules.IAM.Core.Events;

public record CredentialsProvided(byte[] PublicKey, string Username) : IEvent;