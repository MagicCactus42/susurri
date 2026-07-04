using System.Reflection;
using Susurri.CLI.Tui;

namespace Susurri.CLI.Commands;

internal sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Description => "Show version info";
    public string HelpLine => "  version              - Show version info";

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        ConsoleUi.Panel("susurri", new[]
        {
            ("version", $"v{version.Major}.{version.Minor}.{version.Build}", Palette.Accent),
            ("runtime", $".NET {Environment.Version}", Palette.Text),
            ("platform", Environment.OSVersion.ToString(), Palette.Text)
        });
        return Task.FromResult(true);
    }
}
