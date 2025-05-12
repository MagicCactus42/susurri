using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Bootstrapper;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider _serviceProvider;
    private readonly IList<IModule> _modules;
    private readonly IList<Assembly> _assemblies;

    public App()
    {
        _assemblies = ModuleLoader.LoadAssemblies();
        _modules = ModuleLoader.LoadModules(_assemblies);
    }
    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        foreach (var module in _modules)
        {
            module.Initialize(_serviceProvider);
            
        }

        Console.WriteLine($"Modules: {string.Join(", ", _modules.Select(x => x.Name))}");
        
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        foreach (var module in _modules)
        {
            module.Register(services);
        }
        
        services.AddScoped<MainWindow>();
    }
}