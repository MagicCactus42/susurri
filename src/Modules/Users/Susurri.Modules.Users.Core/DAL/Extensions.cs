using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.Users.Core.Abstractions;
using Susurri.Modules.Users.Core.DAL.Repositories;

namespace Susurri.Modules.Users.Core.DAL;

internal static class Extensions
{
    public static IServiceCollection AddPostgres(this IServiceCollection services)
    {
        const string connectionString = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
        
        services.AddDbContext<UsersDbContext>(x =>
            x.UseNpgsql(connectionString));

        services.AddScoped<IUserRepository, UserRepository>();
        
        return services;
    }
}