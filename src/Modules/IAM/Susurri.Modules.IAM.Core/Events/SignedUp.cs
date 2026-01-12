using Susurri.Shared.Abstractions.Events;
using Susurri.Shared.Abstractions.Messaging;

namespace Susurri.Modules.IAM.Core.Events;

public record SignedUp(byte[] PublicKey, string Username) : IEvent;