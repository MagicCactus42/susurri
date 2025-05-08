using Susurri.Modules.Users.Core.ValueObjects;

namespace Susurri.Modules.Users.Core.Entities;

public class User
{
    public UserId UserId { get; private set; }
    public Username Username { get; private set; }
    public string PublicKey { get; private set; } // maybe change to value object
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
}