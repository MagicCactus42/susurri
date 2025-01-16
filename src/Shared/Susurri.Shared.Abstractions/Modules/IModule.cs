using Microsoft.Extensions.DependencyInjection;

namespace Susurri.Shared.Abstractions.Modules;

public interface IModule
{
    string Name { get; }
    void Register(IServiceCollection services);
    void Initialize(IServiceProvider provider);
}