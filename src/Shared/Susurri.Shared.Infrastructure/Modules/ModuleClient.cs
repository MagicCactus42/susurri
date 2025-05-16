using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Shared.Infrastructure.Modules;

internal sealed class ModuleClient : IModuleClient
{
    private readonly IModuleRegistry _moduleRegistry;

    public ModuleClient(IModuleRegistry moduleRegistry)
    {
        _moduleRegistry = moduleRegistry;
    }

    public Task PublishAsync(object message)
    {
        var key = message.GetType().Name;
        var registrations = _moduleRegistry.GetModuleBroadcastRegistrations(key);

        foreach (var registration in registrations)
        {
            registration.Action(message);
        }
    }
}