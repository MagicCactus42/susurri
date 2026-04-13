using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Susurri.CLI.Logging;
using Susurri.Shared.Abstractions.Diagnostics;
using Susurri.Shared.Abstractions.Modules;
using Susurri.Shared.Infrastructure.Diagnostics;

namespace Susurri.CLI;

/// <summary>
/// Builds the DI container, loads modules from sibling assemblies, and configures
/// logging. Replaces the static initialization that used to live in Program.cs.
/// </summary>
internal static class CliHost
{
    public static IServiceProvider Build(bool bootstrapMode, ILoggerFactory? earlyLogger = null)
    {
        var services = new ServiceCollection();

        // Configuration precedence (later sources override earlier):
        //   1. appsettings.json (defaults checked into the repo, no secrets)
        //   2. appsettings.bootstrap.json (bootstrap-mode overrides)
        //   3. dotnet user-secrets (developer secrets, never committed —
        //      see <UserSecretsId> in Susurri.CLI.csproj)
        //   4. Environment variables (production secret/config injection,
        //      e.g. ConnectionStrings__UsersDb)
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);

        if (bootstrapMode)
            configBuilder.AddJsonFile("appsettings.bootstrap.json", optional: true);

        configBuilder
            .AddUserSecrets(typeof(CliHost).Assembly, optional: true)
            .AddEnvironmentVariables();

        var configuration = configBuilder.Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.SetMinimumLevel(LogLevel.Warning);

            // Serilog is the only sink: in Production we write Compact JSON
            // (one event per line, machine-parseable for log shipping); in
            // Development we write a human-friendly formatted line. Activity
            // enricher stamps every event with TraceId/SpanId so all log lines
            // emitted while handling a single inbound request share a key.
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                           ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                           ?? "Development";
            var isProduction = string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

            var serilog = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .Enrich.With(new ActivityEnricher())
                .Enrich.WithProperty("Application", "susurri")
                .Enrich.WithProperty("Environment", environment)
                .MinimumLevel.Is(LogEventLevel.Verbose);

            if (isProduction)
            {
                serilog.WriteTo.Console(new CompactJsonFormatter(), standardErrorFromLevel: LogEventLevel.Error);
            }
            else
            {
                const string outputTemplate =
                    "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext} {TraceId}: {Message:lj}{NewLine}{Exception}";
                serilog.WriteTo.Console(outputTemplate: outputTemplate, standardErrorFromLevel: LogEventLevel.Error);
            }

            builder.AddSerilog(serilog.CreateLogger(), dispose: true);
        });

        // OpenTelemetry metrics. The Susurri meter is always live (so unit
        // tests can attach a MeterListener and observe values without an SDK),
        // but the OTLP exporter is opt-in: only configured when
        // Metrics:OtlpEndpoint is set. This keeps the default startup quiet —
        // no outbound network traffic at boot, matching the privacy posture.
        var otlpEndpoint = configuration["Metrics:OtlpEndpoint"];
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName: "susurri", autoGenerateServiceInstanceId: false))
            .WithMetrics(b =>
            {
                b.AddMeter(SusurriMetrics.MeterName);
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    b.AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
                }
            });

        var crashSection = configuration.GetSection("CrashReporting");
        var crashDirectory = crashSection["Directory"];
        var crashEndpoint = crashSection["Endpoint"];

        services.AddSingleton(_ => new CrashDumpWriter(crashDirectory));
        services.AddSingleton<FatalErrorHandler>(sp => new FatalErrorHandler(
            sp.GetRequiredService<CrashDumpWriter>(),
            string.IsNullOrWhiteSpace(crashEndpoint) ? null : new HttpCrashReporter(new Uri(crashEndpoint)),
            sp.GetRequiredService<ILogger<FatalErrorHandler>>()));
        services.AddSingleton<IFatalErrorHandler>(sp => sp.GetRequiredService<FatalErrorHandler>());

        var assemblies = LoadModuleAssemblies(earlyLogger);
        RegisterModules(services, assemblies, earlyLogger);

        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<FatalErrorHandler>().Install();

        var modules = provider.GetServices<IModule>();
        foreach (var module in modules)
            module.Initialize(provider);

        return provider;
    }

    private static IList<Assembly> LoadModuleAssemblies(ILoggerFactory? earlyLogger)
    {
        var logger = earlyLogger?.CreateLogger("CliHost");
        var assemblies = new List<Assembly>();
        var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        foreach (var file in Directory.GetFiles(location, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                assemblies.Add(assembly);
            }
            catch (Exception ex) when (
                ex is BadImageFormatException
                || ex is FileLoadException
                || ex is FileNotFoundException)
            {
                logger?.LogDebug("Skipping non-managed or unloadable assembly {File}: {Error}",
                    Path.GetFileName(file), ex.Message);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Unexpected error loading assembly {File}", Path.GetFileName(file));
            }
        }

        return assemblies;
    }

    private static void RegisterModules(IServiceCollection services, IList<Assembly> assemblies, ILoggerFactory? earlyLogger)
    {
        var logger = earlyLogger?.CreateLogger("CliHost");
        var moduleType = typeof(IModule);

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => moduleType.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var type in types)
                {
                    var instance = (IModule)Activator.CreateInstance(type)!;
                    instance.Register(services);
                    services.AddSingleton(instance);
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                logger?.LogWarning(
                    "Skipping assembly {Assembly}: {Loader} type-load failures",
                    assembly.GetName().Name, ex.LoaderExceptions.Length);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to register modules from {Assembly}",
                    assembly.GetName().Name);
            }
        }
    }
}
