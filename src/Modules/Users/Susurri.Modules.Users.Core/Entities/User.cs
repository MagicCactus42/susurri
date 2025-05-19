using Susurri.Modules.Users.Core.ValueObjects;

namespace Susurri.Modules.Users.Core.Entities;

public class User
{
    public UserId UserId { get; set; }
    public Username Username { get; set; }
    public byte[] PublicKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}