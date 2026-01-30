using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Susurri.Modules.DHT.Core.Node;
using Susurri.Modules.IAM.Application.Commands;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Crypto;
using Susurri.Shared.Abstractions.Commands;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.CLI;

public static class Program
{
    private static IServiceProvider _serviceProvider = null!;
    private static NodeServer? _dhtNode;
    private static CancellationTokenSource? _dhtCts;
    private static bool _isLoggedIn;
    private static string? _currentUser;
    private static ILogger<object>? _logger;

    public static async Task Main(string[] args)
    {
        Console.Clear();
        PrintBanner();

        try
        {
            // Check for --bootstrap mode
            var isBootstrapMode = args.Any(a =>
                a.Equals("--bootstrap", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-b", StringComparison.OrdinalIgnoreCase));

            var bootstrapPort = 7070;
            var portArgIndex = Array.FindIndex(args, a =>
                a.Equals("--port", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-p", StringComparison.OrdinalIgnoreCase));
            if (portArgIndex >= 0 && portArgIndex + 1 < args.Length)
            {
                int.TryParse(args[portArgIndex + 1], out bootstrapPort);
            }

            await InitializeServicesAsync(isBootstrapMode);
            Console.WriteLine();
            PrintSuccess("Services initialized successfully.");
            Console.WriteLine();

            if (isBootstrapMode)
            {
                await RunBootstrapModeAsync(bootstrapPort);
            }
            else if (args.Length > 0 && !args[0].StartsWith("-"))
            {
                await ExecuteCommandAsync(string.Join(" ", args));
            }
            else
            {
                await RunInteractiveAsync();
            }
        }
        catch (Exception ex)
        {
            PrintError($"Fatal error: {ex.Message}");
            Environment.Exit(1);
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private static async Task RunBootstrapModeAsync(int port)
    {
        PrintInfo("Starting in BOOTSTRAP NODE mode...");
        PrintInfo("This node will serve as a DHT bootstrap for the Susurri network.");
        PrintInfo("No identity/login required. Node operates as DHT + relay only.");
        Console.WriteLine();

        await StartDhtNodeAsync(new[] { port.ToString() });

        if (_dhtNode == null)
        {
            PrintError("Failed to start bootstrap node.");
            return;
        }

        PrintSuccess($"Bootstrap node running on port {port}");
        PrintInfo("Press Ctrl+C to stop.");
        Console.WriteLine();

        // Wait for shutdown signal
        var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdownCts.Cancel();
            PrintInfo("Shutdown signal received...");
        };

        try
        {
            await Task.Delay(Timeout.Infinite, shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        PrintInfo("Bootstrap node stopped.");
    }

    private static async Task InitializeServicesAsync(bool bootstrapMode = false)
    {
        PrintInfo(bootstrapMode ? "Initializing Susurri Bootstrap Node..." : "Initializing Susurri...");

        var services = new ServiceCollection();

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);

        if (bootstrapMode)
        {
            configBuilder.AddJsonFile("appsettings.bootstrap.json", optional: true);
        }

        var configuration = configBuilder.Build();

        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Error;
            });
        });

        var assemblies = LoadModuleAssemblies();
        RegisterModules(services, assemblies);

        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<object>();

        var modules = _serviceProvider.GetServices<IModule>();
        foreach (var module in modules)
        {
            module.Initialize(_serviceProvider);
        }
    }

    private static IList<Assembly> LoadModuleAssemblies()
    {
        var assemblies = new List<Assembly>();
        var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        foreach (var file in Directory.GetFiles(location, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                assemblies.Add(assembly);
            }
            catch
            {
                // Skip assemblies that can't be loaded
            }
        }

        return assemblies;
    }

    private static void RegisterModules(IServiceCollection services, IList<Assembly> assemblies)
    {
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
            catch
            {
                // Skip assemblies that fail type resolution
            }
        }
    }

    private static async Task RunInteractiveAsync()
    {
        PrintHelp();
        Console.WriteLine();

        while (true)
        {
            PrintPrompt();
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                continue;

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                PrintInfo("Goodbye!");
                break;
            }

            try
            {
                await ExecuteCommandAsync(input);
            }
            catch (Exception ex)
            {
                PrintError($"Command failed: {ex.Message}");
            }

            Console.WriteLine();
        }
    }

    private static async Task ExecuteCommandAsync(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var command = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        switch (command)
        {
            case "help":
            case "?":
                PrintHelp();
                break;

            case "login":
                await HandleLoginAsync(args);
                break;

            case "logout":
                HandleLogout();
                break;

            case "status":
                PrintStatus();
                break;

            case "dht":
                await HandleDhtCommandAsync(args);
                break;

            case "ping":
                await HandlePingAsync(args);
                break;

            case "clear":
                Console.Clear();
                PrintBanner();
                break;

            case "version":
                PrintVersion();
                break;

            case "generate":
                HandleGeneratePassphrase(args);
                break;

            case "clearcache":
                HandleClearCache();
                break;

            case "group":
                HandleGroupCommand(args);
                break;

            default:
                PrintWarning($"Unknown command: {command}");
                PrintInfo("Type 'help' for available commands.");
                break;
        }
    }

