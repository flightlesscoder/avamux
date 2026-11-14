using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;

namespace Avamux.EasterEggs;

// A tiny windowing system in the style of a 16-colour desktop from around 1990, drawn into the 640x400 DOS screen:
// windows with thick frames and control boxes, bevelled buttons, menus, list boxes, edit fields and an arrow cursor.

/// <summary>The 16 colours of the desktop, and drawing helpers for its bevels and frames.</summary>
public static class W3
{
    public static readonly Color Black = C("#000000"), Maroon = C("#800000"), Green = C("#008000"), Olive = C("#808000"),
        Navy = C("#000080"), Purple = C("#800080"), Teal = C("#008080"), Silver = C("#c0c0c0"), Gray = C("#808080"),
        Red = C("#ff0000"), Lime = C("#00ff00"), Yellow = C("#ffff00"), Blue = C("#0000ff"), Fuchsia = C("#ff00ff"),
        Aqua = C("#00ffff"), White = C("#ffffff");

    private static Color C(string hex) => Color.Parse(hex);
    private static readonly Dictionary<Color, IBrush> Brushes = new();

    public static IBrush B(Color c)
    {
        if (!Brushes.TryGetValue(c, out var b)) Brushes[c] = b = new ImmutableSolidColorBrush(c);
        return b;
    }

    public static void Fill(DrawingContext g, Rect r, Color c) => g.FillRectangle(B(c), r);
    public static void Fill(DrawingContext g, double x, double y, double w, double h, Color c) => g.FillRectangle(B(c), new Rect(x, y, w, h));

    public static void Outline(DrawingContext g, Rect r, Color c, double t = 1)
    {
        Fill(g, r.X, r.Y, r.Width, t, c);
        Fill(g, r.X, r.Bottom - t, r.Width, t, c);
        Fill(g, r.X, r.Y, t, r.Height, c);
        Fill(g, r.Right - t, r.Y, t, r.Height, c);
    }

    /// <summary>A raised silver face: white light from the top left, a grey shadow at the bottom right.</summary>
    public static void Raised(DrawingContext g, Rect r, bool pressed = false, int depth = 2, Color? face = null)
    {
        Fill(g, r, face ?? Silver);
        if (pressed)
        {
            Fill(g, r.X, r.Y, r.Width, 1, Gray);
            Fill(g, r.X, r.Y, 1, r.Height, Gray);
            return;
        }
        for (var d = 0; d < depth; d++)
        {
            Fill(g, r.X + d, r.Y + d, r.Width - 2 * d - 1, 1, White);
            Fill(g, r.X + d, r.Y + d, 1, r.Height - 2 * d - 1, White);
            Fill(g, r.X + d, r.Bottom - 1 - d, r.Width - 2 * d, 1, Gray);
            Fill(g, r.Right - 1 - d, r.Y + d, 1, r.Height - 2 * d, Gray);
        }
    }

    /// <summary>A sunken well: the reverse of <see cref="Raised"/>.</summary>
    public static void Sunken(DrawingContext g, Rect r, Color face)
    {
        Fill(g, r, face);
        Fill(g, r.X, r.Y, r.Width, 1, Gray);
        Fill(g, r.X, r.Y, 1, r.Height, Gray);
        Fill(g, r.X, r.Bottom - 1, r.Width, 1, White);
        Fill(g, r.Right - 1, r.Y, 1, r.Height, White);
    }

    /// <summary>A push button: a black outline with its corners cut, thicker on the default button.</summary>
    public static void Button(DrawingContext g, Rect r, bool pressed, bool thick)
    {
        var o = thick ? 2 : 1;
        for (var i = 0; i < o; i++)
        {
            Fill(g, r.X + 1, r.Y + i, r.Width - 2, 1, Black);
            Fill(g, r.X + 1, r.Bottom - 1 - i, r.Width - 2, 1, Black);
            Fill(g, r.X + i, r.Y + 1, 1, r.Height - 2, Black);
            Fill(g, r.Right - 1 - i, r.Y + 1, 1, r.Height - 2, Black);
        }
        Raised(g, r.Deflate(o), pressed);
    }

    public static readonly IPen Dotted = new ImmutablePen(new ImmutableSolidColorBrush(Black), 1, new ImmutableDashStyle([1, 1], 0));

    public static void Triangle(DrawingContext g, double cx, double cy, int size, bool up, Color c)
    {
        for (var i = 0; i < size; i++)
        {
            var row = up ? i : size - 1 - i;
            Fill(g, cx - row, cy - size / 2.0 + i, row * 2 + 1, 1, c);
        }
    }

    /// <summary>A vertical scroll bar with arrow buttons and a square thumb.</summary>
    public static void VScroll(DrawingContext g, Rect r, int top, int maxTop)
    {
        var w = r.Width;
        Fill(g, r, Silver);
        // The track is a light checkerboard.
        for (var y = r.Y + w; y < r.Bottom - w; y += 2) Fill(g, r.X + (int)(y - r.Y) % 4 / 2, y, w, 1, White);
        Outline(g, r, Black);
        foreach (var (y, up) in new[] { (r.Y, true), (r.Bottom - w, false) })
        {
            var b = new Rect(r.X, y, w, w);
            Outline(g, b, Black);
            Raised(g, b.Deflate(1), false, 1);
            Triangle(g, b.Center.X, b.Center.Y, 4, up, maxTop > 0 ? Black : Gray);
        }
        if (maxTop <= 0) return;
        var span = r.Height - 3 * w;
        var thumb = new Rect(r.X, r.Y + w + Math.Round(span * top / maxTop), w, w);
        Outline(g, thumb, Black);
        Raised(g, thumb.Deflate(1), false, 1);
    }

    /// <summary>Where a click on a scroll bar moves the view to.</summary>
    public static int VScrollHit(Rect r, Point p, int top, int maxTop, int page)
    {
        var w = r.Width;
        if (p.Y < r.Y + w) return Math.Max(0, top - 1);
        if (p.Y >= r.Bottom - w) return Math.Min(maxTop, top + 1);
        var span = r.Height - 3 * w;
        var thumbY = r.Y + w + (maxTop > 0 ? span * top / maxTop : 0);
        return p.Y < thumbY ? Math.Max(0, top - page) : p.Y > thumbY + w ? Math.Min(maxTop, top + page) : top;
    }
}

public enum TF { Sys, Small, SmallBold, Big, Head, Logo, Splash, Mono }

/// <summary>Text drawn without anti-aliasing, cached, in a bold sans "system" font and a few others.</summary>
public static class Txt
{
    private static readonly FontFamily Sans = Pick(["Liberation Sans", "Arial", "Helvetica", "Nimbus Sans", "DejaVu Sans", "Noto Sans"], "sans-serif");
    private static readonly FontFamily Serif = Pick(["Liberation Serif", "Times New Roman", "Nimbus Roman", "DejaVu Serif", "Noto Serif"], "serif");

