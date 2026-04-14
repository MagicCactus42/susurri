using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Shared.Abstractions.Events;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Shared.Infrastructure.Modules;

internal static class Extensions
{
    public static IServiceCollection AddModuleRequest(this IServiceCollection services, IList<Assembly> assemblies)
    {
        services.AddModuleRegistry(assemblies);
        services.AddSingleton<IModuleSerializer, JsonModuleSerializer>();
        services.AddSingleton<IModuleClient, ModuleClient>();

        return services;
    }

    private static void AddModuleRegistry(this IServiceCollection services, IList<Assembly> assemblies)
    {
        var registry = new ModuleRegistry();

        var types = assemblies.SelectMany(a =>
        {
            try
            {
                return (IEnumerable<Type>)a.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>();
            }
        }).ToArray();

        var eventTypes = types.Where(x => x.IsClass && typeof(IEvent).IsAssignableFrom(x)).ToArray();

        services.AddSingleton<IModuleRegistry>(sp =>
        {
            var eventDispatcher = sp.GetRequiredService<IEventDispatcher>();
            var eventDispatcherType = eventDispatcher.GetType();

            foreach (var type in eventTypes)
            {
                var publishMethod = eventDispatcherType.GetMethod(nameof(eventDispatcher.PublishAsync))
                    ?? throw new InvalidOperationException(
                        $"{eventDispatcherType.Name} is missing required method PublishAsync");

                registry.AddBroadcastAction(type, @event => (Task)publishMethod
                    .MakeGenericMethod(type)
                    .Invoke(eventDispatcher, new[] { @event })!);
            }

            return registry;
        });
    }
}