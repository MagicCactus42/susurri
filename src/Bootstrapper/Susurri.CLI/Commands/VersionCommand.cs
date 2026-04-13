using System.Reflection;

namespace Susurri.CLI.Commands;

internal sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Description => "Show version info";
    public string HelpLine => "  version              - Show version info";

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        Console.WriteLine($"  Susurri CLI v{version.Major}.{version.Minor}.{version.Build}");
        Console.WriteLine($"  .NET Runtime: {Environment.Version}");
        Console.WriteLine($"  Platform: {Environment.OSVersion}");
        return Task.FromResult(true);
    }
}
