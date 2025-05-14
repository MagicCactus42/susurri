using Susurri.Shared.Abstractions.Commands;

namespace Susurri.Modules.IAM.Application.Commands;

public record SignUp(Guid UserId, string Username, string PublicKey) : ICommand;