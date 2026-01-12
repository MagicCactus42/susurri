using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.DHT.Core;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Modules.DHT.Application;

internal class DHTModule : IModule
{
    public string Name { get; } = "DHT";
    
    public void Register(IServiceCollection services)
    {
        services.AddCore();
    }

    public void Initialize(IServiceProvider provider)
    {
    }
}