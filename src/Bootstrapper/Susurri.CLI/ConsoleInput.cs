using System.Text;

namespace Susurri.CLI;

internal static class ConsoleInput
{
    /// <summary>
    /// Reads a line from the console without echoing characters. Backspace and
    /// printable input are honored. Returns an empty string when input is
    /// redirected (no interactive terminal).
    /// </summary>
    public static string ReadPassword()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Remove(password.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return password.ToString();
    }
}
