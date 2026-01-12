using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.Users.Core;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Modules.Users.Application;

internal class UsersModule : IModule
{
    public string Name { get; } = "Users";
    public void Register(IServiceCollection services)
    {
        services.AddCore();
    }

    public void Initialize(IServiceProvider provider)
    {
    }
}