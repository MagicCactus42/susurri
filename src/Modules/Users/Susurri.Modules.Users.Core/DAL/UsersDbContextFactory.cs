using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Susurri.Modules.Users.Core.DAL;

/// <summary>
/// Used only by `dotnet ef migrations add / database update` at design time —
/// the runtime DbContext is registered via <see cref="Extensions.AddPostgres"/>.
/// Reads the connection string from the <c>SUSURRI_USERSDB_CONNECTION</c>
/// environment variable so no credentials live in the repo. The localhost
/// fallback is for `dotnet ef migrations add` only (no DB connection needed
/// for that command); production migrations should always set the env var.
/// </summary>
public class UsersDbContextFactory : IDesignTimeDbContextFactory<UsersDbContext>
{
    public const string ConnectionEnvVar = "SUSURRI_USERSDB_CONNECTION";

    public UsersDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();

        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar)
            // Design-time fallback: only the schema name is real; the password is
            // a placeholder that obviously fails on any non-localhost DB.
            ?? "Host=localhost;Port=5432;Database=susurri_dev;Username=susurri;Password=DESIGN_TIME_PLACEHOLDER";

        optionsBuilder.UseNpgsql(connectionString);

        return new UsersDbContext(optionsBuilder.Options);
    }
}