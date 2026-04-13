namespace Susurri.CLI.Commands;

internal sealed class ExitCommand : ICommand
{
    public string Name => "exit";
    public IReadOnlyCollection<string> Aliases => new[] { "quit" };
    public string Description => "Exit the application";
    public string HelpLine => "  exit                 - Exit the application";

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ConsoleUi.PrintInfo("Goodbye!");
        return Task.FromResult(false);
    }
}