    private static readonly (Typeface Face, double Size)[] Faces =
    [
        (new Typeface(Sans, FontStyle.Normal, FontWeight.Bold), 12),
        (new Typeface(Sans), 11),
        (new Typeface(Sans, FontStyle.Normal, FontWeight.Bold), 11),
        (new Typeface(Sans, FontStyle.Normal, FontWeight.Bold), 15),
        (new Typeface(Serif, FontStyle.Normal, FontWeight.Bold), 13),
        (new Typeface(Serif, FontStyle.Italic, FontWeight.Bold), 23),
        (new Typeface(Serif, FontStyle.Italic, FontWeight.Bold), 44),
        (new Typeface(Views.Ui.Mono), 11),
    ];

    public static readonly TextOptions Aliased = new() { TextRenderingMode = TextRenderingMode.Alias };
    private static readonly Dictionary<(string, TF, Color, bool), FormattedText> Cache = new();

    private static FontFamily Pick(string[] wanted, string fallback)
    {
        try
        {
            var installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted.FirstOrDefault(installed.Contains) is { } name) return new FontFamily(name);
        }
        catch { /* fall through */ }
        return new FontFamily(fallback);
    }

    public static FormattedText Get(string s, TF f, Color c, bool underline = false)
    {
        if (Cache.TryGetValue((s, f, c, underline), out var t)) return t;
        if (Cache.Count > 4000) Cache.Clear();
        var (face, size) = Faces[(int)f];
        t = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, W3.B(c));
        if (underline) t.SetTextDecorations(TextDecorations.Underline);
        return Cache[(s, f, c, underline)] = t;
    }

    public static double Width(string s, TF f) => Get(s, f, W3.Black).WidthIncludingTrailingWhitespace;
    public static double LineHeight(TF f) => f switch { TF.Sys => 16, TF.Small or TF.SmallBold or TF.Mono => 14, TF.Big => 19, TF.Head => 16, TF.Logo => 28, _ => 52 };

    public static void Draw(DrawingContext g, string s, double x, double y, TF f, Color c, bool underline = false) =>
        g.DrawText(Get(s, f, c, underline), new Point(Math.Round(x), Math.Round(y)));

    /// <summary>Centred in a rectangle; returns where the text went.</summary>
    public static Rect Centre(DrawingContext g, string s, Rect r, TF f, Color c)
    {
        var t = Get(s, f, c);
        var at = new Point(Math.Round(r.X + (r.Width - t.WidthIncludingTrailingWhitespace) / 2), Math.Round(r.Y + (r.Height - t.Height) / 2));
        g.DrawText(t, at);
        return new Rect(at, new Size(t.WidthIncludingTrailingWhitespace, t.Height));
    }

    /// <summary>Breaks text into lines no wider than <paramref name="width"/>, at spaces where it can.</summary>
    public static List<string> Wrap(string s, TF f, double width, double firstWidth = -1)
    {
        var lines = new List<string>();
        foreach (var para in s.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' '))
            {
                var limit = lines.Count == 0 && firstWidth >= 0 ? firstWidth : width;
                var tryLine = line.Length == 0 ? word : line + " " + word;
                if (Width(tryLine, f) <= limit || line.Length == 0 && Width(word, f) <= limit) { line = tryLine; continue; }
                if (line.Length > 0) { lines.Add(line); line = ""; }
                // A word longer than a line is broken where it has to be.
                var w = word;
                while (Width(w, f) > width && w.Length > 1)
                {
                    var n = w.Length - 1;
                    while (n > 1 && Width(w[..n], f) > width) n--;
                    lines.Add(w[..n]);
                    w = w[n..];
                }
                line = w;
            }
            lines.Add(line);
        }
        return lines;
    }

    public static string Fit(string s, TF f, double width)
    {
        if (Width(s, f) <= width) return s;
        while (s.Length > 0 && Width(s + "...", f) > width) s = s[..^1];
        return s + "...";
    }
}

/// <summary>Little pictures as rows of letters, one letter a colour; '.' is see-through.</summary>
public static class Art
{
    private static readonly Dictionary<char, Color> Map = new()
    {
        ['k'] = W3.Black, ['m'] = W3.Maroon, ['e'] = W3.Green, ['o'] = W3.Olive, ['n'] = W3.Navy, ['p'] = W3.Purple,
        ['t'] = W3.Teal, ['s'] = W3.Silver, ['g'] = W3.Gray, ['r'] = W3.Red, ['l'] = W3.Lime, ['y'] = W3.Yellow,
        ['b'] = W3.Blue, ['f'] = W3.Fuchsia, ['a'] = W3.Aqua, ['w'] = W3.White,
    };

    private static readonly Dictionary<(string[], bool), RenderTargetBitmap> Cache = new();

    public static RenderTargetBitmap Get(string[] rows, bool disabled = false)
    {
        if (Cache.TryGetValue((rows, disabled), out var bmp)) return bmp;
        var w = rows.Max(r => r.Length);
        bmp = new RenderTargetBitmap(new PixelSize(w, rows.Length));
        using (var g = bmp.CreateDrawingContext())
        using (g.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased }))
        {
            for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
            {
                if (!Map.TryGetValue(rows[y][x], out var c)) continue;
                // Greyed out: the outline in dark grey, everything else gone, like a disabled toolbar picture.
                if (disabled) { if (c != W3.Black && c != W3.Gray && c != W3.Navy) continue; c = W3.Gray; }
                W3.Fill(g, x, y, 1, 1, c);
            }
        }
        return Cache[(rows, disabled)] = bmp;
    }

    public static void Draw(DrawingContext g, string[] rows, double x, double y, int scale = 1, bool disabled = false)
    {
        var bmp = Get(rows, disabled);
        g.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), new Rect(Math.Round(x), Math.Round(y), bmp.PixelSize.Width * scale, bmp.PixelSize.Height * scale));
    }

    public static Size SizeOf(string[] rows) => new(rows.Max(r => r.Length), rows.Length);

    public static readonly string[] Arrow =
    [
        "k", "kk", "kwk", "kwwk", "kwwwk", "kwwwwk", "kwwwwwk", "kwwwwwwk", "kwwwwwwwk", "kwwwwwwwwk", "kwwwwwkkkkk",
        "kwwkwwk", "kwk.kwwk", "kk..kwwk", "k....kwwk", ".....kwwk", "......kwwk", "......kwwk", ".......kk",
    ];

    public static readonly string[] Hourglass =
    [
        "kkkkkkkkkkk", "kwwwwwwwwwk", ".kwwwwwwwk.", ".kwkkkkkwk.", "..kwkkkwk..", "...kwkwk...", "....kwk....", "....kwk....",
        "...kwwwk...", "..kwwkwwk..", ".kwwwkwwwk.", ".kwwkkkwwk.", "kwkkkkkkkwk", "kkkkkkkkkkk",
    ];
}

/// <summary>Something inside a window: a button, an edit field, a list...</summary>
public abstract class Widget
{
    /// <summary>Position in the window's client area.</summary>
    public Rect R;
    public Wnd Win = null!;
    public bool Visible = true;
    public Func<bool>? EnabledIf;
    public bool Enabled => EnabledIf?.Invoke() ?? true;
    public virtual bool Focusable => false;
    public bool HasFocus => Win.Focused == this && Win.Desk.Active == Win;
    public Rect Abs => new(Win.Client.X + R.X, Win.Client.Y + R.Y, R.Width, R.Height);
    public bool Hover => Abs.Contains(Win.Desk.Mouse) && Win.Desk.TopAt(Win.Desk.Mouse) == Win;

