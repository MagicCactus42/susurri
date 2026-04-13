namespace Susurri.CLI;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.Clear();
        ConsoleUi.PrintBanner();

        var bootstrapMode = args.Any(a =>
            a.Equals("--bootstrap", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-b", StringComparison.OrdinalIgnoreCase));

        var bootstrapPort = 7070;
        var portArgIndex = Array.FindIndex(args, a =>
            a.Equals("--port", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-p", StringComparison.OrdinalIgnoreCase));
        if (portArgIndex >= 0 && portArgIndex + 1 < args.Length)
            int.TryParse(args[portArgIndex + 1], out bootstrapPort);

        await using var app = CliApplication.Create(bootstrapMode);

        Console.WriteLine();
        ConsoleUi.PrintSuccess("Services initialized successfully.");
        Console.WriteLine();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
            ConsoleUi.PrintInfo("Shutdown signal received...");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            shutdown.Cancel();
        };

        try
        {
            if (bootstrapMode)
            {
                await app.RunBootstrapAsync(bootstrapPort, shutdown.Token);
            }
            else if (args.Length > 0 && !args[0].StartsWith('-'))
            {
                await app.RunSingleCommandAsync(string.Join(' ', args), shutdown.Token);
            }
            else
            {
                await app.RunInteractiveAsync(shutdown.Token);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleUi.PrintError($"Fatal error: {ex.Message}");
            return 1;
        }
    }
}
