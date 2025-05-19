namespace Susurri.Modules.Users.Core.Exceptions;

public class UserDoesntExistException : CustomException
{
    public UserDoesntExistException(string username) : base($"User with username: {username} doesn't exist.")
    {
    }
}