    public abstract void Draw(DrawingContext g, Rect r);
    /// <summary>Points are relative to the widget.</summary>
    public virtual void Press(Point p, int clicks) { }
    public virtual void Drag(Point p) { }
    public virtual void Release(Point p, bool inside) { }
    public virtual void Wheel(double dy) { }
    public virtual bool OnKey(Key k, KeyModifiers m) => false;
    public virtual void Text(string s) { }
}

public sealed class PushButton(string label, Action click) : Widget
{
    public string Label = label;
    public Action Click = click;
    private bool _down;
    public override bool Focusable => true;

    public override void Draw(DrawingContext g, Rect r)
    {
        var pressed = _down && Win.Desk.PressedInside;
        W3.Button(g, r, pressed, Win.Default == this && Enabled);
        var o = pressed ? 1 : 0;
        var at = Txt.Centre(g, Label, new Rect(r.X + o, r.Y + o, r.Width, r.Height), TF.Sys, Enabled ? W3.Black : W3.Gray);
        if (HasFocus) g.DrawRectangle(null, W3.Dotted, at.Inflate(new Thickness(3, 0, 3, 1)).Translate(new Vector(0.5, 0.5)));
    }

    public override void Press(Point p, int clicks) => _down = Enabled;

    public override void Release(Point p, bool inside)
    {
        var fire = _down && inside && Enabled;
        _down = false;
        if (fire) Click();
    }

    public override bool OnKey(Key k, KeyModifiers m)
    {
        if (k is not (Key.Space or Key.Enter) || !Enabled) return false;
        Click();
        return true;
    }
}

/// <summary>A button on a toolbar: a picture with its name underneath.</summary>
public sealed class ToolButton(string[] art, string label, Action click) : Widget
{
    private bool _down;

    public override void Draw(DrawingContext g, Rect r)
    {
        var pressed = _down && Win.Desk.PressedInside;
        W3.Outline(g, r, W3.Black);
        W3.Raised(g, r.Deflate(1), pressed);
        var o = pressed ? 1 : 0;
        var size = Art.SizeOf(art);
        Art.Draw(g, art, r.X + (r.Width - size.Width) / 2 + o, r.Y + 3 + o, 1, !Enabled);
        var text = new Rect(r.X + o, r.Bottom - 15 + o, r.Width, 13);
        if (!Enabled) Txt.Centre(g, label, text.Translate(new Vector(1, 1)), TF.Small, W3.White);
        Txt.Centre(g, label, text, TF.Small, Enabled ? W3.Black : W3.Gray);
    }

    public override void Press(Point p, int clicks) => _down = Enabled;

    public override void Release(Point p, bool inside)
    {
        var fire = _down && inside && Enabled;
        _down = false;
        if (fire) click();
    }
}

/// <summary>A one-line edit field.</summary>
public sealed class EditField : Widget
{
    public string Value = "";
    public int Max = 60;
    public bool Password;
    /// <summary>What Enter does; without it, Enter presses the window's default button.</summary>
    public Action? Enter;
    private int _caret = -1;
    public override bool Focusable => true;

    private int Caret
    {
        get => _caret < 0 || _caret > Value.Length ? Value.Length : _caret;
        set => _caret = Math.Clamp(value, 0, Value.Length);
    }

    private string Shown => Password ? new string('*', Value.Length) : Value;

    public override void Draw(DrawingContext g, Rect r)
    {
        W3.Sunken(g, r, W3.White);
        W3.Outline(g, r.Deflate(1), W3.Black);
        var inner = r.Deflate(3);
        var shown = Shown;
        var caretX = Txt.Width(shown[..Caret], TF.Sys);
        var scroll = Math.Max(0, caretX - (inner.Width - 4));
        using (g.PushClip(inner))
        {
            Txt.Draw(g, shown, inner.X + 1 - scroll, inner.Y + (inner.Height - 15) / 2, TF.Sys, Enabled ? W3.Black : W3.Gray);
            if (HasFocus && Win.Desk.Time % 1 < 0.55) W3.Fill(g, inner.X + 1 + caretX - scroll, inner.Y + 1, 1, inner.Height - 2, W3.Black);
        }
    }

    public override void Press(Point p, int clicks)
    {
        var shown = Shown;
        var i = 0;
        while (i < shown.Length && Txt.Width(shown[..(i + 1)], TF.Sys) < p.X - 4) i++;
        Caret = i;
    }

    public override bool OnKey(Key k, KeyModifiers m)
    {
        switch (k)
        {
            case Key.Back when Caret > 0: var c = Caret; Value = Value.Remove(c - 1, 1); Caret = c - 1; return true;
            case Key.Delete when Caret < Value.Length: var d = Caret; Value = Value.Remove(d, 1); Caret = d; return true;
            case Key.Left: Caret--; return true;
            case Key.Right: Caret++; return true;
            case Key.Home: Caret = 0; return true;
            case Key.End: Caret = Value.Length; return true;
            case Key.Enter when Enter is not null: Enter(); return true;
            case Key.Back or Key.Delete: return true;
        }
        return false;
    }

    public override void Text(string s)
    {
        foreach (var ch in s)
        {
            if (char.IsControl(ch) || Value.Length >= Max) continue;
            var c = Caret;
            Value = Value.Insert(c, ch.ToString());
            Caret = c + 1;
        }
    }

    public void Set(string value) { Value = value; _caret = -1; }
}

/// <summary>Several lines of typing, wrapped; the caret stays at the end.</summary>
public sealed class MemoField : Widget
{
    public string Value = "";
    public override bool Focusable => true;

    public override void Draw(DrawingContext g, Rect r)
    {
        W3.Sunken(g, r, W3.White);
        W3.Outline(g, r.Deflate(1), W3.Black);
        var inner = r.Deflate(4);
        var lines = Txt.Wrap(Value, TF.Small, inner.Width - 4);
        var lh = Txt.LineHeight(TF.Small);
        var first = Math.Max(0, lines.Count - (int)(inner.Height / lh));
        using (g.PushClip(inner))
        {
            for (var i = first; i < lines.Count; i++) Txt.Draw(g, lines[i], inner.X, inner.Y + (i - first) * lh, TF.Small, W3.Black);
            if (HasFocus && Win.Desk.Time % 1 < 0.55)
                W3.Fill(g, inner.X + Txt.Width(lines[^1], TF.Small) + 1, inner.Y + (lines.Count - 1 - first) * lh + 1, 1, lh - 2, W3.Black);
        }
    }

    public override bool OnKey(Key k, KeyModifiers m)
    {
        switch (k)
        {
            case Key.Back: if (Value.Length > 0) Value = Value[..^1]; return true;
            case Key.Enter: if (Value.Length < 2000) Value += "\n"; return true;
        }
        return false;
    }