    private static async Task HandleLoginAsync(string[] args)
    {
        if (_isLoggedIn)
        {
            PrintWarning($"Already logged in as '{_currentUser}'. Use 'logout' first.");
            return;
        }

        string? username = null;
        string? passphrase = null;
        var useCachedCredentials = false;

        var credentialsCache = _serviceProvider.GetService<ICredentialsCache>();

        if (credentialsCache?.Exists() == true)
        {
            Console.WriteLine();
            PrintInfo("Cached credentials found.");
            Console.Write("  Enter cache password to use cached credentials (or press Enter to skip): ");
            var cachePassword = ReadPassword();

            if (!string.IsNullOrEmpty(cachePassword))
            {
                try
                {
                    var cached = credentialsCache.Load(cachePassword);
                    username = cached.Username;
                    passphrase = cached.Passphrase;
                    useCachedCredentials = true;
                    PrintSuccess("Loaded credentials from cache.");
                }
                catch (Exception ex)
                {
                    PrintWarning($"Could not load cached credentials: {ex.Message}");
                    PrintInfo("Proceeding with manual login...");
                }
            }
        }

        if (!useCachedCredentials)
        {
            if (args.Length >= 1)
            {
                username = args[0];
            }
            else
            {
                Console.Write("  Username: ");
                username = Console.ReadLine()?.Trim();
            }

            if (string.IsNullOrEmpty(username))
            {
                PrintError("Username cannot be empty.");
                return;
            }

            Console.Write("  Passphrase (6+ word BIP39 mnemonic, use 'generate' command to create one): ");
            passphrase = ReadPassword();

            if (string.IsNullOrEmpty(passphrase))
            {
                PrintError("Passphrase cannot be empty.");
                return;
            }
        }

        PrintInfo("Authenticating...");

        try
        {
            var commandDispatcher = _serviceProvider.GetRequiredService<ICommandDispatcher>();

            bool cacheCredentials = false;
            string? cachePassword = null;

            if (!useCachedCredentials && credentialsCache != null)
            {
                Console.WriteLine();
                Console.Write("  Save credentials locally for future logins? [y/N]: ");
                var saveResponse = Console.ReadLine()?.Trim().ToLowerInvariant();

                if (saveResponse == "y" || saveResponse == "yes")
                {
                    Console.Write("  Enter a password to protect cached credentials (8+ chars): ");
                    cachePassword = ReadPassword();

                    if (!string.IsNullOrEmpty(cachePassword) && cachePassword.Length >= 8)
                    {
                        cacheCredentials = true;
                    }
                    else
                    {
                        PrintWarning("Password too short. Credentials will not be cached.");
                    }
                }
            }

            await commandDispatcher.SendAsync(new Login(username!, passphrase!, cacheCredentials, cachePassword));

            _isLoggedIn = true;
            _currentUser = username;

            PrintSuccess($"Logged in as '{username}'.");
            PrintInfo("Your identity keys have been derived from your passphrase.");

            if (cacheCredentials)
            {
                PrintSuccess("Credentials saved locally (encrypted).");
            }
        }
        catch (Exception ex)
        {
            PrintError($"Login failed: {ex.Message}");
        }
    }

    private static void HandleLogout()
    {
        if (!_isLoggedIn)
        {
            PrintWarning("Not logged in.");
            return;
        }

        _isLoggedIn = false;
        _currentUser = null;
        PrintSuccess("Logged out.");
    }

