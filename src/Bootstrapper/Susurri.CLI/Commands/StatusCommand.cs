namespace Susurri.CLI.Commands;

internal sealed class StatusCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "status";
    public string Description => "Show current status";
    public string HelpLine => "  status               - Show current status";

    public StatusCommand(SessionState session)
    {
        _session = session;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        Console.WriteLine();
        ConsoleUi.PrintHeader("=== Susurri Status ===");
        Console.WriteLine();

        if (_session.IsLoggedIn)
            ConsoleUi.PrintInfo($"User:     {_session.CurrentUser} (logged in)");
        else
            ConsoleUi.PrintInfo("User:     Not logged in");

        if (_session.DhtNode == null)
        {
            ConsoleUi.PrintInfo("DHT node: STOPPED");
        }
        else
        {
            ConsoleUi.PrintInfo("DHT node: RUNNING");
            ConsoleUi.PrintInfo($"  Node ID: {_session.DhtNode.NodeId}");
        }

        return Task.FromResult(true);
    }
}
