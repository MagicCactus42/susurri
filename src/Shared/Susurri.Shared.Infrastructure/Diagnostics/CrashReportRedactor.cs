using System.Text.RegularExpressions;
using Susurri.Shared.Abstractions.Logging;

namespace Susurri.Shared.Infrastructure.Diagnostics;

public static partial class CrashReportRedactor
{
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        var s = input;
        s = KeyHexRegex().Replace(s, m =>
        {
            var bytes = Convert.FromHexString(m.Value);
            return $"<key:{LogRedaction.KeyFingerprint(bytes)}>";
        });
        s = RedactHomePath(s);
        return s;
    }

    private static string RedactHomePath(string s)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return s;
        return s.Replace(home, "~", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\b[0-9A-Fa-f]{64}\b", RegexOptions.Compiled)]
    private static partial Regex KeyHexRegex();
}
