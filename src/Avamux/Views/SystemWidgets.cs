using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Avamux.Views;

/// <summary>Numbers as people read them: 1.4 GB, 12 MB/s, 3d 4h.</summary>
public static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:0} B";
        var u = 0;
        while (bytes >= 1024 && u < Units.Length - 1) { bytes /= 1024; u++; }
        return bytes >= 100 ? $"{bytes:0} {Units[u]}" : bytes >= 10 ? $"{bytes:0.0} {Units[u]}" : $"{bytes:0.00} {Units[u]}";
    }

    public static string Rate(double perSecond) => Bytes(perSecond) + "/s";

    public static string Percent(double fraction) => fraction >= 0.995 ? "100%" : fraction < 0.1 ? $"{fraction * 100:0.0}%" : $"{fraction * 100:0}%";

    public static string Count(long n) => n.ToString("N0");

    public static string Span(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
}

/// <summary>
/// One timer for everything that moves (the scan bars and the spinning platter): it ticks only while some
/// visible control wants to be animated, and stops by itself otherwise.
/// </summary>
public static class Animator
{
    private static readonly HashSet<Control> Wanting = [];
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    public static double Seconds => Environment.TickCount64 / 1000.0;

    static Animator() => Timer.Tick += (_, _) =>
    {
        foreach (var c in Wanting.ToList())
            if (c.IsEffectivelyVisible && TopLevel.GetTopLevel(c) is Window { WindowState: not WindowState.Minimized }) c.InvalidateVisual();
        if (Wanting.Count == 0) Timer.Stop();
    };

    public static void Set(Control c, bool animate)
    {
        if (animate ? Wanting.Add(c) : Wanting.Remove(c))
        {
            if (Wanting.Count > 0 && !Timer.IsEnabled) Timer.Start();
            c.InvalidateVisual();
        }
    }

    /// <summary>Animates while <paramref name="c"/> is on screen and <see cref="Set"/> asked for it.</summary>
    public static void Track(Control c, Func<bool> wants)
    {
        c.AttachedToVisualTree += (_, _) => Set(c, wants());
        c.DetachedFromVisualTree += (_, _) => Set(c, false);
    }
}

/// <summary>A small line chart of recent values, 0 at the bottom; the top is 1 or the largest value shown.</summary>
public sealed class Sparkline : Control
{
    private readonly List<double[]> _series = [];
    private readonly List<IBrush> _brushes = [];
    public double? Max { get; set; } = 1;

    public Sparkline() { Height = 34; ClipToBounds = true; }

    public void Set(params (double[] Values, IBrush Brush)[] series)
    {
        _series.Clear(); _brushes.Clear();
        foreach (var (v, b) in series) { _series.Add(v); _brushes.Add(b); }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.DrawLine(new Pen(Ui.Tk("lineSoft"), 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
        var max = Max ?? Math.Max(1e-9, _series.SelectMany(s => s).DefaultIfEmpty(0).Max());
        for (var s = 0; s < _series.Count; s++)
        {
            var values = _series[s];
            if (values.Length < 2) continue;
            var step = w / (SystemPage.HistoryLength - 1);
            var x0 = w - (values.Length - 1) * step;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var p = new Point(x0 + i * step, h - 1 - Math.Clamp(values[i] / max, 0, 1) * (h - 3));
                    if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            if (s == 0)
            {
                // A faint fill under the first series.
                var fill = new StreamGeometry();
                using (var g = fill.Open())
                {
                    g.BeginFigure(new Point(x0, h), true);
                    for (var i = 0; i < values.Length; i++) g.LineTo(new Point(x0 + i * step, h - 1 - Math.Clamp(values[i] / max, 0, 1) * (h - 3)));
                    g.LineTo(new Point(w, h));
                    g.EndFigure(true);
                }
                var c = (_brushes[s] as ISolidColorBrush)?.Color ?? Colors.Gray;
                ctx.DrawGeometry(new SolidColorBrush(c, 0.16), null, fill);
            }
            ctx.DrawGeometry(null, new Pen(_brushes[s], 1.6, lineJoin: PenLineJoin.Round), geo);
        }
    }
}

/// <summary>A horizontal bar filled to a fraction; amber past <see cref="WarnAt"/>.</summary>
public sealed class UsageBar : Control
{
    private double _value;
    public double WarnAt { get; set; } = 0.9;

    public UsageBar() { Height = 8; }

    public double Value
    {
        get => _value;
        set { _value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        var radius = Ui.Theme.Radius <= 0 ? 0 : r.Height / 2;
        ctx.DrawRectangle(Ui.Tk("bgDeep"), new Pen(Ui.Tk("lineSoft"), 1), r, radius, radius);
        if (_value <= 0) return;
        var fill = new Rect(0, 0, Math.Max(r.Height, r.Width * _value), r.Height);
        ctx.DrawRectangle(Ui.Tk(_value >= WarnAt ? "warn" : "accent"), null, fill, radius, radius);
    }
}

/// <summary>
/// The scan's progress: a bar filled as far as the scan has got (when that is known), with stripes that move
/// while it runs and stand still while it is paused; with no total to measure against, a band sweeps across.
/// </summary>
public sealed class ScanBar : Control
{
    private double? _fraction;
    private bool _running;

    public ScanBar()
    {
        Height = 10;
        Animator.Track(this, () => _running);
    }

    public void Set(double? fraction, bool running)
    {
        _fraction = fraction;
        if (_running != running) { _running = running; Animator.Set(this, running && TopLevel.GetTopLevel(this) is not null); }
        InvalidateVisual();
    }

    

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        var radius = Ui.Theme.Radius <= 0 ? 0 : r.Height / 2;
        ctx.DrawRectangle(Ui.Tk("bgDeep"), new Pen(Ui.Tk("lineSoft"), 1), r, radius, radius);
        var t = Animator.Seconds;
        Rect fill;
        if (_fraction is { } f) fill = new Rect(0, 0, Math.Max(r.Height, r.Width * f), r.Height);
        else
        {
            // Nothing to measure against: a band sweeps back and forth.
            var band = r.Width * 0.28;
            var pos = (Math.Sin(t * 1.6) + 1) / 2 * (r.Width - band);
            fill = new Rect(pos, 0, band, r.Height);
        }
        var accent = Ui.Color("accent");
        using (ctx.PushClip(new RoundedRect(fill, radius)))
        {
            ctx.FillRectangle(new SolidColorBrush(accent), fill);
            // Diagonal stripes, drifting while it runs.
            var shift = _running ? t * 18 % 12 : 0;
            var stripe = new SolidColorBrush(Colors.White, 0.22);
            for (var x = fill.X - 12 + shift - r.Height; x < fill.Right + r.Height; x += 12)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x, r.Height), true);
                    c.LineTo(new Point(x + r.Height, 0));
                    c.LineTo(new Point(x + r.Height + 5, 0));
                    c.LineTo(new Point(x + 5, r.Height));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(stripe, null, g);
            }
        }
    }
}

