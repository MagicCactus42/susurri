using Microsoft.Extensions.DependencyInjection;
using Susurri.CLI.Tui;
using Susurri.Modules.IAM.Core.Crypto;

namespace Susurri.CLI.Commands;

internal sealed class GenerateCommand : ICommand
{
    private readonly IServiceProvider _services;

    public string Name => "generate";
    public string Description => "Generate a new BIP39 passphrase";
    public string HelpLine => "  generate [words]     - Generate a new BIP39 passphrase (default: 12 words)";

    public GenerateCommand(IServiceProvider services)
    {
        _services = services;
    }

    public Task<bool> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var wordCount = 12;
        if (args.Length > 0 && int.TryParse(args[0], out var customCount))
            wordCount = customCount;

        try
        {
            var keyGenerator = _services.GetService<ICryptoKeyGenerator>();
            if (keyGenerator == null)
            {
                ConsoleUi.PrintError("Key generator not available.");
                return Task.FromResult(true);
            }

            var passphrase = keyGenerator.GeneratePassphrase(wordCount);
            var words = passphrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var lines = new List<string>();
            for (var i = 0; i < words.Length; i += 4)
            {
                var row = words.Skip(i).Take(4)
                    .Select((w, j) => $"{ConsoleUi.Faint($"{i + j + 1,2}.")} {ConsoleUi.Color(w.PadRight(10), Palette.Yellow)}");
                lines.Add(string.Join("  ", row));
            }
            lines.Add("");
            lines.Add(ConsoleUi.Faint($"{wordCount} words · {wordCount * 11 - wordCount / 3} bits of entropy"));

            Console.WriteLine();
            ConsoleUi.Box("your identity", lines, Palette.Yellow);
            Console.WriteLine();
            ConsoleUi.PrintWarning("Write this down and store it securely offline!");
            ConsoleUi.PrintWarning("This passphrase is your identity — lose it and access is gone.");
            ConsoleUi.PrintWarning("Anyone holding it can impersonate you.");
        }
        catch (ArgumentException ex)
        {
            ConsoleUi.PrintError(ex.Message);
            ConsoleUi.PrintInfo("Valid word counts: 12, 15, 18, 21, 24");
        }

        return Task.FromResult(true);
    }
}
