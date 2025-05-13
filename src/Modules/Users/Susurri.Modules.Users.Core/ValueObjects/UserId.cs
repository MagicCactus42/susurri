using Susurri.Modules.Users.Core.Exceptions;

namespace Susurri.Modules.Users.Core.ValueObjects;

public sealed record UserId
{
    public Guid Value { get; }

    private UserId(){}
    
    public UserId(Guid value)
    {
        if (value == Guid.Empty)
            throw new InvalidUserIdException(value);
        
        Value = value;
    }

    public static implicit operator Guid(UserId userId) => userId.Value;
    public static implicit operator UserId(Guid userId) => new(userId);
}