    private static void HandleClearCache()
    {
        var credentialsCache = _serviceProvider.GetService<ICredentialsCache>();

        if (credentialsCache == null)
        {
            PrintError("Credentials cache not available.");
            return;
        }

        if (!credentialsCache.Exists())
        {
            PrintInfo("No cached credentials found.");
            return;
        }

        Console.Write("  Are you sure you want to delete cached credentials? [y/N]: ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (response == "y" || response == "yes")
        {
            credentialsCache.Clear();
            PrintSuccess("Cached credentials deleted.");
        }
        else
        {
            PrintInfo("Operation cancelled.");
        }
    }

    private static void HandleGroupCommand(string[] args)
    {
        if (!_isLoggedIn)
        {
            PrintWarning("You must be logged in to manage groups.");
            return;
        }

        if (args.Length == 0)
        {
            PrintGroupHelp();
            return;
        }

        var subcommand = args[0].ToLowerInvariant();

        switch (subcommand)
        {
            case "create":
                if (args.Length < 2)
                {
                    PrintError("Usage: group create <name>");
                    return;
                }
                PrintInfo($"Creating group '{args[1]}'...");
                PrintSuccess($"Group created. Group ID would be generated here.");
                PrintInfo("(Group functionality requires full integration with GroupManager)");
                break;

            case "list":
                PrintInfo("Your groups:");
                PrintInfo("  (No groups yet - functionality requires GroupManager integration)");
                break;

            case "invite":
                if (args.Length < 3)
                {
                    PrintError("Usage: group invite <group-id> <user-public-key>");
                    return;
                }
                PrintInfo($"Generating invite for group {args[1]}...");
                PrintInfo("(Invite functionality requires GroupManager integration)");
                break;

            case "join":
                if (args.Length < 2)
                {
                    PrintError("Usage: group join <invite-code>");
                    return;
                }
                PrintInfo("Joining group...");
                PrintInfo("(Join functionality requires GroupManager integration)");
                break;

            case "leave":
                if (args.Length < 2)
                {
                    PrintError("Usage: group leave <group-id>");
                    return;
                }
                PrintInfo($"Leaving group {args[1]}...");
                PrintInfo("(Leave functionality requires GroupManager integration)");
                break;

            case "help":
                PrintGroupHelp();
                break;

            default:
                PrintWarning($"Unknown group command: {subcommand}");
                PrintGroupHelp();
                break;
        }
    }

    private static void PrintGroupHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Group Commands:");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  group create <name>          - Create a new group");
        Console.WriteLine("  group list                   - List your groups");
        Console.WriteLine("  group invite <id> <pubkey>   - Generate invite for a user");
        Console.WriteLine("  group join <invite-code>     - Join a group using invite code");
        Console.WriteLine("  group leave <id>             - Leave a group");
        Console.WriteLine("  group help                   - Show this help");
    }

    private static void HandleGeneratePassphrase(string[] args)
    {
        var wordCount = 12;
        if (args.Length > 0 && int.TryParse(args[0], out var customCount))
        {
            wordCount = customCount;
        }

        try
        {
            var keyGenerator = _serviceProvider.GetService<ICryptoKeyGenerator>();
            if (keyGenerator == null)
            {
                PrintError("Key generator not available.");
                return;
            }

            var passphrase = keyGenerator.GeneratePassphrase(wordCount);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  === Generated Passphrase ===");
            Console.ResetColor();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  {passphrase}");
            Console.ResetColor();
            Console.WriteLine();
            PrintWarning("IMPORTANT: Write this down and store it securely offline!");
            PrintWarning("This passphrase is your identity. If you lose it, you lose access.");
            PrintWarning("Anyone with this passphrase can impersonate you.");
            Console.WriteLine();
            PrintInfo($"Word count: {wordCount} ({wordCount * 11 - wordCount / 3} bits of entropy)");
        }
        catch (ArgumentException ex)
        {
            PrintError(ex.Message);
            PrintInfo("Valid word counts: 12, 15, 18, 21, 24");
        }
    }

    private static async Task HandleDhtCommandAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintDhtHelp();
            return;
        }

        var subcommand = args[0].ToLowerInvariant();

        switch (subcommand)
        {
            case "start":
            case "deploy":
                await StartDhtNodeAsync(args.Skip(1).ToArray());
                break;

            case "stop":
                StopDhtNode();
                break;

            case "status":
                PrintDhtStatus();
                break;

            case "help":
                PrintDhtHelp();
                break;

            default:
                PrintWarning($"Unknown DHT command: {subcommand}");
                PrintDhtHelp();
                break;
        }
    }

    private static async Task StartDhtNodeAsync(string[] args)
    {
        if (_dhtNode != null)
        {
            PrintWarning("DHT node is already running.");
            return;
        }

        var port = 7070;
        if (args.Length > 0 && int.TryParse(args[0], out var customPort))
        {
            port = customPort;
        }

        PrintInfo($"Starting DHT node on port {port}...");

        try
        {
            var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<NodeServer>();

            _dhtNode = new NodeServer(port, logger);
            _dhtCts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                try
                {
                    await _dhtNode.StartAsync();
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    PrintError($"DHT node error: {ex.Message}");
                }
            });

            await Task.Delay(500);

            PrintSuccess($"DHT node started.");
            PrintInfo($"  Node ID: {_dhtNode.NodeId}");
            PrintInfo($"  Port:    {port}");
        }
        catch (Exception ex)
        {
            PrintError($"Failed to start DHT node: {ex.Message}");
            _dhtNode = null;
            _dhtCts = null;
        }
    }

    private static void StopDhtNode()
    {
        if (_dhtNode == null)
        {
            PrintWarning("DHT node is not running.");
            return;
        }

        PrintInfo("Stopping DHT node...");
        _dhtNode.Stop();
        _dhtCts?.Cancel();
        _dhtNode = null;
        _dhtCts = null;
        PrintSuccess("DHT node stopped.");
    }

    private static void PrintDhtStatus()
    {
        if (_dhtNode == null)
        {
            PrintInfo("DHT node: " + Colorize("STOPPED", ConsoleColor.Yellow));
        }
        else
        {
            PrintInfo("DHT node: " + Colorize("RUNNING", ConsoleColor.Green));
            PrintInfo($"  Node ID: {_dhtNode.NodeId}");
        }
    }

    private static async Task HandlePingAsync(string[] args)
    {
        if (args.Length < 2)
        {
            PrintInfo("Usage: ping <host> <port>");
            return;
        }

        if (!int.TryParse(args[1], out var port))
        {
            PrintError("Invalid port number.");
            return;
        }

        var host = args[0];
        PrintInfo($"Pinging {host}:{port}...");

        try
        {
            var nodeClient = _serviceProvider.GetService<Susurri.Modules.DHT.Core.Abstractions.INodeClient>();
            if (nodeClient == null)
            {
                PrintError("Node client not available.");
                return;
            }

            var response = await nodeClient.SendMessage(host, port, "PING");
            if (!string.IsNullOrEmpty(response))
            {
                PrintSuccess($"Response: {response}");
            }
            else
            {
                PrintWarning("No response received.");
            }
        }
        catch (Exception ex)
        {
            PrintError($"Ping failed: {ex.Message}");
        }
    }

    private static void PrintStatus()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  === Susurri Status ===");
        Console.ResetColor();
        Console.WriteLine();

        if (_isLoggedIn)
        {
            PrintInfo("User:     " + Colorize(_currentUser!, ConsoleColor.Green) + " (logged in)");
        }
        else
        {
            PrintInfo("User:     " + Colorize("Not logged in", ConsoleColor.Yellow));
        }

        PrintDhtStatus();
    }

    private static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Available Commands:");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  generate [words]     - Generate a new BIP39 passphrase (default: 12 words)");
        Console.WriteLine("  login [username]     - Login with username and passphrase (6+ words)");
        Console.WriteLine("  logout               - Logout current user");
        Console.WriteLine("  clearcache           - Delete locally cached credentials");
        Console.WriteLine("  group <command>      - Group chat management (see 'group help')");
        Console.WriteLine("  status               - Show current status");
        Console.WriteLine("  dht <command>        - DHT node management (see 'dht help')");
        Console.WriteLine("  ping <host> <port>   - Ping a DHT node");
        Console.WriteLine("  clear                - Clear screen");
        Console.WriteLine("  version              - Show version info");
        Console.WriteLine("  help                 - Show this help");
        Console.WriteLine("  exit                 - Exit the application");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Bootstrap Mode:");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  --bootstrap, -b      - Start as headless bootstrap node (DHT + relay only)");
        Console.WriteLine("  --port, -p <port>    - Set listening port (default: 7070)");
        Console.WriteLine();
        Console.WriteLine("  Example: susurri --bootstrap --port 7070");
    }

    private static void PrintDhtHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  DHT Commands:");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  dht start [port]     - Start DHT node (default port: 7070)");
        Console.WriteLine("  dht deploy [port]    - Alias for 'dht start'");
        Console.WriteLine("  dht stop             - Stop DHT node");
        Console.WriteLine("  dht status           - Show DHT node status");
        Console.WriteLine("  dht help             - Show this help");
    }

    private static void PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        Console.WriteLine($"  Susurri CLI v{version.Major}.{version.Minor}.{version.Build}");
        Console.WriteLine($"  .NET Runtime: {Environment.Version}");
        Console.WriteLine($"  Platform: {Environment.OSVersion}");
    }

    private static async Task ShutdownAsync()
    {
        if (_dhtNode != null)
        {
            PrintInfo("Shutting down DHT node...");
            StopDhtNode();
        }

        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
   ____                            _
  / ___| _   _ ___ _   _ _ __ _ __(_)
  \___ \| | | / __| | | | '__| '__| |
   ___) | |_| \__ \ |_| | |  | |  | |
  |____/ \__,_|___/\__,_|_|  |_|  |_|
");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  Secure P2P Chat with DHT & Onion Routing");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintPrompt()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        if (_isLoggedIn)
        {
            Console.Write($"  {_currentUser}");
        }
        else
        {
            Console.Write("  susurri");
        }
        Console.ResetColor();
        Console.Write(" > ");
    }

    private static void PrintInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write("  [*] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [+] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void PrintWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [!] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [-] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private static string Colorize(string text, ConsoleColor color)
    {
        return text;
    }

    private static string ReadPassword()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Remove(password.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return password.ToString();
    }
}
