using System.Text;

namespace Susurri.CLI.Tui;

internal struct Cell
{
    public char Ch;
    public int Fg;
    public int Bg;
    public bool Bold;
    public bool Dim;

    public static readonly Cell Blank = new() { Ch = ' ', Fg = -1, Bg = -1 };
}

internal sealed class Canvas
{
    public int Width { get; }
    public int Height { get; }

    private readonly Cell[] _cells;

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        _cells = new Cell[width * height];
        Clear();
    }

    public void Clear()
    {
        Array.Fill(_cells, Cell.Blank);
    }

    public void Set(int x, int y, char ch, int fg = -1, int bg = -1, bool bold = false, bool dim = false)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return;
        _cells[y * Width + x] = new Cell { Ch = ch, Fg = fg, Bg = bg, Bold = bold, Dim = dim };
    }

    public int Write(int x, int y, string text, int fg = -1, int bg = -1, bool bold = false, bool dim = false, int maxWidth = int.MaxValue)
    {
        var col = x;
        foreach (var ch in text)
        {
            if (col - x >= maxWidth || col >= Width)
                break;
            var w = TextMeasure.CharWidth(ch);
            Set(col, y, ch, fg, bg, bold, dim);
            if (w == 2 && col + 1 < Width && col + 1 - x < maxWidth)
                Set(col + 1, y, '\0', fg, bg, bold, dim);
            col += w;
        }
        return col - x;
    }

    public void FillRect(int x, int y, int w, int h, int bg, int fg = -1)
    {
        for (var yy = y; yy < y + h; yy++)
        for (var xx = x; xx < x + w; xx++)
            Set(xx, yy, ' ', fg, bg);
    }

    public void Border(int x, int y, int w, int h, int fg, string? title = null, int titleFg = -1, bool titleBold = false)
    {
        if (w < 2 || h < 2)
            return;

        Set(x, y, '╭', fg);
        Set(x + w - 1, y, '╮', fg);
        Set(x, y + h - 1, '╰', fg);
        Set(x + w - 1, y + h - 1, '╯', fg);

        for (var xx = x + 1; xx < x + w - 1; xx++)
        {
            Set(xx, y, '─', fg);
            Set(xx, y + h - 1, '─', fg);
        }

        for (var yy = y + 1; yy < y + h - 1; yy++)
        {
            Set(x, yy, '│', fg);
            Set(x + w - 1, yy, '│', fg);
        }

        if (!string.IsNullOrEmpty(title) && w > 6)
        {
            var text = $" {title} ";
            if (text.Length > w - 4)
                text = text[..(w - 4)];
            Write(x + 2, y, text, titleFg < 0 ? fg : titleFg, -1, titleBold);
        }
    }

    public string Render()
    {
        var sb = new StringBuilder(Width * Height * 4);
        sb.Append(Ansi.SyncStart);
        sb.Append(Ansi.HideCursor);

        var fg = int.MinValue;
        var bg = int.MinValue;
        var bold = false;
        var dim = false;

        for (var y = 0; y < Height; y++)
        {
            sb.Append(Ansi.MoveTo(y, 0));
            for (var x = 0; x < Width; x++)
            {
                var cell = _cells[y * Width + x];
                if (cell.Ch == '\0')
                    continue;

                if (cell.Bold != bold || cell.Dim != dim)
                {
                    sb.Append(Ansi.Reset);
                    fg = int.MinValue;
                    bg = int.MinValue;
                    bold = cell.Bold;
                    dim = cell.Dim;
                    if (bold) sb.Append("\x1b[1m");
                    if (dim) sb.Append("\x1b[2m");
                }
                if (cell.Fg != fg)
                {
                    sb.Append(Ansi.Fg(cell.Fg));
                    fg = cell.Fg;
                }
                if (cell.Bg != bg)
                {
                    sb.Append(Ansi.Bg(cell.Bg));
                    bg = cell.Bg;
                }
                sb.Append(cell.Ch);
            }
        }

        sb.Append(Ansi.Reset);
        sb.Append(Ansi.SyncEnd);
        return sb.ToString();
    }
}

internal static class TextMeasure
{
    public static int CharWidth(char ch)
    {
        if (ch is >= 'ᄀ' and <= 'ᅟ') return 2;
        if (ch is >= '⺀' and <= '꓏') return 2;
        if (ch is >= '가' and <= '힣') return 2;
        if (ch is >= '豈' and <= '﫿') return 2;
        if (ch is >= '＀' and <= '｠') return 2;
        if (ch is >= '￠' and <= '￦') return 2;
        return 1;
    }

    public static int Measure(string text)
    {
        var w = 0;
        foreach (var ch in text)
            w += CharWidth(ch);
        return w;
    }

    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsSurrogate(ch))
            {
                if (char.IsHighSurrogate(ch))
                    sb.Append('□');
                continue;
            }
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString();
    }
}
