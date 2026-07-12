using System.Text;
using System.Threading.Channels;

namespace Susurri.CLI.Tui;

internal abstract record TuiEvent;

internal sealed record KeyEvent(ConsoleKeyInfo Key) : TuiEvent;

internal sealed record MouseClickEvent(int X, int Y) : TuiEvent;

internal sealed record MouseWheelEvent(int X, int Y, int Delta) : TuiEvent;

internal sealed record RefreshEvent : TuiEvent;

internal sealed record InputClosedEvent : TuiEvent;

internal sealed class TuiInput : IDisposable
{
    private readonly Channel<TuiEvent> _events = Channel.CreateUnbounded<TuiEvent>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public ChannelReader<TuiEvent> Events => _events.Reader;

    public TuiInput()
    {
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "tui-input" };
        _thread.Start();
    }

    public void Post(TuiEvent evt) => _events.Writer.TryWrite(evt);

    private void ReadLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(15);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape && Console.KeyAvailable)
                {
                    HandleEscapeSequence();
                    continue;
                }

                _events.Writer.TryWrite(new KeyEvent(key));
            }
        }
        catch
        {
            _events.Writer.TryWrite(new InputClosedEvent());
        }
    }

    private void HandleEscapeSequence()
    {
        var first = Console.ReadKey(intercept: true);
        if (first.KeyChar != '[')
        {
            _events.Writer.TryWrite(new KeyEvent(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false)));
            _events.Writer.TryWrite(new KeyEvent(first));
            return;
        }

        var sb = new StringBuilder();
        while (Console.KeyAvailable && sb.Length < 32)
        {
            var ch = Console.ReadKey(intercept: true).KeyChar;
            sb.Append(ch);
            if (ch >= '@' && ch <= '~' && ch != '<' && !(sb.Length == 1 && ch == '<'))
                break;
        }

        var seq = sb.ToString();
        if (seq.Length == 0)
            return;

        if (seq[0] == '<')
            ParseSgrMouse(seq);
        else
            ParseCsiKey(seq);
    }

    private void ParseSgrMouse(string seq)
    {
        var final = seq[^1];
        if (final != 'M' && final != 'm')
            return;

        var parts = seq[1..^1].Split(';');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var b) ||
            !int.TryParse(parts[1], out var col) ||
            !int.TryParse(parts[2], out var row))
            return;

        var x = col - 1;
        var y = row - 1;

        if ((b & 64) != 0)
        {
            _events.Writer.TryWrite(new MouseWheelEvent(x, y, (b & 1) == 0 ? -1 : 1));
            return;
        }

        if (final == 'M' && (b & 32) == 0 && (b & 3) == 0)
            _events.Writer.TryWrite(new MouseClickEvent(x, y));
    }

    private void ParseCsiKey(string seq)
    {
        var key = seq[^1] switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            _ => ConsoleKey.None
        };

        if (seq == "5~") key = ConsoleKey.PageUp;
        if (seq == "6~") key = ConsoleKey.PageDown;

        if (key != ConsoleKey.None)
            _events.Writer.TryWrite(new KeyEvent(new ConsoleKeyInfo('\0', key, false, false, false)));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromMilliseconds(200));
        _cts.Dispose();
    }
}
