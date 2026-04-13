namespace Susurri.CLI.Commands;

/// <summary>
/// Maps a command keyword to its <see cref="ICommand"/> implementation and dispatches
/// invocations. Held by <see cref="CliApplication"/>; one instance per CLI process.
/// </summary>
internal sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ICommand> _all = new();

    public IReadOnlyList<ICommand> All => _all;

    public void Register(ICommand command)
    {
        _byName[command.Name] = command;
        foreach (var alias in command.Aliases)
            _byName[alias] = command;
        _all.Add(command);
    }

    public bool TryGet(string name, out ICommand? command)
    {
        return _byName.TryGetValue(name, out command);
    }

    /// <summary>
    /// Parses a single input line and dispatches. Returns <c>false</c> if the
    /// command requested loop exit; <c>true</c> otherwise.
    /// </summary>
    public async Task<bool> DispatchAsync(string input, CancellationToken ct)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        var name = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        if (!_byName.TryGetValue(name, out var command) || command == null)
        {
            ConsoleUi.PrintWarning($"Unknown command: {name}");
            ConsoleUi.PrintInfo("Type 'help' for available commands.");
            return true;
        }

        return await command.ExecuteAsync(args, ct);
    }
}
