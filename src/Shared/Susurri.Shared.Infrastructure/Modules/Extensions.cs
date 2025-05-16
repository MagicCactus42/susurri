using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Shared.Abstractions.Events;

namespace Susurri.Shared.Infrastructure.Modules;

internal static class Extensions
{
    public static IServiceCollection AddModuleRequest(this IServiceCollection services, IList<Assembly> assemblies)
    {
        services.AddModuleRegistry(assemblies);

        return services;
    }

    private static void AddModuleRegistry(this IServiceCollection services, IList<Assembly> assemblies)
    {
        var registry = new ModuleRegistry();

        var types = assemblies.SelectMany(x => x.GetTypes()).ToArray();

        var eventTypes = types.Where(x => x.IsClass && typeof(IEvent).IsAssignableFrom(x)).ToArray();

        services.AddSingleton<IModuleRegistry>(sp =>
        {
            var eventDispatcher = sp.GetRequiredService<IEventDispatcher>();
            var eventDispatcherType = eventDispatcher.GetType();
            
            foreach (var type in eventTypes)
            {
                registry.AddBroadcastAction(type, @event => (Task) eventDispatcherType
                    .GetMethod(nameof(eventDispatcher.PublishAsync))?
                    .MakeGenericMethod(type).Invoke(eventDispatcher, new[] {@event}));
            }

            return registry;
        });
    }
}