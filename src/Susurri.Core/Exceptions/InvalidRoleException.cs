namespace Susurri.Core.Exceptions;

public sealed class InvalidRoleException(string role) : CustomException($"Role: '{role}' is invalid.")
{
    public string Role { get; } = role;
}