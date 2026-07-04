using System.Text;
using Susurri.Modules.DHT.Core.Services;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.CLI.Tui;

internal sealed class ChatsScreen
{
    private const int Accent = Palette.Accent;
    private const int Green = Palette.Green;
    private const int Red = Palette.Red;
    private const int Yellow = Palette.Yellow;
    private const int Mauve = Palette.Mauve;
    private const int Text = Palette.Text;
    private const int Dim = Palette.Dim;
    private const int BorderDim = Palette.BorderDim;
    private const int Selection = Palette.Selection;
    private const int StatusBg = Palette.StatusBg;

    private enum Mode
    {
        Normal,
        Insert,
        Recipient
    }

    private readonly SessionState _session;
    private readonly ConversationStore _store;

    private Mode _mode = Mode.Normal;
    private string? _selectedKey;
    private int _scroll;
    private int _msgPageSize = 10;
    private readonly StringBuilder _input = new();
    private int _cursor;
    private readonly StringBuilder _recipient = new();
    private string? _flash;
    private readonly List<(int Row, string Key)> _sidebarHits = new();
    private int _sidebarWidth;
    private int _inputRow;

    public ChatsScreen(SessionState session)
    {
        _session = session;
        _store = session.Conversations!;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var stdout = Console.Out;
        Console.SetOut(TextWriter.Null);
        _session.TuiActive = true;

        using var input = new TuiInput();
        Action onChanged = () => input.Post(new RefreshEvent());
        _store.Changed += onChanged;

        stdout.Write(Ansi.AltScreenOn + Ansi.ClearScreen + Ansi.HideCursor + Ansi.MouseOn);
        stdout.Flush();

        try
        {
            _store.RefreshGroups();

            Task<TuiEvent>? pending = null;
            var running = true;
            while (running && !ct.IsCancellationRequested)
            {
                Render(stdout);

                pending ??= input.Events.ReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(pending, Task.Delay(400, ct)).ConfigureAwait(false);
                if (completed != pending)
                    continue;

                var evt = await pending.ConfigureAwait(false);
                pending = null;
                running = Handle(evt);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _store.Changed -= onChanged;
            stdout.Write(Ansi.MouseOff + Ansi.ShowCursor + Ansi.AltScreenOff + Ansi.Reset);
            stdout.Flush();
            Console.SetOut(stdout);
            _session.TuiActive = false;
        }
    }

    private bool Handle(TuiEvent evt)
    {
        switch (evt)
        {
            case RefreshEvent:
                return true;
            case MouseClickEvent click:
                HandleClick(click);
                return true;
            case MouseWheelEvent wheel:
                HandleWheel(wheel);
                return true;
            case KeyEvent key:
                return _mode switch
                {
                    Mode.Normal => HandleNormal(key.Key),
                    Mode.Insert => HandleInsert(key.Key),
                    Mode.Recipient => HandleRecipient(key.Key),
                    _ => true
                };
            default:
                return true;
        }
    }

    private void HandleClick(MouseClickEvent click)
    {
        if (click.Y == _inputRow)
        {
            if (SelectedConversation() != null)
                _mode = Mode.Insert;
            return;
        }

        foreach (var (row, key) in _sidebarHits)
        {
            if (click.Y == row && click.X < _sidebarWidth)
            {
                _selectedKey = key;
                _scroll = 0;
                return;
            }
        }
    }

    private void HandleWheel(MouseWheelEvent wheel)
    {
        if (wheel.X < _sidebarWidth)
        {
            MoveSelection(wheel.Delta);
            return;
        }
        _scroll = Math.Max(0, _scroll - wheel.Delta * 3);
    }

    private bool HandleNormal(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.DownArrow:
                MoveSelection(1);
                return true;
            case ConsoleKey.UpArrow:
                MoveSelection(-1);
                return true;
            case ConsoleKey.Tab:
                MoveSelection(1);
                return true;
            case ConsoleKey.Enter:
                if (SelectedConversation() != null)
                    _mode = Mode.Insert;
                return true;
            case ConsoleKey.PageDown:
                _scroll = Math.Max(0, _scroll - _msgPageSize);
                return true;
            case ConsoleKey.PageUp:
                _scroll += _msgPageSize;
                return true;
        }

        switch (key.KeyChar)
        {
            case 'q':
                return false;
            case 'j':
                MoveSelection(1);
                break;
            case 'k':
                MoveSelection(-1);
                break;
            case 'i':
            case 'a':
                if (SelectedConversation() != null)
                    _mode = Mode.Insert;
                break;
            case 'n':
                _recipient.Clear();
                _mode = Mode.Recipient;
                break;
            case 'g':
                _scroll = int.MaxValue / 2;
                break;
            case 'G':
                _scroll = 0;
                break;
            case 'd':
                _scroll = Math.Max(0, _scroll - _msgPageSize);
                break;
            case 'u':
                _scroll += _msgPageSize;
                break;
        }
        return true;
    }

