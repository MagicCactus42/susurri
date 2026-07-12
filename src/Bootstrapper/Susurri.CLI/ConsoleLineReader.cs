using System.Text;

namespace Susurri.CLI;

internal sealed class ConsoleLineReader
{
    public static ConsoleLineReader Shared { get; } = new();

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private string _prompt = string.Empty;
    private bool _active;
    private bool _plainOnly;

    public async Task<string?> ReadLineAsync(string prompt, CancellationToken ct)
    {
        if (_plainOnly || Console.IsInputRedirected || !ConsoleUi.Fancy)
        {
            Console.Write(prompt);
            return await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
        }

        try
        {
            return await ReadInteractiveAsync(prompt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                _active = false;
                _plainOnly = true;
            }
            Console.Write(prompt);
            return await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
        }
    }

    public void WriteInterrupting(Action write)
    {
        lock (_gate)
        {
            if (!_active)
            {
                write();
                return;
            }

            Console.Write("\r\x1b[2K");
            write();
            Console.Write(_prompt);
            Console.Write(_buffer.ToString());
        }
    }

    private async Task<string?> ReadInteractiveAsync(string prompt, CancellationToken ct)
    {
        lock (_gate)
        {
            _buffer.Clear();
            _prompt = prompt;
            _active = true;
            Console.Write(prompt);
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (!Console.KeyAvailable)
                {
                    await Task.Delay(15, ct).ConfigureAwait(false);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                lock (_gate)
                {
                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        _active = false;
                        return _buffer.ToString();
                    }

                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (_buffer.Length > 0)
                        {
                            _buffer.Remove(_buffer.Length - 1, 1);
                            Console.Write("\b \b");
                        }
                        continue;
                    }

                    if (!char.IsControl(key.KeyChar))
                    {
                        _buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
                _active = false;
        }
    }
}