    public override void Text(string s)
    {
        foreach (var ch in s)
            if (!char.IsControl(ch) && Value.Length < 2000) Value += ch;
    }
}

public sealed record ListRow(string Text, Color? Color = null, bool Bold = false, object? Tag = null, string[]? Icon = null, bool Header = false);

/// <summary>A list box: rows, one of them selected, a scroll bar when they don't fit.</summary>
public sealed class ListPane : Widget
{
    public readonly List<ListRow> Rows = [];
    public int Selected = -1;
    public Action<ListRow>? Activate;
    /// <summary>Where the columns of a row split by tabs begin.</summary>
    public double[] Tabs = [];
    public int RowH = 15;
    private int _top;
    public override bool Focusable => true;

    private int VisibleRows => Math.Max(1, (int)((R.Height - 2) / RowH));
    private int MaxTop => Math.Max(0, Rows.Count - VisibleRows);
    private const int Bar = 16;

    public ListRow? Current => Selected >= 0 && Selected < Rows.Count ? Rows[Selected] : null;

    public override void Draw(DrawingContext g, Rect r)
    {
        W3.Fill(g, r, W3.White);
        W3.Outline(g, r, W3.Black);
        _top = Math.Clamp(_top, 0, MaxTop);
        var scroll = MaxTop > 0;
        var inner = new Rect(r.X + 1, r.Y + 1, r.Width - 2 - (scroll ? Bar - 1 : 0), r.Height - 2);
        using (g.PushClip(inner))
        {
            for (var i = _top; i < Rows.Count && i < _top + VisibleRows + 1; i++)
            {
                var row = Rows[i];
                var y = inner.Y + (i - _top) * RowH;
                var sel = i == Selected && !row.Header;
                if (sel) W3.Fill(g, inner.X, y, inner.Width, RowH, HasFocus || Win.Desk.Active == Win ? W3.Navy : W3.Gray);
                var color = sel ? W3.White : row.Color ?? W3.Black;
                var x = inner.X + 3;
                if (row.Icon is { } icon) { Art.Draw(g, icon, x, y + (RowH - Art.SizeOf(icon).Height) / 2); x += Art.SizeOf(icon).Width + 4; }
                var cols = row.Text.Split('\t');
                for (var c = 0; c < cols.Length; c++)
                {
                    var cx = c == 0 ? x : inner.X + 3 + (c - 1 < Tabs.Length ? Tabs[c - 1] : 0);
                    var limit = c + 1 <= Tabs.Length ? inner.X + 3 + Tabs[c] - cx - 6 : inner.Right - cx - 2;
                    Txt.Draw(g, Txt.Fit(cols[c], row.Bold || row.Header ? TF.SmallBold : TF.Small, limit), cx, y, row.Bold || row.Header ? TF.SmallBold : TF.Small, color);
                }
            }
        }
        if (scroll) W3.VScroll(g, new Rect(r.Right - Bar, r.Y, Bar, r.Height), _top, MaxTop);
    }

    public override void Press(Point p, int clicks)
    {
        if (MaxTop > 0 && p.X >= R.Width - Bar) { _top = W3.VScrollHit(new Rect(0, 0, Bar, R.Height), new Point(0, p.Y), _top, MaxTop, VisibleRows); return; }
        var i = _top + (int)((p.Y - 1) / RowH);
        if (i < 0 || i >= Rows.Count || Rows[i].Header) return;
        Selected = i;
        if (clicks >= 2) Activate?.Invoke(Rows[i]);
    }

    public override void Wheel(double dy) => _top = Math.Clamp(_top - (int)Math.Round(dy * 3), 0, MaxTop);

    public override bool OnKey(Key k, KeyModifiers m)
    {
        var step = k switch { Key.Up => -1, Key.Down => 1, _ => 0 };
        if (step != 0)
        {
            var i = Selected;
            do i += step; while (i >= 0 && i < Rows.Count && Rows[i].Header);
            if (i >= 0 && i < Rows.Count) Selected = i;
            if (Selected < _top) _top = Selected;
            if (Selected >= _top + VisibleRows) _top = Selected - VisibleRows + 1;
            return true;
        }
        if (k == Key.Enter && Current is { } row) { Activate?.Invoke(row); return true; }
        return false;
    }
}

/// <summary>A read-only page of text that grows at the bottom: a chat, an instant message, a letter.</summary>
public sealed class ChatLog : Widget
{
    private readonly record struct Line(string? Who, Color WhoColor, string Text, Color TextColor, bool Italic);
    private readonly List<Line> _lines = [];
    private int _top;
    private bool _follow = true;
    private const int Bar = 16;
    private static double LH => Txt.LineHeight(TF.Small);
    private int VisibleRows => Math.Max(1, (int)((R.Height - 4) / LH));
    private int MaxTop => Math.Max(0, _lines.Count - VisibleRows);

    public void Add(string? who, string text, Color? whoColor = null, Color? textColor = null)
    {
        var avail = R.Width - Bar - 8;
        var label = who is null ? "" : who + ":  ";
        var first = avail - Txt.Width(label, TF.SmallBold);
        var wrapped = Txt.Wrap(text, TF.Small, avail, first);
        for (var i = 0; i < wrapped.Count; i++)
            _lines.Add(new Line(i == 0 ? who : null, whoColor ?? W3.Navy, i == 0 && who is not null ? label + "\u0001" + wrapped[i] : wrapped[i], textColor ?? W3.Black, false));
        if (_lines.Count > 400) _lines.RemoveRange(0, _lines.Count - 400);
        if (_follow) _top = MaxTop;
    }

    public void Clear() { _lines.Clear(); _top = 0; _follow = true; }

    /// <summary>Back to the first line, for something to be read from the beginning.</summary>
    public void ScrollToTop() { _top = 0; _follow = false; }

    public override void Draw(DrawingContext g, Rect r)
    {
        W3.Fill(g, r, W3.White);
        W3.Outline(g, r, W3.Black);
        _top = Math.Clamp(_top, 0, MaxTop);
        using (g.PushClip(new Rect(r.X + 1, r.Y + 1, r.Width - Bar - 1, r.Height - 2)))
        {
            for (var i = _top; i < _lines.Count && i < _top + VisibleRows + 1; i++)
            {
                var line = _lines[i];
                var y = r.Y + 2 + (i - _top) * LH;
                var x = r.X + 4;
                var text = line.Text;
                if (line.Who is not null)
                {
                    var split = text.IndexOf('\u0001');
                    var label = text[..split];
                    Txt.Draw(g, label, x, y, TF.SmallBold, line.WhoColor);
                    x += Txt.Width(label, TF.SmallBold);
                    text = text[(split + 1)..];
                }
                Txt.Draw(g, text, x, y, TF.Small, line.TextColor);
            }
        }
        W3.VScroll(g, new Rect(r.Right - Bar, r.Y, Bar, r.Height), _top, MaxTop);
    }

