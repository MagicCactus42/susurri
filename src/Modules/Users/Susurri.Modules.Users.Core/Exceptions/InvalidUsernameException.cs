using Susurri.Shared.Abstractions.Exceptions;

namespace Susurri.Modules.Users.Core.Exceptions;

public sealed class InvalidUsernameException : SusurriException
{
    public InvalidUsernameException(string username) : base($"Username: {username} is invalid.")
    {
    }
}