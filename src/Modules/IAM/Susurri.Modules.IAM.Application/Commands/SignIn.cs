using Susurri.Shared.Abstractions.Commands;

namespace Susurri.Modules.IAM.Application.Commands;

public record SignIn(Guid UserId, string Username, string PublicKey) : ICommand;