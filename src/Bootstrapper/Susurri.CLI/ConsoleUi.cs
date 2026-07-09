namespace Susurri.CLI;

/// <summary>
/// Console formatting helpers. Centralized here so all CLI output is consistent.
/// </summary>
internal static class ConsoleUi
{
    public static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
   ____                            _
  / ___| _   _ ___ _   _ _ __ _ __(_)
  \___ \| | | / __| | | | '__| '__| |
   ___) | |_| \__ \ |_| | |  | |  | |
  |____/ \__,_|___/\__,_|_|  |_|  |_|
");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  Secure P2P Chat with DHT & Onion Routing");
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void PrintPrompt(string? currentUser)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write(currentUser != null ? $"  {currentUser}" : "  susurri");
        Console.ResetColor();
        Console.Write(" > ");
    }

    public static void PrintInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write("  [*] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [+] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void PrintWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [!] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  [-] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void PrintHeader(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  {text}");
        Console.ResetColor();
    }

    public static void PrintIncoming(string sender, string content)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.Write($"  «{sender}» ");
        Console.ResetColor();
        Console.WriteLine(content);
    }
}