/// <summary>A disk platter spinning under a read head that skips about: the scan is working.</summary>
public sealed class Platter : Control
{
    private bool _running;

    public Platter()
    {
        Width = Height = 46;
        Animator.Track(this, () => _running);
    }

    public bool Running
    {
        get => _running;
        set { if (_running == value) return; _running = value; Animator.Set(this, value && TopLevel.GetTopLevel(this) is not null); InvalidateVisual(); }
    }

    private double _angle, _last = Animator.Seconds;

    public override void Render(DrawingContext ctx)
    {
        var now = Animator.Seconds;
        if (_running) _angle += (now - _last) * 5.5;
        _last = now;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(size / 2, size / 2);
        var rad = size / 2 - 1;
        var accent = Ui.Color("accent");
        ctx.DrawEllipse(Ui.Tk("bgDeep"), new Pen(Ui.Tk("lineStrong"), 1), c, rad, rad);
        // Tracks, with a lit sector going round on each.
        for (var i = 0; i < 4; i++)
        {
            var tr = rad * (0.35 + i * 0.16);
            ctx.DrawEllipse(null, new Pen(Ui.Tk("lineSoft"), 1), c, tr, tr);
            var a = _angle * (1 + i * 0.15) + i;
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(Polar(c, tr, a), false);
                s.ArcTo(Polar(c, tr, a + 1.1), new Size(tr, tr), 0, false, SweepDirection.Clockwise);
                s.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(accent, _running ? 0.9 - i * 0.15 : 0.35), 2.2, lineCap: PenLineCap.Round), g);
        }
        ctx.DrawEllipse(Ui.Tk("panelHi"), new Pen(Ui.Tk("lineStrong"), 1), c, rad * 0.16, rad * 0.16);
        // The arm swings over the tracks as it seeks.
        var seek = _running ? 0.45 + 0.35 * Math.Sin(now * 2.3) * Math.Sin(now * 0.7 + 1) : 0.2;
        var pivot = new Point(size - 4, size - 4);
        var armAngle = Math.PI * (1.08 + seek * 0.32);
        var tip = Polar(pivot, rad * 1.05, armAngle);
        ctx.DrawLine(new Pen(Ui.Tk("textSoft"), 2.4, lineCap: PenLineCap.Round), pivot, tip);
        ctx.DrawEllipse(Ui.Tk("textSoft"), null, pivot, 3.5, 3.5);
        ctx.DrawEllipse(new SolidColorBrush(accent), null, tip, 2.2, 2.2);
    }

    private static Point Polar(Point c, double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
}