    public override void Press(Point p, int clicks)
    {
        if (p.X < R.Width - Bar) return;
        _top = W3.VScrollHit(new Rect(0, 0, Bar, R.Height), new Point(0, p.Y), _top, MaxTop, VisibleRows);
        _follow = _top >= MaxTop;
    }

    public override void Wheel(double dy)
    {
        _top = Math.Clamp(_top - (int)Math.Round(dy * 3), 0, MaxTop);
        _follow = _top >= MaxTop;
    }
}

/// <summary>A clickable area that draws itself: a link, a picture.</summary>
public sealed class HotSpot(Action<DrawingContext, Rect, bool> paint, Action? click = null) : Widget
{
    private bool _down;
    public override void Draw(DrawingContext g, Rect r) => paint(g, r, Hover && click is not null);
    public override void Press(Point p, int clicks) => _down = click is not null;

    public override void Release(Point p, bool inside)
    {
        if (_down && inside) click?.Invoke();
        _down = false;
    }
}

public sealed record MenuItem(string Text, Action? Do = null, Func<bool>? Enabled = null, string? Keys = null)
{
    public bool Separator => Text == "-";
    public bool On => !Separator && Do is not null && (Enabled?.Invoke() ?? true);
}

public sealed class Menu(string title, Func<IReadOnlyList<MenuItem>> items)
{
    public string Title { get; } = title;
    public IReadOnlyList<MenuItem> Items() => items();
}

/// <summary>A window: a frame, a title bar, perhaps a menu bar, and widgets in its client area.</summary>
public class Wnd
{
    public string Title = "";
    public Rect Bounds;
    public bool Modal, DialogFrame, Closable = true, Movable = true, MinMax, KeepBottom, Minimized, Maximized;
    public Wnd? Owner;
    public Menu[]? MenuBar;
    public Color Back = W3.White;
    public readonly List<Widget> Widgets = [];
    public Widget? Focused;
    public PushButton? Default;
    public Action<DrawingContext, Rect>? Paint, PaintOver;
    public Action<double>? Tick;
    public Action? Closed;
    /// <summary>Asked before closing; false keeps the window open.</summary>
    public Func<bool>? Closing;
    /// <summary>What Escape does; without it Escape closes the window if it can.</summary>
    public Action? Escape;
    /// <summary>For a window that holds others: the area they live in.</summary>
    public Func<Rect>? ChildArea;
    public string[]? IconArt;
    public Rect Restore;
    public Desktop Desk = null!;

    public const int Edge = 5, TitleH = 18, MenuH = 18;
    public Rect TitleBar => new(Bounds.X + Edge, Bounds.Y + Edge, Bounds.Width - 2 * Edge, TitleH);
    public Rect MenuRect => new(Bounds.X + Edge, TitleBar.Bottom + 1, Bounds.Width - 2 * Edge, MenuBar is null ? 0 : MenuH);

    public Rect Client
    {
        get
        {
            var top = TitleBar.Bottom + 1 + (MenuBar is null ? 0 : MenuH + 1);
            return new Rect(Bounds.X + Edge, top, Bounds.Width - 2 * Edge, Math.Max(0, Bounds.Bottom - Edge - top));
        }
    }

    public Rect ControlBox => new(TitleBar.X, TitleBar.Y, TitleH, TitleH);
    public Rect MaxBox => new(TitleBar.Right - TitleH, TitleBar.Y, TitleH, TitleH);
    public Rect MinBox => new(TitleBar.Right - 2 * TitleH - 1, TitleBar.Y, TitleH, TitleH);
    public bool Hidden => Minimized || Owner is { } o && o.Hidden;

    public T Add<T>(T w) where T : Widget
    {
        w.Win = this;
        Widgets.Add(w);
        if (Focused is null && w.Focusable && w is not ListPane) Focused = w;
        return w;
    }

    public void Close() => Desk.Close(this);

    /// <summary>Sizes the window so its client area is <paramref name="w"/> x <paramref name="h"/>.</summary>
    public Wnd Sized(double w, double h)
    {
        Bounds = new Rect(Bounds.X, Bounds.Y, w + 2 * Edge, h + 2 * Edge + TitleH + 1 + (MenuBar is null ? 0 : MenuH + 1));
        return this;
    }

    public IEnumerable<(Rect Rect, int Index)> MenuTitles()
    {
        if (MenuBar is null) yield break;
        var x = MenuRect.X + 4;
        for (var i = 0; i < MenuBar.Length; i++)
        {
            var w = Txt.Width(MenuBar[i].Title, TF.Sys) + 14;
            yield return (new Rect(x, MenuRect.Y, w, MenuH), i);
            x += w;
        }
    }

    public void Draw(DrawingContext g, bool active)
    {
        var r = Bounds;
        W3.Fill(g, r, W3.Black);
        var frame = DialogFrame ? (active ? W3.Navy : W3.White) : W3.Silver;
        W3.Fill(g, r.Deflate(1), frame);
        W3.Fill(g, r.Deflate(4), W3.Black);
        if (!DialogFrame)
        {
            // The notches in a sizing border.
            var n = TitleH + Edge;
            foreach (var x in new[] { r.X + n, r.Right - n - 1 }) { W3.Fill(g, x, r.Y, 1, 4, W3.Black); W3.Fill(g, x, r.Bottom - 4, 1, 4, W3.Black); }
            foreach (var y in new[] { r.Y + n, r.Bottom - n - 1 }) { W3.Fill(g, r.X, y, 4, 1, W3.Black); W3.Fill(g, r.Right - 4, y, 4, 1, W3.Black); }
        }
        var tb = TitleBar;
        W3.Fill(g, tb, active ? W3.Navy : W3.White);
        var left = tb.X;
        var right = tb.Right;
        if (Closable)
        {
            var box = ControlBox;
            W3.Fill(g, box, W3.Silver);
            W3.Fill(g, box.Right, box.Y, 1, box.Height, W3.Black);
            var bar = new Rect(box.X + 3, box.Y + 7, box.Width - 6, 4);
            W3.Fill(g, bar.Translate(new Vector(1, 1)), W3.Gray);
            W3.Fill(g, bar, W3.White);
            W3.Outline(g, bar, W3.Black);
            left = box.Right + 1;
        }
        if (MinMax)
        {
            foreach (var (b, up) in new[] { (MinBox, false), (MaxBox, true) })
            {
                W3.Fill(g, b.X - 1, b.Y, 1, b.Height, W3.Black);
                W3.Raised(g, b, false);
                if (b == MaxBox && Maximized)
                {
                    W3.Triangle(g, b.Center.X - 0.5, b.Center.Y - 3, 3, true, W3.Black);
                    W3.Triangle(g, b.Center.X - 0.5, b.Center.Y + 3, 3, false, W3.Black);
                }
                else W3.Triangle(g, b.Center.X - 0.5, b.Center.Y, 4, up, W3.Black);
            }
            right = MinBox.X - 1;
        }
        var titleArea = new Rect(left, tb.Y, right - left, tb.Height);
        using (g.PushClip(titleArea))
            Txt.Centre(g, Txt.Fit(Title, TF.Sys, titleArea.Width - 8), titleArea, TF.Sys, active ? W3.White : W3.Black);
        W3.Fill(g, r.X + Edge, tb.Bottom, r.Width - 2 * Edge, 1, W3.Black);

        if (MenuBar is not null)
        {
            var mr = MenuRect;
            W3.Fill(g, mr, W3.White);
            foreach (var (rect, i) in MenuTitles())
            {
                var open = Desk.IsMenuOpen(this, i);
                if (open) W3.Fill(g, rect, W3.Navy);
                Txt.Draw(g, MenuBar[i].Title, rect.X + 7, rect.Y + 1, TF.Sys, open ? W3.White : W3.Black);
            }
            W3.Fill(g, mr.X, mr.Bottom, mr.Width, 1, W3.Black);
        }

        var c = Client;
        using (g.PushClip(c))
        {
            W3.Fill(g, c, Back);
            Paint?.Invoke(g, c);
            foreach (var w in Widgets)
                if (w.Visible) w.Draw(g, w.Abs);
            PaintOver?.Invoke(g, c);
        }
    }
}

