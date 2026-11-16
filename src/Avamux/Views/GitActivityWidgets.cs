using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avamux.Core.GitActivity;

namespace Avamux.Views;

/// <summary>Colours for activity figures, from the theme: an empty cell is the cell colour, the busiest the accent.</summary>
public static class Heat
{
    /// <summary>0 → nothing, 1 → the most. A square-root scale, so a few big repositories do not wash out the rest.</summary>
    public static Color At(double t)
    {
        var cell = Ui.Color("cell");
        if (t <= 0) return cell;
        var accent = Ui.Color("accent");
        var k = 0.16 + 0.84 * Math.Sqrt(Math.Clamp(t, 0, 1));
        return Color.FromRgb((byte)(cell.R + (accent.R - cell.R) * k), (byte)(cell.G + (accent.G - cell.G) * k), (byte)(cell.B + (accent.B - cell.B) * k));
    }

    public static string Number(double v) =>
        v >= 1_000_000 ? $"{v / 1_000_000:0.#}M" : v >= 10_000 ? $"{v / 1000:0}k" : v >= 1000 ? $"{v / 1000:0.#}k" : v.ToString("0", CultureInfo.CurrentCulture);
}

/// <summary>
/// A heatmap: one row per key of one dimension, one column per key of another, each cell shaded by its value, with
/// the row's total as a bar at the end. Hovering says what a cell is; clicking narrows the view to it.
/// </summary>
public sealed class HeatmapControl : Control
{
    private ActivityMatrix? _m;
    private Func<ActivityDimension, string, string> _display = (_, k) => k;
    private (int Row, int Col) _hover = (-1, -1);
    private double _cell = 16;
    private const double RowHeight = 20, Header = 30, TotalsWidth = 96, Gap = 1.5;
    public double LabelWidth { get; set; } = 230;
    /// <summary>The width to fit into when the space is unlimited (inside a sideways scroller): wider only when the columns need it.</summary>
    public double FitWidth { get; set; }

    /// <summary>A cell was clicked: row key, column key.</summary>
    public event Action<string, string>? CellClicked;
    /// <summary>A row's label was clicked.</summary>
    public event Action<string>? RowClicked;
    /// <summary>What is under the pointer, for a status line (empty when nothing).</summary>
    public event Action<string>? Hovered;
    /// <summary>Builds the right-click menu of a row.</summary>
    public Func<string, ContextMenu?>? RowMenu { get; set; }

