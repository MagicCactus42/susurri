using System.Runtime.InteropServices;
using System.Text;

namespace Susurri.CLI.Tui;

internal static class WindowsConsole
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint DisableNewlineAutoReturn = 0x0008;

    private static readonly Lazy<bool> VtEnabled = new(EnableVt);

    public static bool TryEnableVt() => VtEnabled.Value;

    private static bool EnableVt()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        if (!TryEnableOutputMode())
            return false;

        try
        {
            if (Console.OutputEncoding.CodePage != Encoding.UTF8.CodePage)
                Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
        }

        return true;
    }

    private static bool TryEnableOutputMode()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return false;

            if (!GetConsoleMode(handle, out var mode))
                return false;

            if ((mode & EnableVirtualTerminalProcessing) != 0)
                return true;

            if (SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn))
                return true;

            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
