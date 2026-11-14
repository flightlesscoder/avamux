using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avamux.EasterEggs;

/// <summary>STARFLD.EXE: flying forward through a field of stars.</summary>
public sealed class Starfield : DosProgram
{
    private struct Star { public double X, Y, Z, PrevZ; public int Tint; }
    private readonly Star[] _stars = new Star[640];
    private double _speed = 0.35, _t;
    private static readonly IBrush[] Tints = [Brushes.White, new ImmutableSolidColorBrush(Color.Parse("#cfe3ff")), new ImmutableSolidColorBrush(Color.Parse("#fff2cc"))];

    public override bool IsSaver => true;

    public override void Start()
    {
        for (var i = 0; i < _stars.Length; i++) _stars[i] = NewStar(R(0.05, 1));
    }

    private static Star NewStar(double z) => new() { X = R(-1, 1), Y = R(-1, 1), Z = z, PrevZ = z, Tint = Rnd.Next(10) < 7 ? 0 : Rnd.Next(1, 3) };

    public override void Update(double dt)
    {
        _t += dt;
        // Every so often, a burst of warp speed.
        _speed = 0.35 + 0.9 * Math.Pow(Math.Max(0, Math.Sin(_t * 0.25)), 8);
        for (var i = 0; i < _stars.Length; i++)
        {
            ref var s = ref _stars[i];
            s.PrevZ = s.Z;
            s.Z -= dt * _speed;
            if (s.Z <= 0.02) s = NewStar(1);
        }
    }

    public override void Draw(DrawingContext g)
    {
        g.FillRectangle(Brushes.Black, new Rect(0, 0, DosScreen.Width, DosScreen.Height));
        const double cx = DosScreen.Width / 2.0, cy = DosScreen.Height / 2.0;
        foreach (var s in _stars)
        {
            var x = cx + s.X / s.Z * cx;
            var y = cy + s.Y / s.Z * cy;
            if (x < 0 || y < 0 || x >= DosScreen.Width || y >= DosScreen.Height) continue;
            var size = Math.Clamp((1 - s.Z) * 4.5, 1, 4);
            var bright = Math.Clamp(1.35 - s.Z, 0.35, 1);
            using (g.PushOpacity(bright))
            {
                if (_speed > 0.6)
                {
                    // Streaks while at warp.
                    var px = cx + s.X / s.PrevZ * cx;
                    var py = cy + s.Y / s.PrevZ * cy;
                    g.DrawLine(new Pen(Tints[s.Tint], size * 0.8), new Point(px, py), new Point(x, y));
                }
                g.FillRectangle(Tints[s.Tint], new Rect(Math.Round(x), Math.Round(y), Math.Round(size), Math.Round(size)));
            }
        }
    }
}

/// <summary>FLYING.EXE: chrome toasters flap across the night sky, with slices of toast.</summary>
public sealed class Toasters : DosProgram
{
    private sealed class Flyer { public double X, Y, Speed, Scale, Phase, FlapRate; public bool Toast; public int Doneness; }
    private readonly List<Flyer> _flyers = [];