/// <summary>A desktop icon: a minimised window, or a program waiting to be opened.</summary>
public sealed class DeskIcon
{
    public string Label = "";
    public string[]? Art;
    public Action<DrawingContext, Rect>? Paint;
    public Action? Open;
    public Wnd? Window;
}

/// <summary>The desktop: the windows on it, the icons along the bottom, the mouse, the menus.</summary>
public sealed class Desktop
{
    public const int W = DosScreen.Width, H = DosScreen.Height;
    public readonly List<Wnd> Windows = [];
    public readonly List<DeskIcon> Icons = [];
    public Wnd? Active;
    public Point Mouse = new(W / 2.0, H / 2.0);
    public bool Busy;
    public double Time;
    public Color Background = W3.Teal;
    public Action Beep = () => { };
    public bool PressedInside { get; private set; }

    private readonly List<(double At, Action Do)> _timers = [];
    private Wnd? _drag;
    private Point _dragFrom;
    private Rect _dragRect;
    private Widget? _pressed;
    private DeskIcon? _selectedIcon;
    private OpenMenu? _menu;

    private sealed class OpenMenu
    {
        public required Wnd Win;
        public int Index;
        public required IReadOnlyList<MenuItem> Items;
        public Rect Drop;
        public int Hover = -1;
    }

    /// <summary>Puts a window on the desktop. Without <paramref name="activate"/> it appears on top but the keyboard stays where it was.</summary>
    public Wnd Open(Wnd w, bool centre = false, bool activate = true)
    {
        w.Desk = this;
        if (centre)
        {
            var area = w.Owner?.ChildArea?.Invoke() ?? new Rect(0, 0, W, H);
            w.Bounds = new Rect(Math.Round(area.X + (area.Width - w.Bounds.Width) / 2), Math.Round(area.Y + (area.Height - w.Bounds.Height) / 2), w.Bounds.Width, w.Bounds.Height);
        }
        w.Bounds = Keep(w, w.Bounds);
        if (w.KeepBottom) { Windows.Insert(0, w); Active ??= w; return w; }
        // Something popping up behind a message box waits its turn for the keyboard.
        if (!w.Modal && TopModal() is { } modal) { Windows.Insert(Windows.IndexOf(modal), w); return w; }
        Windows.Add(w);
        if (activate || Active is null) Active = w;
        return w;
    }

    public void Close(Wnd w, bool force = false)
    {
        if (!Windows.Contains(w)) return;
        if (!force && w.Closing?.Invoke() == false) return;
        foreach (var child in Windows.Where(c => c.Owner == w).ToList()) Close(child, true);
        Windows.Remove(w);
        if (_menu?.Win == w) _menu = null;
        if (_drag == w) _drag = null;
        if (_pressed?.Win == w) _pressed = null;
        w.Closed?.Invoke();
        if (Active == w || Active is not null && !Windows.Contains(Active)) Active = Windows.LastOrDefault(x => !x.Hidden);
    }

    public void Activate(Wnd w)
    {
        if (w.Hidden) return;
        if (!w.KeepBottom) { Windows.Remove(w); Windows.Add(w); }
        Active = w;
    }

    public void After(double seconds, Action a) => _timers.Add((Time + seconds, a));

    public Wnd? TopModal() => Windows.LastOrDefault(w => w.Modal && !w.Hidden);
    public Wnd? TopAt(Point p) => Windows.LastOrDefault(w => !w.Hidden && w.Bounds.Contains(p));
    public bool IsMenuOpen(Wnd w, int i) => _menu is { } m && m.Win == w && m.Index == i;
    private bool LooksActive(Wnd w) => Active is { } a && (a == w || a.Owner == w);

    public void Minimize(Wnd w)
    {
        w.Minimized = true;
        if (Active is { } a && a.Hidden) Active = Windows.LastOrDefault(x => !x.Hidden);
    }

    public void ToggleMax(Wnd w)
    {
        if (w.Maximized) { w.Maximized = false; MoveTo(w, w.Restore); }
        else
        {
            w.Restore = w.Bounds;
            w.Maximized = true;
            MoveTo(w, new Rect(-1, -1, W + 2, H + 2));
        }
    }

    /// <summary>Moves a window; the windows inside it go with it and stay inside.</summary>
    public void MoveTo(Wnd w, Rect to)
    {
        var dx = to.X - w.Bounds.X;
        var dy = to.Y - w.Bounds.Y;
        w.Bounds = to;
        foreach (var c in Windows.Where(c => c.Owner == w))
            c.Bounds = Keep(c, c.Bounds.Translate(new Vector(dx, dy)));
    }

    public Rect Keep(Wnd w, Rect r)
    {
        if (w.Owner?.ChildArea?.Invoke() is { } area)
        {
            var x = Math.Clamp(r.X, area.X, Math.Max(area.X, area.Right - r.Width));
            var y = Math.Clamp(r.Y, area.Y, Math.Max(area.Y, area.Bottom - r.Height));
            return new Rect(x, y, r.Width, r.Height);
        }
        // On the desktop, keep a piece of the title bar where it can be grabbed.
        return new Rect(Math.Clamp(r.X, 40 - r.Width, W - 40), Math.Clamp(r.Y, 0, H - 20), r.Width, r.Height);
    }

    public void Update(double dt)
    {
        Time += dt;
        for (var i = 0; i < _timers.Count; i++)
        {
            if (_timers[i].At > Time) continue;
            var a = _timers[i].Do;
            _timers.RemoveAt(i--);
            a();
        }
        foreach (var w in Windows.ToList()) w.Tick?.Invoke(dt);
    }

    // ----- Drawing -----

    private IEnumerable<(DeskIcon Icon, Rect Rect)> IconSlots()
    {
        var all = Icons.Concat(Windows.Where(w => w.Minimized && w.Owner is null).Select(w => new DeskIcon { Label = w.Title, Art = w.IconArt, Window = w })).ToList();
        for (var i = 0; i < all.Count; i++) yield return (all[i], new Rect(4 + i * 96, H - 52, 92, 50));
    }

