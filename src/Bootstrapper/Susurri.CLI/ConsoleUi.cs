using System.Text;
using System.Text.RegularExpressions;
using Susurri.CLI.Tui;

namespace Susurri.CLI;

internal static class ConsoleUi
{
    public static readonly bool Fancy =
        !Console.IsOutputRedirected &&
        Environment.GetEnvironmentVariable("NO_COLOR") == null &&
        Environment.GetEnvironmentVariable("TERM") != "dumb" &&
        WindowsConsole.TryEnableVt();

    private static readonly Regex AnsiPattern = new(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    public static string Color(string text, int rgb) => Fancy ? $"{Ansi.Fg(rgb)}{text}\x1b[39m" : text;

    public static string Bold(string text) => Fancy ? $"\x1b[1m{text}\x1b[22m" : text;

    public static string Faint(string text) => Fancy ? $"\x1b[2m{text}\x1b[22m" : text;

    private static string Strip(string text) => AnsiPattern.Replace(text, "");

    public static void PrintBanner()
    {
        string[] art =
        {
            @"   ____                            _",
            @"  / ___| _   _ ___ _   _ _ __ _ __(_)",
            @"  \___ \| | | / __| | | | '__| '__| |",
            @"   ___) | |_| \__ \ |_| | |  | |  | |",
            @"  |____/ \__,_|___/\__,_|_|  |_|  |_|"
        };

        if (!Fancy)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            foreach (var line in art)
                Console.WriteLine(line);
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  Secure P2P Chat with DHT & Onion Routing");
            Console.WriteLine();
            return;
        }

        Console.WriteLine();
        var maxLen = art.Max(l => l.Length);
        var sb = new StringBuilder();
        foreach (var line in art)
        {
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ')
                {
                    sb.Append(' ');
                    continue;
                }
                var rgb = Palette.Lerp(Palette.Mauve, Palette.Teal, (double)i / maxLen);
                sb.Append(Ansi.Fg(rgb)).Append(line[i]);
            }
            sb.Append(Ansi.Reset).AppendLine();
        }
        Console.Write(sb);
        Console.WriteLine();
        Console.WriteLine(
            $"  {Color("●", Palette.Green)} {Faint("end-to-end encrypted")}   " +
            $"{Color("●", Palette.Accent)} {Faint("onion-routed")}   " +
            $"{Color("●", Palette.Mauve)} {Faint("serverless")}");
        Console.WriteLine();
    }

    public static string BuildPrompt(string? currentUser)
    {
        if (!Fancy)
            return currentUser != null ? $"  {currentUser} > " : "  susurri > ";

        return currentUser != null
            ? $"  {Color(Bold(currentUser), Palette.Accent)} {Color("❯", Palette.Green)} "
            : $"  {Faint("susurri")} {Color("❯", Palette.Dim)} ";
    }

    public static void PrintPrompt(string? currentUser)
    {
        if (!Fancy)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(currentUser != null ? $"  {currentUser}" : "  susurri");
            Console.ResetColor();
            Console.Write(" > ");
            return;
        }

        Console.Write(currentUser != null
            ? $"  {Color(Bold(currentUser), Palette.Accent)} {Color("❯", Palette.Green)} "
            : $"  {Faint("susurri")} {Color("❯", Palette.Dim)} ");
    }

    public static void PrintInfo(string message) => PrintGlyph("•", Palette.Accent, ConsoleColor.Blue, message);

    public static void PrintSuccess(string message) => PrintGlyph("✓", Palette.Green, ConsoleColor.Green, message);

    public static void PrintWarning(string message) => PrintGlyph("▲", Palette.Yellow, ConsoleColor.Yellow, message);

    public static void PrintError(string message) => PrintGlyph("✗", Palette.Red, ConsoleColor.Red, message);

    private static void PrintGlyph(string glyph, int rgb, ConsoleColor fallback, string message)
    {
        if (Fancy)
        {
            Console.WriteLine($"  {Color(Bold(glyph), rgb)} {message}");
            return;
        }

        var marker = glyph switch
        {
            "✓" => "+",
            "▲" => "!",
            "✗" => "-",
            _ => "*"
        };
        Console.ForegroundColor = fallback;
        Console.Write($"  [{marker}] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    public static void PrintHeader(string text)
    {
        if (!Fancy)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  {text}");
            Console.ResetColor();
            return;
        }

        var label = text.Trim('=', ' ');
        Console.WriteLine($"  {Color("──", Palette.BorderDim)} {Color(Bold(label), Palette.Accent)} {Color(new string('─', Math.Max(2, 40 - label.Length)), Palette.BorderDim)}");
    }

    public static void PrintIncoming(string sender, string content)
    {
        if (!Fancy)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write($"  «{sender}» ");
            Console.ResetColor();
            Console.WriteLine(content);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {Color("▌", Palette.SenderColor(sender))} {Color(Bold(sender), Palette.SenderColor(sender))} {content}");
    }

    public static void Box(string title, IReadOnlyList<string> lines, int borderRgb = Palette.BorderDim)
    {
        if (!Fancy)
        {
            PrintHeader(title);
            foreach (var line in lines)
                Console.WriteLine($"  {Strip(line)}");
            return;
        }

        var width = Math.Max(
            lines.Count == 0 ? 0 : lines.Max(l => TextMeasure.Measure(Strip(l))),
            Strip(title).Length + 2) + 2;

        var b = Ansi.Fg(borderRgb);
        var r = Ansi.Reset;

        Console.WriteLine($"  {b}╭─{r} {Bold(title)} {b}{new string('─', Math.Max(0, width - Strip(title).Length - 2))}╮{r}");
        foreach (var line in lines)
        {
            var pad = width - TextMeasure.Measure(Strip(line));
            Console.WriteLine($"  {b}│{r} {line}{new string(' ', Math.Max(0, pad))}{b}│{r}");
        }
        Console.WriteLine($"  {b}╰{new string('─', width + 1)}╯{r}");
    }

    public static void Panel(string title, IReadOnlyList<(string Key, string Value, int Rgb)> rows, int borderRgb = Palette.BorderDim)
    {
        if (rows.Count == 0)
            return;

        var keyWidth = rows.Max(row => row.Key.Length);
        var lines = rows
            .Select(row => $"{Faint(row.Key.PadRight(keyWidth))}  {Color(row.Value, row.Rgb)}")
            .ToList();
        Box(title, lines, borderRgb);
    }

    public static async Task<T> WithSpinnerAsync<T>(string label, Func<Task<T>> action)
    {
        if (!Fancy)
        {
            PrintInfo($"{label}...");
            return await action().ConfigureAwait(false);
        }

        const string frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        var task = action();
        var i = 0;

        Console.Write(Ansi.HideCursor);
        try
        {
            while (!task.IsCompleted)
            {
                Console.Write($"\r  {Color(frames[i++ % frames.Length].ToString(), Palette.Accent)} {Faint(label)}");
                await Task.WhenAny(task, Task.Delay(80)).ConfigureAwait(false);
            }
        }
        finally
        {
            Console.Write($"\r\x1b[2K{Ansi.ShowCursor}");
        }

        return await task.ConfigureAwait(false);
    }

    public static async Task WithSpinnerAsync(string label, Func<Task> action)
    {
        await WithSpinnerAsync(label, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }
}
