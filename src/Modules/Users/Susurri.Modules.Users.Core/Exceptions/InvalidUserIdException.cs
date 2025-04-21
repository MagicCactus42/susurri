using Susurri.Shared.Abstractions.Exceptions;

namespace Susurri.Modules.Users.Core.Exceptions;

public class InvalidUserIdException : SusurriException
{
    public InvalidUserIdException(Guid userId) : base($"User id: {userId} is invalid}}")
    {
    }
}