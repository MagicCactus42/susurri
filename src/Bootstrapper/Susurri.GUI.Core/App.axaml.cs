using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Susurri.GUI.Services;
using Susurri.GUI.ViewModels;
using Susurri.GUI.Views;
using Susurri.Modules.IAM.Application;

namespace Susurri.GUI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services, ApplicationLifetime is ISingleViewApplicationLifetime);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
            desktop.Exit += (_, _) =>
            {
                try
                {
                    Services.GetRequiredService<AppSession>().DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            try
            {
                singleView.MainView = new MobileMainView
                {
                    DataContext = Services.GetRequiredService<MainViewModel>()
                };
            }
            catch (Exception ex)
            {
                singleView.MainView = new ScrollViewer
                {
                    Content = new SelectableTextBlock
                    {
                        Margin = new Thickness(24, 48, 24, 24),
                        FontSize = 11,
                        Text = "Susurri failed to start.\n\n" + ex
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services, bool isMobile)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory);

        var embedded = Assembly.GetEntryAssembly()?.GetManifestResourceStream("appsettings.json");
        if (embedded != null)
            builder.AddJsonStream(embedded);

        var configuration = builder
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddIam();
        services.AddSingleton<AppSession>();
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<AppSession>(),
            autoSelectConversation: !isMobile));
    }
}
