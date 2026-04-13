namespace Susurri.CLI.Commands;

internal sealed class ClearScreenCommand : ICommand
{
    public string Name => "clear";
    public string Description => "Clear screen";
    public string HelpLine => "  clear                - Clear screen";

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        Console.Clear();
        ConsoleUi.PrintBanner();
        return Task.FromResult(true);
    }
}
