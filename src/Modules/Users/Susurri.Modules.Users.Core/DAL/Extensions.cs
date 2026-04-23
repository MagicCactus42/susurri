using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.Users.Core.Abstractions;
using Susurri.Modules.Users.Core.DAL.Repositories;

namespace Susurri.Modules.Users.Core.DAL;

internal static class Extensions
{
    /// <summary>
    /// Connection string key looked up via <see cref="IConfiguration.GetConnectionString"/>.
    /// Set it via appsettings, env var (<c>ConnectionStrings__UsersDb</c>), or
    /// <c>dotnet user-secrets set "ConnectionStrings:UsersDb" "&lt;...&gt;"</c>.
    /// </summary>
    public const string ConnectionStringKey = "UsersDb";

    public static IServiceCollection AddPostgres(this IServiceCollection services)
    {
        // Deferred DbContext construction: we don't have IConfiguration at
        // Register-time, only at Initialize/runtime. The (sp, options) overload
        // resolves IConfiguration on first DbContext request.
        services.AddDbContext<UsersDbContext>((sp, options) =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(ConnectionStringKey);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Missing connection string '{ConnectionStringKey}'. " +
                    "Set it via appsettings.json, the ConnectionStrings__UsersDb env var, " +
                    "or `dotnet user-secrets set \"ConnectionStrings:UsersDb\" \"<connection-string>\"`.");
            }

            options.UseNpgsql(connectionString);
        });

        services.AddScoped<IUserRepository, UserRepository>();

        return services;
    }
}