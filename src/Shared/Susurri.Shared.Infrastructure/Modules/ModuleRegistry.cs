﻿namespace Susurri.Shared.Infrastructure.Modules;

internal sealed class ModuleRegistry : IModuleRegistry
{
    private readonly List<ModuleBroadcastRegistration> _broadcastRegistrations = new();

    public IEnumerable<ModuleBroadcastRegistration> GetModuleBroadcastRegistrations(string key) 
        => _broadcastRegistrations.Where(x => x.Key == key);

    public void AddBroadcastAction(Type requestType, Func<object, Task> action)
    {
        if (string.IsNullOrWhiteSpace(requestType.Namespace))
        {
            throw new InvalidOperationException("Missing namespace.");
        }

        var registration = new ModuleBroadcastRegistration(requestType, action);
        _broadcastRegistrations.Add(registration);
    }
}