    private bool HandleInsert(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _mode = Mode.Normal;
                return true;
            case ConsoleKey.Enter:
                SubmitMessage();
                return true;
            case ConsoleKey.Backspace:
                if (_cursor > 0)
                {
                    _input.Remove(_cursor - 1, 1);
                    _cursor--;
                }
                return true;
            case ConsoleKey.Delete:
                if (_cursor < _input.Length)
                    _input.Remove(_cursor, 1);
                return true;
            case ConsoleKey.LeftArrow:
                _cursor = Math.Max(0, _cursor - 1);
                return true;
            case ConsoleKey.RightArrow:
                _cursor = Math.Min(_input.Length, _cursor + 1);
                return true;
            case ConsoleKey.Home:
                _cursor = 0;
                return true;
            case ConsoleKey.End:
                _cursor = _input.Length;
                return true;
            case ConsoleKey.UpArrow:
                _scroll++;
                return true;
            case ConsoleKey.DownArrow:
                _scroll = Math.Max(0, _scroll - 1);
                return true;
        }

        if (!char.IsControl(key.KeyChar))
        {
            _input.Insert(_cursor, key.KeyChar);
            _cursor++;
        }
        return true;
    }

    private bool HandleRecipient(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _mode = Mode.Normal;
                return true;
            case ConsoleKey.Enter:
                SubmitRecipient();
                return true;
            case ConsoleKey.Backspace:
                if (_recipient.Length > 0)
                    _recipient.Remove(_recipient.Length - 1, 1);
                return true;
        }

        if (!char.IsControl(key.KeyChar))
            _recipient.Append(key.KeyChar);
        return true;
    }

    private void SubmitMessage()
    {
        var conv = SelectedConversation();
        var content = _input.ToString().Trim();
        if (conv == null || content.Length == 0)
            return;

        _input.Clear();
        _cursor = 0;
        _scroll = 0;

        if (conv.Kind == ConversationKind.Direct)
        {
            _ = _store.SendDirectAsync(conv.Title, content);
        }
        else
        {
            var groupId = Guid.Parse(conv.Key[2..]);
            _ = _store.SendGroupAsync(groupId, content);
        }
    }

    private void SubmitRecipient()
    {
        var name = _recipient.ToString().Trim();
        _recipient.Clear();
        _mode = Mode.Normal;

        if (name.Length < SecurityLimits.MinUsernameLength ||
            name.Length > SecurityLimits.MaxUsernameLength ||
            name.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            _flash = "invalid username";
            return;
        }

        var conv = _store.EnsureDirect(name);
        _selectedKey = conv.Key;
        _scroll = 0;
        _mode = Mode.Insert;
    }

    private void MoveSelection(int delta)
    {
        var list = _store.Snapshot();
        if (list.Count == 0)
            return;

        var idx = list.ToList().FindIndex(c => c.Key == _selectedKey);
        idx = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, list.Count - 1);
        _selectedKey = list[idx].Key;
        _scroll = 0;
    }

    private Conversation? SelectedConversation()
    {
        var list = _store.Snapshot();
        return list.FirstOrDefault(c => c.Key == _selectedKey) ?? list.FirstOrDefault();
    }

    private void Render(TextWriter stdout)
    {
        int w, h;
        try
        {
            w = Console.WindowWidth;
            h = Console.WindowHeight;
        }
        catch
        {
            return;
        }

        if (w < 44 || h < 10)
        {
            stdout.Write(Ansi.ClearScreen + Ansi.MoveTo(0, 0) + "terminal too small");
            stdout.Flush();
            return;
        }

        var canvas = new Canvas(w, h);
        var list = _store.Snapshot();
        var selected = list.FirstOrDefault(c => c.Key == _selectedKey) ?? list.FirstOrDefault();
        if (selected != null)
        {
            _selectedKey = selected.Key;
            _store.MarkRead(selected);
        }

        _sidebarWidth = Math.Clamp(w / 4, 20, 32);
        var paneH = h - 2;
        _inputRow = h - 2;

        RenderSidebar(canvas, list, selected, paneH);
        RenderMessages(canvas, selected, paneH);
        RenderInputBar(canvas, w);
        RenderStatusBar(canvas, w, h);

        stdout.Write(canvas.Render());

        if (_mode == Mode.Insert)
        {
            var col = Math.Min(2 + _cursor, w - 1);
            stdout.Write(Ansi.MoveTo(_inputRow, col) + Ansi.ShowCursor);
        }
        else if (_mode == Mode.Recipient)
        {
            var col = Math.Min(6 + _recipient.Length, w - 1);
            stdout.Write(Ansi.MoveTo(_inputRow, col) + Ansi.ShowCursor);
        }

        stdout.Flush();
    }

    private void RenderSidebar(Canvas canvas, IReadOnlyList<Conversation> list, Conversation? selected, int paneH)
    {
        var sw = _sidebarWidth;
        var border = _mode == Mode.Normal ? Accent : BorderDim;
        canvas.Border(0, 0, sw, paneH, border, "susurri", Accent, titleBold: true);

        _sidebarHits.Clear();
        var y = 1;
        var innerW = sw - 2;

        foreach (var kind in new[] { ConversationKind.Direct, ConversationKind.Group })
        {
            if (y >= paneH - 1)
                break;

            var items = list.Where(c => c.Kind == kind).ToList();
            canvas.Write(2, y, kind == ConversationKind.Direct ? "DIRECT" : "GROUPS", Dim, bold: true);
            y++;

            if (items.Count == 0 && y < paneH - 1)
            {
                canvas.Write(3, y, "(none)", Dim, dim: true);
                y++;
            }

            for (var i = 0; i < items.Count && y < paneH - 1; i++)
            {
                var conv = items[i];
                var isSelected = selected != null && conv.Key == selected.Key;
                var glyph = i == items.Count - 1 ? "╰" : "├";
                var bg = isSelected ? Selection : -1;

                if (isSelected)
                    canvas.FillRect(1, y, sw - 2, 1, bg);

                canvas.Write(2, y, glyph, isSelected ? Accent : BorderDim, bg);

                var badge = conv.Unread > 0 ? $"●{Math.Min(conv.Unread, 99)}" : "";
                var titleMax = innerW - 3 - badge.Length - 1;
                var title = conv.Title.Length > titleMax && titleMax > 1
                    ? conv.Title[..(titleMax - 1)] + "…"
                    : conv.Title;

                canvas.Write(4, y, title, isSelected ? Text : (conv.Unread > 0 ? Text : Dim), bg, bold: isSelected || conv.Unread > 0);

                if (badge.Length > 0)
                    canvas.Write(sw - 1 - badge.Length - 1, y, badge, Yellow, bg, bold: true);

                _sidebarHits.Add((y, conv.Key));
                y++;
            }

            y++;
        }
    }

    private void RenderMessages(Canvas canvas, Conversation? selected, int paneH)
    {
        var x0 = _sidebarWidth;
        var pw = canvas.Width - x0;
        var border = _mode == Mode.Normal ? BorderDim : (_mode == Mode.Insert ? Accent : Mauve);
        var title = selected == null
            ? "messages"
            : selected.Kind == ConversationKind.Group ? $"◆ {selected.Title}" : $"@ {selected.Title}";
        canvas.Border(x0, 0, pw, paneH, border, title, Text, titleBold: true);

        var innerX = x0 + 2;
        var innerW = pw - 4;
        var innerH = paneH - 2;
        _msgPageSize = Math.Max(1, innerH - 1);

        var entries = selected == null ? new List<ChatEntry>() : _store.EntriesSnapshot(selected);
        if (selected == null || entries.Count == 0)
        {
            var hint = selected == null
                ? "no conversations yet — press n to start one"
                : "no messages yet — press i and say hi";
            canvas.Write(innerX + Math.Max(0, (innerW - hint.Length) / 2), paneH / 2, hint, Dim, dim: true);
            return;
        }

        var lines = BuildLines(entries, innerW);
        var maxScroll = Math.Max(0, lines.Count - innerH);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        var start = lines.Count - innerH - _scroll;

        var y = 1;
        for (var i = Math.Max(0, start); i < lines.Count && y <= innerH; i++, y++)
        {
            var x = innerX;
            foreach (var seg in lines[i])
                x += canvas.Write(x, y, seg.Text, seg.Fg, -1, seg.Bold, seg.Dim, innerX + innerW - x);
        }

        if (_scroll > 0)
            canvas.Write(x0 + pw - 12, paneH - 1, $" ↓ {_scroll} more ", Yellow, bold: true);
    }

    private List<List<(string Text, int Fg, bool Bold, bool Dim)>> BuildLines(List<ChatEntry> entries, int width)
    {
        var lines = new List<List<(string, int, bool, bool)>>();
        string? lastSender = null;
        var lastAt = DateTimeOffset.MinValue;

        foreach (var entry in entries)
        {
            var showHeader = entry.Sender != lastSender || (entry.At - lastAt) > TimeSpan.FromMinutes(3);
            lastSender = entry.Sender;
            lastAt = entry.At;

            if (showHeader)
            {
                if (lines.Count > 0)
                    lines.Add(new List<(string, int, bool, bool)>());

                var header = new List<(string, int, bool, bool)>
                {
                    ($"{entry.At.LocalDateTime:HH:mm} ", Dim, false, true),
                    (entry.Sender, entry.Outgoing ? Accent : Palette.SenderColor(entry.Sender), true, false)
                };
                lines.Add(header);
            }

            var status = entry.Outgoing
                ? entry.Status switch
                {
                    MessageStatus.Sending => (" ⋯", Dim),
                    MessageStatus.Sent => (" ✓", Dim),
                    MessageStatus.Acknowledged => (" ✓✓", Green),
                    MessageStatus.Failed => (" ✗", Red),
                    _ => ("", Dim)
                }
                : ("", Dim);

            var wrapped = Wrap(entry.Content, width - 2);
            for (var i = 0; i < wrapped.Count; i++)
            {
                var line = new List<(string, int, bool, bool)> { ("  " + wrapped[i], Text, false, false) };
                if (i == wrapped.Count - 1 && status.Item1.Length > 0)
                    line.Add((status.Item1, status.Item2, false, false));
                lines.Add(line);
            }
        }

        return lines;
    }

    private static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        if (width < 4)
        {
            result.Add(text);
            return result;
        }

        var current = new StringBuilder();
        var currentW = 0;

        foreach (var word in text.Split(' '))
        {
            var wordW = TextMeasure.Measure(word);
            if (currentW > 0 && currentW + 1 + wordW > width)
            {
                result.Add(current.ToString());
                current.Clear();
                currentW = 0;
            }

            if (wordW > width)
            {
                foreach (var ch in word)
                {
                    var cw = TextMeasure.CharWidth(ch);
                    if (currentW + cw > width)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        currentW = 0;
                    }
                    current.Append(ch);
                    currentW += cw;
                }
                continue;
            }

            if (currentW > 0)
            {
                current.Append(' ');
                currentW++;
            }
            current.Append(word);
            currentW += wordW;
        }

        if (current.Length > 0 || result.Count == 0)
            result.Add(current.ToString());

        return result;
    }

    private void RenderInputBar(Canvas canvas, int w)
    {
        var y = _inputRow;

        if (_mode == Mode.Recipient)
        {
            canvas.Write(0, y, " to: ", Mauve, bold: true);
            canvas.Write(5, y, _recipient.ToString(), Text);
            return;
        }

        canvas.Write(0, y, "❯ ", _mode == Mode.Insert ? Green : Dim, bold: _mode == Mode.Insert);

        if (_input.Length == 0 && _mode != Mode.Insert)
        {
            canvas.Write(2, y, "press i to type, n for new chat, q to quit", Dim, dim: true);
        }
        else
        {
            var text = _input.ToString();
            var visible = text.Length > w - 4 ? text[^(w - 4)..] : text;
            canvas.Write(2, y, visible, Text);
        }
    }

    private void RenderStatusBar(Canvas canvas, int w, int h)
    {
        var y = h - 1;
        canvas.FillRect(0, y, w, 1, StatusBg);

        var (label, color) = _mode switch
        {
            Mode.Insert => (" INSERT ", Green),
            Mode.Recipient => (" NEW ", Mauve),
            _ => (" NORMAL ", Accent)
        };
        canvas.Write(0, y, label, StatusBg, color, bold: true);

        var user = _session.CurrentUser ?? "?";
        canvas.Write(label.Length + 1, y, $"@{user}", Text, StatusBg, bold: true);

        if (_flash != null)
        {
            canvas.Write(label.Length + user.Length + 4, y, _flash, Red, StatusBg, bold: true);
            _flash = null;
        }

        var chat = _session.Chat;
        var right = chat != null
            ? $"⇅ {chat.PeerCount} peers · {chat.ActiveRelays} relays · {DateTime.Now:HH:mm} "
            : $"{DateTime.Now:HH:mm} ";
        canvas.Write(Math.Max(0, w - right.Length - 1), y, right, Dim, StatusBg);
    }
}
