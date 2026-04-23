using Susurri.Modules.Users.Core.ValueObjects;

namespace Susurri.Modules.Users.Core.Entities;

public class User
{
    // EF Core materializes via reflection after the parameterless ctor,
    // so the compiler can't see that these will be set. `null!` documents
    // the EF contract; if any property is observed null at runtime, the DB
    // schema is the bug.
    public UserId UserId { get; set; } = null!;
    public Username Username { get; set; } = null!;
    public byte[] PublicKey { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}