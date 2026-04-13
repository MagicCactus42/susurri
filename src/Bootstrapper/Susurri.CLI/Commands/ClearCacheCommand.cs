using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.IAM.Core.Abstractions;

namespace Susurri.CLI.Commands;

internal sealed class ClearCacheCommand : ICommand
{
    private readonly IServiceProvider _services;

    public string Name => "clearcache";
    public string Description => "Delete locally cached credentials";
    public string HelpLine => "  clearcache           - Delete locally cached credentials";

    public ClearCacheCommand(IServiceProvider services)
    {
        _services = services;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var credentialsCache = _services.GetService<ICredentialsCache>();
        if (credentialsCache == null)
        {
            ConsoleUi.PrintError("Credentials cache not available.");
            return Task.FromResult(true);
        }

        if (!credentialsCache.Exists())
        {
            ConsoleUi.PrintInfo("No cached credentials found.");
            return Task.FromResult(true);
        }

        Console.Write("  Are you sure you want to delete cached credentials? [y/N]: ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (response == "y" || response == "yes")
        {
            credentialsCache.Clear();
            ConsoleUi.PrintSuccess("Cached credentials deleted.");
        }
        else
        {
            ConsoleUi.PrintInfo("Operation cancelled.");
        }

        return Task.FromResult(true);
    }
}
