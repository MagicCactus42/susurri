using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Shared.Abstractions.Auth;
using Susurri.Shared.Abstractions.Time;
using Susurri.Shared.Infrastructure.Auth;
using Susurri.Shared.Infrastructure.Commands;
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
        services.AddCommands(assemblies);
        services.AddModuleRequest(assemblies);
        services.AddSingleton<IClock, Clock>();
        services.AddSingleton<ISignatureManager, SignatureManager>();
        services.AddEvents(assemblies);
        return services;
    }
    
    public static T GetOptions<T>(this IServiceCollection services, string sectionName) where T : new()
    {
        using var serviceProvider = services.BuildServiceProvider();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return configuration.GetOptions<T>(sectionName);
    }

    public static T GetOptions<T>(this IConfiguration configuration, string sectionName) where T : new()
    {
        var options = new T();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }
}