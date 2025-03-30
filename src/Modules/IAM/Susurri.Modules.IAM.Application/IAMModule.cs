using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.IAM.Core;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Modules.IAM.Application;

internal class IAMModule : IModule
{
    public string Name { get; } = "IAM";
    
    public void Register(IServiceCollection services)
    {
        services.AddCore();
    }

    public void Initialize(IServiceProvider provider)
    {
    }
}