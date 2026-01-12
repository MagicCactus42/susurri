using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.Users.Core.DAL;

[assembly: InternalsVisibleTo("Susurri.Modules.Users.Application")]
namespace Susurri.Modules.Users.Core;

internal static class Extensions
{
    public static IServiceCollection AddCore(this IServiceCollection services)
        => services
            .AddPostgres();
}