using Microsoft.EntityFrameworkCore;
using Susurri.Modules.Users.Core.DAL;

namespace Susurri.Tests.Unit;

internal class TestDatabase : IDisposable, IAsyncDisposable
{
    public UsersDbContext DbContext { get; }

    public TestDatabase()
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres");

        DbContext = new UsersDbContext(optionsBuilder.Options);
    }

    public void Dispose()
    {
        DbContext.Database.EnsureDeleted();
        DbContext.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DbContext.Database.EnsureDeletedAsync();
        await DbContext.DisposeAsync();
    }
}