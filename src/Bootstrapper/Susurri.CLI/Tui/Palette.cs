namespace Susurri.CLI.Tui;

internal static class Palette
{
    public const int Accent = 0x89b4fa;
    public const int Green = 0xa6e3a1;
    public const int Red = 0xf38ba8;
    public const int Yellow = 0xf9e2af;
    public const int Peach = 0xfab387;
    public const int Teal = 0x94e2d5;
    public const int Mauve = 0xcba6f7;
    public const int Text = 0xcdd6f4;
    public const int Dim = 0x6c7086;
    public const int BorderDim = 0x45475a;
    public const int Selection = 0x313244;
    public const int StatusBg = 0x181825;

    private static readonly int[] Senders = { Red, Peach, Yellow, Green, Teal, Accent, Mauve };

    public static int SenderColor(string sender)
    {
        var hash = 0;
        foreach (var ch in sender)
            hash = hash * 31 + ch;
        return Senders[Math.Abs(hash) % Senders.Length];
    }

    public static int Lerp(int from, int to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        var r = (int)((from >> 16 & 0xFF) + (((to >> 16) & 0xFF) - ((from >> 16) & 0xFF)) * t);
        var g = (int)((from >> 8 & 0xFF) + (((to >> 8) & 0xFF) - ((from >> 8) & 0xFF)) * t);
        var b = (int)((from & 0xFF) + ((to & 0xFF) - (from & 0xFF)) * t);
        return (r << 16) | (g << 8) | b;
    }
}
