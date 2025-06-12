using Susurri.Shared.Abstractions.Exceptions;

namespace Susurri.Modules.Users.Core.Exceptions;

public class UserDoesntExistException : SusurriException
{
    public UserDoesntExistException(string username) : base($"User with username: {username} doesn't exist.")
    {
    }
}