    public HeatmapControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        AttachedToVisualTree += (_, _) => Ui.ThemeChanged += InvalidateVisual;
        DetachedFromVisualTree += (_, _) => Ui.ThemeChanged -= InvalidateVisual;
    }

    public void Set(ActivityMatrix matrix, Func<ActivityDimension, string, string> display)
    {
        _m = matrix;
        _display = display;
        _hover = (-1, -1);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        if (_m is null) return new Size(0, 0);
        var cols = Math.Max(1, _m.Columns.Count);
        var width = !double.IsInfinity(available.Width) ? available.Width : FitWidth > 0 ? FitWidth : LabelWidth + TotalsWidth + cols * 18;
        _cell = Math.Clamp((width - LabelWidth - TotalsWidth) / cols, 3, 42);
        return new Size(LabelWidth + TotalsWidth + cols * _cell, Header + _m.Rows.Count * RowHeight + 4);
    }

    private FormattedText Text(string s, double size, IBrush brush, double maxWidth, FontWeight weight = FontWeight.Normal)
    {
        var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1,
        };
        return t;
    }

    public override void Render(DrawingContext ctx)
    {
        if (_m is null) return;
        var dim = Ui.Tk("dim");
        var text = Ui.Tk("text");
        var cols = _m.Columns.Count;
        // Column headings: as many as fit without running into each other.
        var every = Math.Max(1, (int)Math.Ceiling(46 / _cell));
        for (var c = 0; c < cols; c += every)
        {
            // Leave room for the totals heading.
            if (c > 0 && LabelWidth + c * _cell + 40 > LabelWidth + cols * _cell + 4) break;
            var label = ShortColumn(_m.ColumnDimension, _m.Columns[c]);
            ctx.DrawText(Text(label, 10.5, dim, Math.Max(_cell * every, 40)), new Point(LabelWidth + c * _cell, Header - 18));
        }
        ctx.DrawText(Text(ActivityEngine.Label(_m.Measure), 10.5, dim, TotalsWidth - 8), new Point(LabelWidth + cols * _cell + 8, Header - 18));
        var rowMax = _m.Rows.Count == 0 ? 0 : _m.Rows.Max(r => _m.RowTallies[r].Value(_m.Measure));
        var bar = Ui.Tk("accent");
        for (var r = 0; r < _m.Rows.Count; r++)
        {
            var y = Header + r * RowHeight;
            if (r == _hover.Row) ctx.FillRectangle(Ui.Tk("cellSel"), new Rect(0, y, Bounds.Width, RowHeight));
            ctx.DrawText(Text(_display(_m.RowDimension, _m.Rows[r]), 12, text, LabelWidth - 12), new Point(4, y + 2));
            for (var c = 0; c < cols; c++)
            {
                var v = _m.Values[r, c];
                var rect = new Rect(LabelWidth + c * _cell + Gap / 2, y + Gap / 2, Math.Max(1, _cell - Gap), RowHeight - Gap);
                ctx.FillRectangle(new SolidColorBrush(Heat.At(_m.Max > 0 ? v / _m.Max : 0)), rect, Ui.Theme.Radius > 0 ? 2 : 0);
                if (r == _hover.Row && c == _hover.Col) ctx.DrawRectangle(new Pen(Ui.Tk("textStrong"), 1.5), rect);
                else if (_cell >= 26 && v > 0)
                {
                    var t = _m.Max > 0 ? v / _m.Max : 0;
                    var label = Text(Heat.Number(v), 9.5, t > 0.55 ? Ui.OnAccent : dim, _cell - 2);
                    ctx.DrawText(label, new Point(rect.X + (rect.Width - label.Width) / 2, y + 4));
                }
            }
            var total = _m.RowTallies[_m.Rows[r]].Value(_m.Measure);
            var x = LabelWidth + cols * _cell + 8;
            var w = rowMax > 0 ? (TotalsWidth - 50) * total / rowMax : 0;
            ctx.FillRectangle(bar, new Rect(x, y + 6, Math.Max(1, w), RowHeight - 12));
            ctx.DrawText(Text(Heat.Number(total), 10.5, dim, 48), new Point(x + w + 4, y + 3));
        }
    }

    private static string ShortColumn(ActivityDimension d, string key) => d switch
    {
        ActivityDimension.Day => DateTime.TryParse(key, CultureInfo.InvariantCulture, out var day) ? day.ToString("d MMM", CultureInfo.CurrentCulture) : key,
        ActivityDimension.Week => DateTime.TryParse(key, CultureInfo.InvariantCulture, out var w) ? w.ToString("d MMM", CultureInfo.CurrentCulture) : key,
        ActivityDimension.Month => DateTime.TryParse(key + "-01", CultureInfo.InvariantCulture, out var m) ? m.ToString("MMM yy", CultureInfo.CurrentCulture) : key,
        _ => key,
    };

    private (int Row, int Col) Hit(Point p)
    {
        if (_m is null || p.Y < Header) return (-1, -1);
        var r = (int)((p.Y - Header) / RowHeight);
        if (r < 0 || r >= _m.Rows.Count) return (-1, -1);
        var c = p.X < LabelWidth ? -1 : (int)((p.X - LabelWidth) / _cell);
        return (r, c >= _m.Columns.Count ? -2 : c);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = Hit(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        InvalidateVisual();
        if (_m is null || hit.Row < 0) { Hovered?.Invoke(""); return; }
        var row = _display(_m.RowDimension, _m.Rows[hit.Row]);
        var tally = _m.RowTallies[_m.Rows[hit.Row]];
        if (hit.Col >= 0)
        {
            var v = _m.Values[hit.Row, hit.Col];
            Hovered?.Invoke($"{row}  ·  {_display(_m.ColumnDimension, _m.Columns[hit.Col])}:  {v:N0} {ActivityEngine.Label(_m.Measure).ToLowerInvariant()}   (click to look at just this)");
        }
        else Hovered?.Invoke($"{row}:  {tally.Commits:N0} commits · +{tally.Added:N0} −{tally.Deleted:N0} lines · {tally.People?.Count ?? 0} contributors · {Pct(tally.AiCommits, tally.Commits)} with AI   (click to look at just this; right-click for more)");
    }

    internal static string Pct(int part, int whole) => whole == 0 ? "0%" : $"{100.0 * part / whole:0}%";

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = (-1, -1);
        InvalidateVisual();
        Hovered?.Invoke("");
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_m is null) return;
        var (r, c) = Hit(e.GetPosition(this));
        if (r < 0) return;
        var key = _m.Rows[r];
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (RowMenu?.Invoke(key) is { } menu) { ContextMenu = menu; menu.Open(this); }
            e.Handled = true;
            return;
        }
        if (c >= 0) CellClicked?.Invoke(key, _m.Columns[c]); else RowClicked?.Invoke(key);
        e.Handled = true;
    }
}

