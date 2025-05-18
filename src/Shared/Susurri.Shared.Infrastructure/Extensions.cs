using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Shared.Abstractions.Time;
using Susurri.Shared.Infrastructure.Events;
using Susurri.Shared.Infrastructure.Messaging;
using Susurri.Shared.Infrastructure.Modules;
using Susurri.Shared.Infrastructure.Time;

[assembly: InternalsVisibleTo("Susurri.Bootstrapper")]
namespace Susurri.Shared.Infrastructure;

internal static class Extensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IList<Assembly> assemblies)
    {
        services.AddMessaging();
        services.AddModuleRequest(assemblies);
        services.AddSingleton<IClock, Clock>();
        services.AddEvents(assemblies);
        return services;
    }
}