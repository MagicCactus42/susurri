using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Susurri.Shared.Abstractions.Modules;
using Susurri.Shared.Infrastructure;

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
        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true).Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(c => c.AddConsole());
        
        services.AddInfrastructure(_assemblies);
        foreach (var module in _modules)
        {
            module.Register(services);
        }
        
        services.AddScoped<MainWindow>();
    }
}