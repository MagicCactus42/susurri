namespace Susurri.CLI.Commands;

internal sealed class LogoutCommand : ICommand
{
    private readonly SessionState _session;

    public string Name => "logout";
    public string Description => "Logout current user";
    public string HelpLine => "  logout               - Logout current user";

    public LogoutCommand(SessionState session)
    {
        _session = session;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (!_session.IsLoggedIn)
        {
            ConsoleUi.PrintWarning("Not logged in.");
            return Task.FromResult(true);
        }

        _session.SetLoggedOut();
        ConsoleUi.PrintSuccess("Logged out.");
        return Task.FromResult(true);
    }
}
