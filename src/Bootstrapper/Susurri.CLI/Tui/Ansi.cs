namespace Susurri.CLI.Tui;

internal static class Ansi
{
    public const string AltScreenOn = "\x1b[?1049h";
    public const string AltScreenOff = "\x1b[?1049l";
    public const string HideCursor = "\x1b[?25l";
    public const string ShowCursor = "\x1b[?25h";
    public const string MouseOn = "\x1b[?1002h\x1b[?1006h";
    public const string MouseOff = "\x1b[?1006l\x1b[?1002l";
    public const string SyncStart = "\x1b[?2026h";
    public const string SyncEnd = "\x1b[?2026l";
    public const string Reset = "\x1b[0m";
    public const string ClearScreen = "\x1b[2J";

    public static string MoveTo(int row, int col) => $"\x1b[{row + 1};{col + 1}H";

    public static string Fg(int rgb) => rgb < 0
        ? "\x1b[39m"
        : $"\x1b[38;2;{(rgb >> 16) & 0xFF};{(rgb >> 8) & 0xFF};{rgb & 0xFF}m";

    public static string Bg(int rgb) => rgb < 0
        ? "\x1b[49m"
        : $"\x1b[48;2;{(rgb >> 16) & 0xFF};{(rgb >> 8) & 0xFF};{rgb & 0xFF}m";
}
