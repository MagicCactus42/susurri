using Microsoft.Extensions.DependencyInjection;
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

            Console.WriteLine();
            ConsoleUi.PrintHeader("=== Generated Passphrase ===");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  {passphrase}");
            Console.ResetColor();
            Console.WriteLine();
            ConsoleUi.PrintWarning("IMPORTANT: Write this down and store it securely offline!");
            ConsoleUi.PrintWarning("This passphrase is your identity. If you lose it, you lose access.");
            ConsoleUi.PrintWarning("Anyone with this passphrase can impersonate you.");
            Console.WriteLine();
            ConsoleUi.PrintInfo($"Word count: {wordCount} ({wordCount * 11 - wordCount / 3} bits of entropy)");
        }
        catch (ArgumentException ex)
        {
            ConsoleUi.PrintError(ex.Message);
            ConsoleUi.PrintInfo("Valid word counts: 12, 15, 18, 21, 24");
        }

        return Task.FromResult(true);
    }
}
