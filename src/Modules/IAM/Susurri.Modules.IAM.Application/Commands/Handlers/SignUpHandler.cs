using Susurri.Shared.Abstractions.Commands;

namespace Susurri.Modules.IAM.Application.Commands.Handlers;

public class SignUpHandler : ICommandHandler<SignUp>
{
    public Task HandleAsync(SignUp command)
    {
        throw new NotImplementedException();
    }
}