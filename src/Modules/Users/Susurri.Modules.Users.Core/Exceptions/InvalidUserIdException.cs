namespace Susurri.Modules.Users.Core.Exceptions;

public class InvalidUserIdException : CustomException
{
    public InvalidUserIdException(Guid userId) : base($"User id: {userId} is invalid}}")
    {
    }
}