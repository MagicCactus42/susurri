using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Formatting;

namespace Susurri.CLI.Logging;

internal sealed partial class RedactingTextFormatter : ITextFormatter
{
    private readonly ITextFormatter _inner;

    public RedactingTextFormatter(ITextFormatter inner)
    {
        _inner = inner;
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);

        var text = buffer.ToString();
        text = Ipv4().Replace(text, "[ip]");
        text = Ipv6Full().Replace(text, "[ip]");
        text = Ipv6Compressed().Replace(text, "[ip]");
        output.Write(text);
    }

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"\b(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\b")]
    private static partial Regex Ipv6Full();

    [GeneratedRegex(@"(?<![\w:])(?:[A-Fa-f0-9]{1,4})?::(?:[A-Fa-f0-9]{1,4}:)*[A-Fa-f0-9]{1,4}")]
    private static partial Regex Ipv6Compressed();
}
