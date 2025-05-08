namespace Susurri.Modules.Users.Core.Exceptions;

public sealed class InvalidUsernameException : CustomException
{
    public InvalidUsernameException(string username) : base($"Username: {username} is invalid.")
    {
    }
}