/// <summary>A small line of weekly values.</summary>
public sealed class MiniSpark : Control
{
    private double[] _values = [];
    public MiniSpark()
    {
        Width = 72; Height = 18;
        AttachedToVisualTree += (_, _) => Ui.ThemeChanged += InvalidateVisual;
        DetachedFromVisualTree += (_, _) => Ui.ThemeChanged -= InvalidateVisual;
    }
    public double[] Values { get => _values; set { _values = value; InvalidateVisual(); } }

    public override void Render(DrawingContext ctx)
    {
        if (_values.Length < 2) return;
        var max = Math.Max(1e-9, _values.Max());
        var w = Bounds.Width; var h = Bounds.Height;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var i = 0; i < _values.Length; i++)
            {
                var p = new Point(i * w / (_values.Length - 1), h - 1 - (h - 3) * _values[i] / max);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Ui.Tk("accent"), 1.4), geo);
    }
}

/// <summary>A card listing the top entries of one dimension, each with a bar, its share, a weekly line and its trend.</summary>
public static class Leaderboard
{
    public static Border Build(string title, string? note, IReadOnlyList<TopEntry> entries, ActivityMeasure measure, Action<TopEntry> onClick, Func<TopEntry, ContextMenu?> menu,
        Func<TopEntry, Control?>? badge = null, double width = 470)
    {
        var list = new StackPanel { Spacing = 1 };
        var max = entries.Count == 0 ? 1 : Math.Max(1e-9, entries.Max(e => e.Value));
        var rank = 0;
        foreach (var e in entries)
        {
            rank++;
            var track = new Border { Height = 6, CornerRadius = Ui.R(3), Background = Ui.Tk("cell"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
            var fill = new Border { Height = 6, CornerRadius = Ui.R(3), Background = Ui.Tk("accent"), HorizontalAlignment = HorizontalAlignment.Left, Width = 1 };
            track.Child = fill;
            track.SizeChanged += (_, s) => fill.Width = Math.Max(2, s.NewSize.Width * e.Value / max);
            var name = new TextBlock { Text = e.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new DockPanel { LastChildFill = true };
            if (badge?.Invoke(e) is { } b) { b.Margin = new Thickness(6, 0, 0, 0); nameRow.Children.Add(b.Dock(Dock.Right)); }
            nameRow.Children.Add(name);
            var trend = new TextBlock
            {
                Text = e.Trend switch { > 0.25 => "▲", < -0.25 => "▼", _ => "■" }, FontSize = 10, Width = 14, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ui.Tk(e.Trend switch { > 0.25 => "ok", < -0.25 => "error", _ => "faint" }),
            };
            ToolTip.SetTip(trend, e.Trend switch
            {
                > 0.25 => $"Picking up: {e.Trend * 100:+0}% in the second half of the period", < -0.25 => $"Slowing down: {e.Trend * 100:0}% in the second half of the period",
                _ => "About as busy in both halves of the period",
            });
            var ai = new TextBlock
            {
                Text = e.Tally.Commits == 0 ? "" : HeatmapControl.Pct(e.Tally.AiCommits, e.Tally.Commits), FontSize = 10.5, Width = 34, TextAlignment = TextAlignment.Right,
                Foreground = e.Tally.AiCommits > 0 ? Ui.Tk("purple") : Ui.Tk("faint"), VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(ai, $"{e.Tally.AiCommits:N0} of {e.Tally.Commits:N0} commits had AI help ({e.Tally.AgentCommits:N0} by an AI agent)");
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("22,*,64,76,14,36"), Background = Brushes.Transparent, Margin = new Thickness(0, 1), Cursor = new Cursor(StandardCursorType.Hand),
                Children =
                {
                    new TextBlock { Text = rank.ToString(), FontSize = 11, Foreground = Ui.Tk("faint"), VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 10, 2), Children = { nameRow, track } }.At(0, 1),
                    new TextBlock { Text = Heat.Number(e.Value), FontSize = 12, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }.At(0, 2),
                    new MiniSpark { Values = e.Weekly, VerticalAlignment = VerticalAlignment.Center }.At(0, 3),
                    trend.At(0, 4),
                    ai.At(0, 5),
                },
            };
            ToolTip.SetTip(row, $"{e.Name}\n{e.Tally.Commits:N0} commits · +{e.Tally.Added:N0} / −{e.Tally.Deleted:N0} lines · {e.Tally.Files:N0} files · {e.Tally.People?.Count ?? 0} contributors\n" +
                                $"{e.Share * 100:0.#}% of all {ActivityEngine.Label(measure).ToLowerInvariant()} · last {e.Tally.Last:ddd d MMM HH:mm}\nClick to look at just this; right-click for more.");
            row.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(row).Properties.IsRightButtonPressed)
                {
                    if (menu(e) is { } m) { row.ContextMenu = m; m.Open(row); }
                }
                else onClick(e);
                args.Handled = true;
            };
            row.PointerEntered += (_, _) => row.Background = Ui.Tk("cellSel");
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            list.Children.Add(row);
        }
        if (entries.Count == 0) list.Children.Add(Ui.Text("Nothing in this view.", "dim", 12));
        var head = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                new TextBlock { Text = "AI", FontSize = 10.5, Foreground = Ui.Tk("faint"), Width = 36, TextAlignment = TextAlignment.Right }.Dock(Dock.Right),
                new TextBlock { Text = "trend", FontSize = 10.5, Foreground = Ui.Tk("faint"), Width = 90, TextAlignment = TextAlignment.Right }.Dock(Dock.Right),
                new StackPanel
                {
                    Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13.5 }, new TextBlock { Text = note ?? "", FontSize = 11, Foreground = Ui.Dim, IsVisible = note is not null } },
                },
            },
        };
        return new Border { Classes = { "card" }, Width = width, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14, 12), Child = new StackPanel { Children = { head, list } } };
    }
}

/// <summary>A figure with a caption: the tiles along the top of Git Activity.</summary>
public static class StatTile
{
    public static Border Build(string value, string caption, string? detail = null, string? color = null, string? tip = null)
    {
        var b = new Border
        {
            Classes = { "card" }, MinWidth = 132, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 10),
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = value, FontSize = 21, FontWeight = FontWeight.SemiBold, Foreground = color is null ? Ui.Tk("textStrong") : Ui.Tk(color) },
                    new TextBlock { Text = caption, FontSize = 11.5, Foreground = Ui.Dim },
                    new TextBlock { Text = detail ?? "", FontSize = 11, Foreground = Ui.Tk("faint"), IsVisible = detail is not null, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 },
                },
            },
        };
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }
}
