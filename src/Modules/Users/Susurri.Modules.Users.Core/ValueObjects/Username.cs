using Susurri.Modules.Users.Core.Exceptions;

namespace Susurri.Modules.Users.Core.ValueObjects;

public sealed record Username
{
    public string Value { get; }
    
    private Username(){}
    
    public Username(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is > 30 or < 3)
            throw new InvalidUsernameException(value);
        
        Value = value;
    }
    
    public static implicit operator string(Username username) => username.Value;
    public static implicit operator Username(string username) => new(username);
    public override string ToString() => Value;
}