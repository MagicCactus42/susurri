namespace Susurri.Client.Exceptions;

public class InvalidUsernameException(string userName) : CustomException($"Username: '{userName}' is invalid.")
{
    public string UserName { get; } = userName;
}