    public void Draw(DrawingContext g)
    {
        W3.Fill(g, new Rect(0, 0, W, H), Background);
        foreach (var (icon, r) in IconSlots())
        {
            var pic = new Rect(r.X + (r.Width - 32) / 2, r.Y, 32, 32);
            if (icon.Paint is { } paint) paint(g, pic);
            else if (icon.Art is { } art) Art.Draw(g, art, pic.X, pic.Y, 2);
            var selected = icon == _selectedIcon || icon.Window is not null && icon.Window == _selectedIcon?.Window;
            var label = Txt.Fit(icon.Label, TF.Small, r.Width);
            var lw = Txt.Width(label, TF.Small);
            var lr = new Rect(Math.Round(r.X + (r.Width - lw) / 2) - 2, r.Y + 34, lw + 4, 14);
            if (selected) W3.Fill(g, lr, W3.Navy);
            Txt.Draw(g, label, lr.X + 2, lr.Y, TF.Small, selected ? W3.White : W3.Black);
        }
        foreach (var w in Windows)
            if (!w.Hidden) w.Draw(g, LooksActive(w));
        if (_drag is not null)
        {
            // Windows move as an outline, and land when the button is let go.
            var o = _dragRect;
            g.DrawRectangle(null, new Pen(W3.B(W3.Gray), 3), o.Deflate(1.5));
            g.DrawRectangle(null, new Pen(W3.B(W3.White), 1, new DashStyle([2, 2], 0)), o.Deflate(1.5));
        }
        if (_menu is { } m) DrawMenu(g, m);
        if (Busy) Art.Draw(g, Art.Hourglass, Mouse.X - 5, Mouse.Y - 7);
        else Art.Draw(g, Art.Arrow, Mouse.X, Mouse.Y);
    }

    private static double ItemH(MenuItem i) => i.Separator ? 8 : 16;

    private void DrawMenu(DrawingContext g, OpenMenu m)
    {
        var d = m.Drop;
        W3.Fill(g, d.Translate(new Vector(2, 2)), W3.Black);
        W3.Fill(g, d, W3.White);
        W3.Outline(g, d, W3.Black);
        var y = d.Y + 2;
        for (var i = 0; i < m.Items.Count; i++)
        {
            var item = m.Items[i];
            var h = ItemH(item);
            if (item.Separator) W3.Fill(g, d.X + 1, y + 3, d.Width - 2, 1, W3.Black);
            else
            {
                var hot = i == m.Hover && item.On;
                if (hot) W3.Fill(g, d.X + 1, y, d.Width - 2, h, W3.Navy);
                var c = hot ? W3.White : item.On ? W3.Black : W3.Gray;
                Txt.Draw(g, item.Text, d.X + 14, y, TF.Sys, c);
                if (item.Keys is { } k) Txt.Draw(g, k, d.Right - 10 - Txt.Width(k, TF.Sys), y, TF.Sys, c);
            }
            y += h;
        }
    }

    // ----- Input -----

    private void ShowMenu(Wnd w, int index)
    {
        var items = index < 0
            ? new List<MenuItem>
            {
                new("Restore", w.Maximized ? () => ToggleMax(w) : null), new("Move"), new("Size"),
                new("Minimize", w.MinMax ? () => Minimize(w) : null), new("Maximize", w.MinMax && !w.Maximized ? () => ToggleMax(w) : null),
                new("-"), new("Close", w.Closable ? () => Close(w) : null, Keys: "Alt+F4"),
            }
            : w.MenuBar![index].Items();
        var at = index < 0 ? new Point(w.ControlBox.X, w.ControlBox.Bottom) : w.MenuTitles().First(t => t.Index == index).Rect.BottomLeft + new Vector(0, 1);
        var width = items.Max(i => Txt.Width(i.Text, TF.Sys) + (i.Keys is null ? 0 : Txt.Width(i.Keys, TF.Sys) + 24)) + 30;
        var height = items.Sum(ItemH) + 4;
        var x = Math.Min(at.X, W - width - 3);
        _menu = new OpenMenu { Win = w, Index = index, Items = items, Drop = new Rect(x, at.Y, width, height) };
    }

    private int MenuItemAt(OpenMenu m, Point p)
    {
        if (!m.Drop.Contains(p)) return -1;
        var y = m.Drop.Y + 2;
        for (var i = 0; i < m.Items.Count; i++)
        {
            var h = ItemH(m.Items[i]);
            if (p.Y >= y && p.Y < y + h) return i;
            y += h;
        }
        return -1;
    }

    private void Choose(OpenMenu m, int i)
    {
        if (i < 0 || i >= m.Items.Count || !m.Items[i].On) return;
        _menu = null;
        m.Items[i].Do!();
    }

    public void Press(Point p, int clicks)
    {
        Mouse = p;
        if (_menu is { } m)
        {
            if (m.Drop.Contains(p)) { Choose(m, MenuItemAt(m, p)); return; }
            _menu = null;
            var title = m.Win.MenuTitles().FirstOrDefault(t => t.Rect.Contains(p));
            if (title.Rect.Width > 0 && title.Index != m.Index) ShowMenu(m.Win, title.Index);
            return;
        }
        var w = TopAt(p);
        var modal = TopModal();
        if (modal is not null && w != modal) { Beep(); if (w is not null) Activate(modal); return; }
        if (w is null)
        {
            var hit = IconSlots().FirstOrDefault(s => s.Rect.Contains(p));
            _selectedIcon = hit.Icon;
            if (hit.Icon is { } icon && clicks >= 2)
            {
                if (icon.Window is { } iw) { iw.Minimized = false; Activate(iw); }
                else icon.Open?.Invoke();
                _selectedIcon = null;
            }
            return;
        }
        _selectedIcon = null;
        // Clicking the frame around child windows (its menus, its toolbar) leaves the child active, as in MDI.
        if (!(w.KeepBottom && Active?.Owner == w)) Activate(w);
        if (w.Closable && w.ControlBox.Contains(p)) { if (clicks >= 2) Close(w); else ShowMenu(w, -1); return; }
        if (w.MinMax && w.MinBox.Contains(p)) { Minimize(w); return; }
        if (w.MinMax && w.MaxBox.Contains(p)) { ToggleMax(w); return; }
        if (w.TitleBar.Contains(p))
        {
            if (clicks >= 2 && w.MinMax) ToggleMax(w);
            else if (w.Movable && !w.Maximized) { _drag = w; _dragFrom = p; _dragRect = w.Bounds; }
            return;
        }
        if (w.MenuBar is not null && w.MenuRect.Contains(p))
        {
            var title = w.MenuTitles().FirstOrDefault(t => t.Rect.Contains(p));
            if (title.Rect.Width > 0) ShowMenu(w, title.Index);
            return;
        }
        var c = w.Client;
        if (!c.Contains(p)) return;
        for (var i = w.Widgets.Count - 1; i >= 0; i--)
        {
            var widget = w.Widgets[i];
            if (!widget.Visible || !widget.Abs.Contains(p)) continue;
            if (!widget.Enabled) return;
            if (widget.Focusable) w.Focused = widget;
            _pressed = widget;
            PressedInside = true;
            widget.Press(p - widget.Abs.TopLeft, clicks);
            return;
        }
    }