    private static readonly IBrush Chrome = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#f4f6fb"), 0), new GradientStop(Color.Parse("#b8bdcc"), 0.45), new GradientStop(Color.Parse("#7d8496"), 1) },
    }.ToImmutable();
    private static readonly IPen Outline = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2b2f3a")), 1.4);
    private static readonly IBrush Slot = new ImmutableSolidColorBrush(Color.Parse("#1b1d24"));
    private static readonly IBrush Shine = new ImmutableSolidColorBrush(Color.FromArgb(200, 255, 255, 255));
    private static readonly IBrush Wing = new ImmutableSolidColorBrush(Color.Parse("#fbfbff"));
    private static readonly IPen Feather = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#9aa3b8")), 1);
    private static readonly IBrush[] Bread = new[] { "#f0d39a", "#d9a441", "#a8692a" }.Select(h => (IBrush)new ImmutableSolidColorBrush(Color.Parse(h))).ToArray();
    private static readonly IBrush Crust = new ImmutableSolidColorBrush(Color.Parse("#6e3f16"));
    private static readonly IBrush Sky = new ImmutableSolidColorBrush(Color.Parse("#000010"));

    // A wing, pivoting at (0,0), swept back and up; x grows backwards (to the right, as they fly left).
    private static readonly Geometry WingShape = StreamGeometry.Parse("M0,0 C6,-10 20,-22 46,-27 C43,-23 45,-21 41,-18 C43,-15 41,-13 36,-11 C38,-8 35,-6 30,-5 C31,-2 27,0 21,0 C18,2 10,3 0,0 Z");

    public override bool IsSaver => true;

    public override void Start()
    {
        for (var i = 0; i < 11; i++)
        {
            var f = Spawn();
            f.X = R(-60, DosScreen.Width + 60);
            f.Y = R(-60, DosScreen.Height);
            _flyers.Add(f);
        }
    }

    private static Flyer Spawn()
    {
        var toast = Rnd.Next(4) == 0;
        var scale = R(0.65, 1.2);
        // Enter from the top or the right edge.
        var fromTop = Rnd.Next(2) == 0;
        return new Flyer
        {
            Toast = toast, Scale = scale, Speed = 38 + 34 * scale, Phase = R(0, 4), FlapRate = R(5, 7), Doneness = Rnd.Next(3),
            X = fromTop ? R(80, DosScreen.Width + 80) : DosScreen.Width + 60, Y = fromTop ? -60 : R(-40, DosScreen.Height * 0.6),
        };
    }

    public override void Update(double dt)
    {
        for (var i = 0; i < _flyers.Count; i++)
        {
            var f = _flyers[i];
            f.X -= f.Speed * dt;
            f.Y += f.Speed * 0.5 * dt;
            f.Phase += dt * f.FlapRate;
            if (f.X < -90 || f.Y > DosScreen.Height + 70) _flyers[i] = Spawn();
        }
    }

    public override void Draw(DrawingContext g)
    {
        g.FillRectangle(Sky, new Rect(0, 0, DosScreen.Width, DosScreen.Height));
        // Small ones are further away, so they go first.
        foreach (var f in _flyers.OrderBy(f => f.Scale))
        {
            using (g.PushTransform(Matrix.CreateScale(f.Scale, f.Scale) * Matrix.CreateTranslation(f.X, f.Y)))
            {
                if (f.Toast) DrawToast(g, f.Doneness);
                else DrawToaster(g, f.Phase);
            }
        }
    }

    private static void DrawToaster(DrawingContext g, double phase)
    {
        // Four flap positions, like hand-drawn animation frames: up, middle, down, middle.
        var frame = (int)phase % 4;
        var angle = frame switch { 0 => -22, 1 => 6, 2 => 34, _ => 6 };
        // Far wing, behind the body.
        using (g.PushTransform(Matrix.CreateRotation(Math.PI / 180 * (angle - 12)) * Matrix.CreateTranslation(22, 6)))
        using (g.PushOpacity(0.8))
            g.DrawGeometry(Wing, Feather, WingShape);
        // The body: a rounded chrome box with two slots and a lever.
        g.DrawRectangle(Chrome, Outline, new Rect(0, 0, 52, 34), 7, 7);
        g.FillRectangle(Shine, new Rect(6, 6, 28, 2));
        g.DrawRectangle(Slot, null, new Rect(9, 1.5, 15, 3), 1.5, 1.5);
        g.DrawRectangle(Slot, null, new Rect(28, 1.5, 15, 3), 1.5, 1.5);
        g.DrawRectangle(Slot, null, new Rect(50, 12, 4, 9), 1, 1);
        g.DrawRectangle(Bread[1], Outline, new Rect(10, -5, 13, 7), 3, 3);
        g.FillRectangle(Slot, new Rect(6, 34, 6, 3));
        g.FillRectangle(Slot, new Rect(40, 34, 6, 3));
        // Near wing, in front.
        using (g.PushTransform(Matrix.CreateRotation(Math.PI / 180 * angle) * Matrix.CreateTranslation(26, 9)))
        {
            g.DrawGeometry(Wing, Feather, WingShape);
            g.DrawLine(Feather, new Point(10, -5), new Point(38, -19));
            g.DrawLine(Feather, new Point(12, -2), new Point(32, -9));
        }
    }

    private static void DrawToast(DrawingContext g, int doneness)
    {
        var bread = Bread[doneness];
        g.DrawGeometry(Crust, null, StreamGeometry.Parse("M0,30 L0,9 C0,-2 10,-4 15,2 C20,-4 30,-2 30,9 L30,30 Z"));
        g.DrawGeometry(bread, null, StreamGeometry.Parse("M3,27 L3,10 C3,2 10,1 15,5 C20,1 27,2 27,10 L27,27 Z"));
        if (doneness > 0)
        {
            using (g.PushOpacity(0.35))
            {
                g.FillRectangle(Crust, new Rect(7, 10, 3, 2));
                g.FillRectangle(Crust, new Rect(18, 15, 3, 2));
                g.FillRectangle(Crust, new Rect(11, 21, 3, 2));
            }
        }
    }
}
