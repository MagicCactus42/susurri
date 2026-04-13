namespace Susurri.CLI.Commands;

/// <summary>
/// A single CLI command. Implementations are expected to be small and self-contained;
/// shared state lives in <see cref="SessionState"/> and DI services.
/// </summary>
internal interface ICommand
{
    /// <summary>The primary command keyword (e.g. "login", "dht").</summary>
    string Name { get; }

    /// <summary>Optional aliases that route to this command.</summary>
    IReadOnlyCollection<string> Aliases => Array.Empty<string>();

    /// <summary>One-line help summary.</summary>
    string Description { get; }

    /// <summary>Help line shown by the help command.</summary>
    string HelpLine { get; }

    /// <summary>
    /// Executes the command. Return value indicates whether the interactive loop
    /// should continue (<c>true</c>) or exit (<c>false</c>).
    /// </summary>
    Task<bool> ExecuteAsync(string[] args, CancellationToken ct);
}
