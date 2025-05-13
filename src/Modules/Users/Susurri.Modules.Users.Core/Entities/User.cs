using Susurri.Modules.Users.Core.ValueObjects;

namespace Susurri.Modules.Users.Core.Entities;

public class User
{
    public UserId UserId { get; private set; }
    public Username Username { get; private set; }
    public byte[] PublicKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
}