    public void Move(Point p)
    {
        Mouse = p;
        if (_drag is { } d)
        {
            var to = d.Bounds.Translate(p - _dragFrom);
            _dragRect = Keep(d, to);
        }
        if (_pressed is { } w)
        {
            PressedInside = w.Abs.Contains(p);
            w.Drag(p - w.Abs.TopLeft);
        }
        if (_menu is { } m)
        {
            m.Hover = MenuItemAt(m, p);
            var title = m.Win.MenuTitles().FirstOrDefault(t => t.Rect.Contains(p));
            if (title.Rect.Width > 0 && title.Index != m.Index && m.Index >= 0) ShowMenu(m.Win, title.Index);
        }
    }

    public void Release(Point p)
    {
        Mouse = p;
        if (_drag is { } d)
        {
            _drag = null;
            if (_dragRect != d.Bounds) MoveTo(d, _dragRect);
        }
        if (_pressed is { } w)
        {
            _pressed = null;
            w.Release(p - w.Abs.TopLeft, w.Abs.Contains(p));
        }
    }

    public void Wheel(Point p, double dy)
    {
        if (TopAt(p) is not { } w || (TopModal() is { } m && m != w)) return;
        for (var i = w.Widgets.Count - 1; i >= 0; i--)
            if (w.Widgets[i].Visible && w.Widgets[i].Abs.Contains(p)) { w.Widgets[i].Wheel(dy); return; }
    }

    /// <summary>A key for the active window. False if nothing wanted it.</summary>
    public bool OnKey(Key k, KeyModifiers mods)
    {
        if (_menu is { } m)
        {
            switch (k)
            {
                case Key.Escape: _menu = null; break;
                case Key.Up or Key.Down:
                    var step = k == Key.Up ? -1 : 1;
                    var i = m.Hover;
                    for (var n = 0; n < m.Items.Count; n++)
                    {
                        i = ((i + step) % m.Items.Count + m.Items.Count) % m.Items.Count;
                        if (m.Items[i].On) break;
                    }
                    m.Hover = i;
                    break;
                case Key.Left or Key.Right when m.Index >= 0 && m.Win.MenuBar is { } bar:
                    ShowMenu(m.Win, ((m.Index + (k == Key.Left ? -1 : 1)) % bar.Length + bar.Length) % bar.Length);
                    break;
                case Key.Enter: Choose(m, m.Hover); break;
            }
            return true;
        }
        if (Active is not { } w) return false;
        if (k == Key.Tab)
        {
            var order = w.Widgets.Where(x => x.Focusable && x.Visible && x.Enabled).ToList();
            if (order.Count == 0) return true;
            var at = w.Focused is null ? -1 : order.IndexOf(w.Focused);
            var step = mods.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            w.Focused = order[((at + step) % order.Count + order.Count) % order.Count];
            return true;
        }
        if (w.Focused is { Visible: true } f && f.Enabled && f.OnKey(k, mods)) return true;
        if (k == Key.Enter && w.Default is { } d && d.Enabled) { d.Click(); return true; }
        if (k == Key.Escape)
        {
            if (w.Escape is { } esc) { esc(); return true; }
            if (w.Closable && !w.KeepBottom) { Close(w); return true; }
        }
        return false;
    }

    public void Text(string s)
    {
        if (_menu is null && Active?.Focused is { Visible: true } f && f.Enabled) f.Text(s);
    }

    // ----- Message boxes -----

    public enum Icon { None, Info, Warning, Stop, Question }

    /// <summary>A modal message with an icon and some buttons. The first button is the default.</summary>
    public Wnd MessageBox(string title, string text, Icon icon = Icon.Info, params (string Label, Action? Do)[] buttons)
    {
        if (buttons.Length == 0) buttons = [("OK", null)];
        var lines = Txt.Wrap(text, TF.Sys, 300);
        var textW = lines.Max(l => Txt.Width(l, TF.Sys));
        var iconW = icon == Icon.None ? 0 : 48;
        var buttonsW = buttons.Length * 76 - 10;
        var w = Math.Max(Math.Max(textW + iconW + 36, buttonsW + 40), 200);
        var textH = Math.Max(lines.Count * 16, icon == Icon.None ? 0 : 32);
        var h = 18 + textH + 18 + 26 + 12;
        var box = new Wnd { Title = title, Modal = true, DialogFrame = true, Back = W3.White }.Sized(w, h);
        box.Paint = (g, c) =>
        {
            var tx = c.X + 18 + iconW;
            if (icon != Icon.None) DrawIcon(g, icon, c.X + 18, c.Y + 16);
            var ty = c.Y + 18 + (textH - lines.Count * 16) / 2.0;
            foreach (var line in lines) { Txt.Draw(g, line, tx, ty, TF.Sys, W3.Black); ty += 16; }
        };
        var x = (w - buttonsW) / 2;
        foreach (var (label, act) in buttons)
        {
            var b = box.Add(new PushButton(label, () => { box.Close(); act?.Invoke(); }) { R = new Rect(x, h - 38, 66, 26) });
            box.Default ??= b;
            x += 76;
        }
        var cancel = buttons[^1].Do;
        box.Escape = () => { box.Close(); if (buttons.Length > 1) cancel?.Invoke(); else buttons[0].Do?.Invoke(); };
        Beep();
        return Open(box, centre: true);
    }

    public static void DrawIcon(DrawingContext g, Icon icon, double x, double y)
    {
        var centre = new Point(x + 16, y + 16);
        switch (icon)
        {
            case Icon.Info or Icon.Question:
                g.DrawEllipse(W3.B(W3.White), new Pen(W3.B(W3.Black), 2), centre, 14, 14);
                if (icon == Icon.Info) { W3.Fill(g, x + 14, y + 7, 4, 4, W3.Navy); W3.Fill(g, x + 14, y + 13, 4, 12, W3.Navy); W3.Fill(g, x + 11, y + 23, 10, 2, W3.Navy); }
                else Txt.Centre(g, "?", new Rect(x, y - 1, 32, 32), TF.Big, W3.Navy);
                break;
            case Icon.Warning:
                g.DrawEllipse(W3.B(W3.Yellow), new Pen(W3.B(W3.Black), 2), centre, 14, 14);
                W3.Fill(g, x + 14, y + 6, 4, 13, W3.Black);
                W3.Fill(g, x + 14, y + 22, 4, 4, W3.Black);
                break;
            case Icon.Stop:
                var oct = new PolylineGeometry(Enumerable.Range(0, 8).Select(i => centre + new Vector(Math.Cos(Math.PI / 8 + i * Math.PI / 4), Math.Sin(Math.PI / 8 + i * Math.PI / 4)) * 15), true);
                g.DrawGeometry(W3.B(W3.Red), new Pen(W3.B(W3.Black), 1.5), oct);
                Txt.Centre(g, "STOP", new Rect(x, y, 32, 32), TF.SmallBold, W3.White);
                break;
        }
    }
}
