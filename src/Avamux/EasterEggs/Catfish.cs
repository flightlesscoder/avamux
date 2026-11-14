using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avamux.EasterEggs;

/// <summary>
/// CATFISH.BAS: two cats on the rooftops take turns throwing exploding fish at each other. Type an angle and a
/// speed; the wind, gravity and the buildings in the way do the rest. Player 2 can be the computer.
/// </summary>
public sealed class Catfish : DosProgram
{
    private enum Stage { Intro, Setup, Aim, Flying, Boom, RoundOver, GameOver }
    private sealed record Building(Rect Box, int Color, bool[] Lit);
    private sealed class Blast { public Point At; public double R, T, Max; public bool Big; }

    private const double Scale = 6, GravityScale = 29, WindScale = 4;
    private static readonly IBrush Sky = new ImmutableSolidColorBrush(Color.Parse("#0000aa"));
    private static readonly IBrush[] CityColours = new[] { "#00aaaa", "#aa0000", "#aaaaaa" }.Select(h => (IBrush)new ImmutableSolidColorBrush(Color.Parse(h))).ToArray();
    private static readonly IBrush LitWindow = new ImmutableSolidColorBrush(Color.Parse("#ffff55"));
    private static readonly IBrush DarkWindow = new ImmutableSolidColorBrush(Color.Parse("#555555"));
    private static readonly IBrush MoonBrush = new ImmutableSolidColorBrush(Color.Parse("#ffffcc"));
    private static readonly IBrush FishBrush = new ImmutableSolidColorBrush(Color.Parse("#7fd4ff"));
    private static readonly IBrush[] Fur = [new ImmutableSolidColorBrush(Color.Parse("#ff9f3a")), new ImmutableSolidColorBrush(Color.Parse("#d8d8e0"))];
    private static readonly IBrush Boom1 = new ImmutableSolidColorBrush(Color.Parse("#ff5555"));
    private static readonly IBrush Boom2 = new ImmutableSolidColorBrush(Color.Parse("#ffff55"));

    private readonly TextGrid _grid = new();
    private Stage _stage = Stage.Intro;
    private readonly string[] _names = ["Tabby", "Computer"];
    private readonly int[] _score = new int[2];
    private bool _cpu = true;
    private int _target = 3, _setupStep, _turn, _aimStep, _winner;
    private double _gravity = 9.8, _wind, _t, _stageT, _cpuDelay;
    private string _input = "";
    private int _angle;
    private readonly List<Building> _city = [];
    private readonly List<(Point C, double R)> _holes = [];
    private readonly Point[] _cats = new Point[2];
    private Point _fish, _fishV;
    private double _fishSpin;
    private Blast? _blast;
    private double _cpuError = 7;
    private string _cpuTyping = "";

    public override void Start() => _grid.CursorVisible = false;

    // ----- Input -----

    public override void KeyDown(Key key, KeyModifiers mods)
    {
        switch (_stage)
        {
            case Stage.Intro: _stage = Stage.Setup; _input = ""; _skipText = true; return;
            case Stage.GameOver when _stageT > 0.8: Done = true; ExitMessage = $"\n{_names[0]} {_score[0]}, {_names[1]} {_score[1]}. Thanks for playing CATFISH.\n"; return;
            case Stage.Setup or Stage.Aim:
                if (_stage == Stage.Aim && _cpu && _turn == 1) return;
                if (key == Key.Back && _input.Length > 0) _input = _input[..^1];
                else if (key == Key.Enter) Submit();
                return;
        }
    }

    private bool _skipText;

    public override void TextInput(string text)
    {
        // The key that left the intro also arrives as text; it is not part of the first answer.
        if (_skipText) { _skipText = false; return; }
        if (_stage == Stage.Setup && _input.Length < 16) _input += text;
        else if (_stage == Stage.Aim && !(_cpu && _turn == 1) && _input.Length < 3 && text.All(char.IsDigit)) _input += text;
    }

    private void Submit()
    {
        var value = _input.Trim();
        _input = "";
        if (_stage == Stage.Setup)
        {
            switch (_setupStep)
            {
                case 0: if (value.Length > 0) _names[0] = value; break;
                case 1:
                    _cpu = value.Length == 0 || value.Equals("computer", StringComparison.OrdinalIgnoreCase) || value.Equals("cpu", StringComparison.OrdinalIgnoreCase);
                    if (value.Length > 0) _names[1] = value;
                    break;
                case 2: if (int.TryParse(value, out var n) && n is > 0 and < 100) _target = n; break;
                case 3: if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g is > 0 and < 200) _gravity = g; break;
            }
            if (++_setupStep == 4) NewRound();
            return;
        }
        if (!int.TryParse(value, out var number)) return;
        if (_aimStep == 0)
        {
            if (number > 90) return;
            _angle = number;
            _aimStep = 1;
        }
        else if (number > 0) Throw(_angle, Math.Min(number, 200));
    }

    // ----- The city -----

    private void NewRound()
    {
        _city.Clear();
        _holes.Clear();
        var x = 2.0;
        var slope = Rnd.Next(4);
        var height = R(90, 180);
        while (x < DosScreen.Width - 2)
        {
            var w = Math.Min(R(42, 78), DosScreen.Width - 2 - x);
            if (DosScreen.Width - 2 - x - w < 30) w = DosScreen.Width - 2 - x;
            height = slope switch
            {
                0 => height + R(-25, 30), // rising
                1 => height + R(-30, 25), // falling
                2 => x < DosScreen.Width / 2 ? height + R(-10, 35) : height - R(-10, 35), // peak
                _ => x < DosScreen.Width / 2 ? height - R(-10, 35) : height + R(-10, 35), // valley
            };
            height = Math.Clamp(height, 60, 270);
            var box = new Rect(x, DosScreen.Height - 20 - height, w - 2, height);
            var cols = (int)((box.Width - 6) / 10);
            var rows = (int)((box.Height - 8) / 15);
            _city.Add(new Building(box, Rnd.Next(3), Enumerable.Range(0, Math.Max(0, cols * rows)).Select(_ => Rnd.Next(4) > 0).ToArray()));
            x += w;
        }
        var left = _city[1].Box;
        var right = _city[^2].Box;
        _cats[0] = new Point(left.X + left.Width / 2, left.Y - 14);
        _cats[1] = new Point(right.X + right.Width / 2, right.Y - 14);
        _wind = Rnd.Next(3) == 0 ? 0 : R(-10, 10);
        _turn = _score.Sum() % 2;
        _stageT = 0;
        StartTurn();
    }

    private void StartTurn()
    {
        _aimStep = 0;
        _input = "";
        _cpuTyping = "";
        _cpuDelay = 0.8;
        _stage = Stage.Aim;
    }

    private bool Solid(Point p)
    {
        foreach (var b in _city)
        {
            if (!b.Box.Contains(p)) continue;
            foreach (var (c, r) in _holes)
                if ((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y) < r * r) return false;
            return true;
        }
        return false;
    }

    private Point Launch(int player, double angle, double speed, out Point velocity)
    {
        var a = angle * Math.PI / 180;
        var dir = player == 0 ? 1 : -1;
        velocity = new Point(dir * Math.Cos(a) * speed * Scale, -Math.Sin(a) * speed * Scale);
        return new Point(_cats[player].X + dir * 10, _cats[player].Y - 16);
    }

    /// <summary>Moves a fish one small step. Returns what it hit: 0 nothing yet, 1 a building, 2/3 cat 0/1, 4 out of the city.</summary>
    private int Step(ref Point p, ref Point v, double dt, int thrower, double flown)
    {
        v = new Point(v.X + _wind * WindScale * dt, v.Y + _gravity * GravityScale * dt);
        p = new Point(p.X + v.X * dt, p.Y + v.Y * dt);
        if (p.X < -10 || p.X > DosScreen.Width + 10 || p.Y > DosScreen.Height) return 4;
        for (var c = 0; c < 2; c++)
        {
            // A fish can't hit the cat that threw it in the first moment of the throw.
            if (c == thrower && flown < 0.15) continue;
            var d = p - _cats[c];
            if (Math.Abs(d.X) < 13 && d.Y > -18 && d.Y < 14) return 2 + c;
        }
        return Solid(p) ? 1 : 0;
    }

    private void Throw(int angle, int speed)
    {
        _fish = Launch(_turn, angle, speed, out _fishV);
        _fishSpin = 0;
        _flown = 0;
        _stage = Stage.Flying;
    }

    private double _flown;

    private (int Angle, int Speed) CpuShot()
    {
        var target = _cats[0];
        var best = (Angle: 45, Speed: 60, Miss: double.MaxValue);
        for (var a = 20; a <= 80; a += 2)
        for (var s = 20; s <= 160; s += 2)
        {
            var p = Launch(1, a, s, out var v);
            var flown = 0.0;
            var result = 0;
            for (var i = 0; i < 900 && result == 0; i++) { result = Step(ref p, ref v, 1 / 60.0, 1, flown); flown += 1 / 60.0; }
            var miss = result == 2 ? 0 : (p - target) is var d ? Math.Sqrt(d.X * d.X + d.Y * d.Y) : 0;
            if (result == 3) miss = 9999;
            if (miss < best.Miss) best = (a, s, miss);
        }
        // Not perfect: it gets closer each throw.
        var angle = (int)Math.Clamp(best.Angle + R(-_cpuError, _cpuError), 5, 89);
        var speed = (int)Math.Clamp(best.Speed + R(-_cpuError * 1.6, _cpuError * 1.6), 10, 200);
        _cpuError = Math.Max(1.2, _cpuError * 0.6);
        return (angle, speed);
    }

    // ----- Time -----

    public override void Update(double dt)
    {
        _t += dt;
        _stageT += dt;
        switch (_stage)
        {
            case Stage.Aim when _cpu && _turn == 1:
                _cpuDelay -= dt;
                if (_cpuDelay > 0) return;
                if (_cpuTyping.Length == 0)
                {
                    var (a, s) = CpuShot();
                    _cpuTyping = $"{a}|{s}";
                }
                var parts = _cpuTyping.Split('|');
                if (_aimStep == 0) { _angle = int.Parse(parts[0], CultureInfo.InvariantCulture); _aimStep = 1; _cpuDelay = 0.6; }
                else Throw(_angle, int.Parse(parts[1], CultureInfo.InvariantCulture));
                break;
            case Stage.Flying:
                _fishSpin += dt * 12;
                const double step = 1 / 240.0;
                for (var t = 0.0; t < dt; t += step)
                {
                    var result = Step(ref _fish, ref _fishV, step, _turn, _flown);
                    _flown += step;
                    if (result == 0) continue;
                    if (result == 4) { _turn = 1 - _turn; StartTurn(); return; }
                    _blast = new Blast { At = _fish, Max = result == 1 ? 14 : 40, Big = result != 1 };
                    if (result >= 2)
                    {
                        var hit = result - 2;
                        _winner = 1 - hit;
                        _score[_winner]++;
                    }
                    _stage = Stage.Boom;
                    _stageT = 0;
                    DosScreen.Beep();
                    return;
                }
                break;
            case Stage.Boom when _blast is { } b:
                b.T += dt;
                b.R = b.Max * Math.Min(1, b.T / (b.Big ? 0.6 : 0.3));
                if (b.T > (b.Big ? 0.9 : 0.4))
                {
                    _holes.Add((b.At, b.Max));
                    _blast = null;
                    if (b.Big) { _stage = Stage.RoundOver; _stageT = 0; }
                    else { _turn = 1 - _turn; StartTurn(); }
                }
                break;
            case Stage.RoundOver when _stageT > 2.4:
                if (_score.Max() >= _target) { _stage = Stage.GameOver; _stageT = 0; }
                else NewRound();
                break;
        }
    }

    // ----- Drawing -----

    public override void Draw(DrawingContext g)
    {
        if (_stage is Stage.Intro or Stage.Setup or Stage.GameOver) { DrawText(g); return; }

        g.FillRectangle(Sky, new Rect(0, 0, DosScreen.Width, DosScreen.Height));
        // A crescent moon.
        g.DrawEllipse(MoonBrush, null, new Point(320, 46), 18, 18);
        g.DrawEllipse(Sky, null, new Point(328, 40), 15, 15);
        foreach (var b in _city)
        {
            g.FillRectangle(CityColours[b.Color], b.Box);
            var cols = (int)((b.Box.Width - 6) / 10);
            var rows = (int)((b.Box.Height - 8) / 15);
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                g.FillRectangle(b.Lit[r * cols + c] ? LitWindow : DarkWindow, new Rect(b.Box.X + 5 + c * 10, b.Box.Y + 6 + r * 15, 4, 7));
        }
        foreach (var (c, r) in _holes) g.DrawEllipse(Sky, null, c, r, r);

        for (var i = 0; i < 2; i++)
        {
            var hit = _stage == Stage.RoundOver && i != _winner;
            if (hit) continue;
            var dancing = _stage == Stage.RoundOver && i == _winner;
            var throwing = (_stage == Stage.Flying && _turn == i && _flown < 0.25) || (dancing && (int)(_stageT * 4) % 2 == 0);
            DrawCat(g, _cats[i], Fur[i], i == 0 ? 1 : -1, throwing, dancing && (int)(_stageT * 4) % 2 == 1);
        }

        if (_stage == Stage.Flying)
        {
            if (_fish.Y < 0) g.FillRectangle(Boom2, new Rect(_fish.X - 1, 2, 3, 6)); // off the top: a marker
            else DrawFish(g, _fish, _fishSpin);
        }
        if (_blast is { } blast)
        {
            g.DrawEllipse(Boom1, null, blast.At, blast.R, blast.R);
            g.DrawEllipse(Boom2, null, blast.At, blast.R * 0.55, blast.R * 0.55);
        }

        // Names, scores, wind.
        Glyphs.Text(g, _names[0], 8, 4, 15);
        Glyphs.Text(g, _names[1], DosScreen.Width - 8 - _names[1].Length * 8, 4, 15);
        var score = $"{_score[0]}>Score<{_score[1]}";
        Glyphs.Centre(g, score, DosScreen.Height - 38, 15, 1);
        if (Math.Abs(_wind) > 0.5)
        {
            var y = DosScreen.Height - 8;
            var len = _wind * 6;
            var pen = new Pen(Boom1, 2);
            g.DrawLine(pen, new Point(320, y), new Point(320 + len, y));
            var tip = Math.Sign(len) * 5;
            g.DrawLine(pen, new Point(320 + len, y), new Point(320 + len - tip, y - 4));
            g.DrawLine(pen, new Point(320 + len, y), new Point(320 + len - tip, y + 4));
        }

        if (_stage == Stage.Aim)
        {
            var x = _turn == 0 ? 8 : DosScreen.Width - 8 - 15 * 8;
            var cursor = _t % 0.5 < 0.27 ? "_" : " ";
            var typing = _cpu && _turn == 1 ? "" : _input + cursor;
            Glyphs.Text(g, "Angle: " + (_aimStep == 0 ? typing : _angle.ToString(CultureInfo.InvariantCulture)), x, 22, 15);
            if (_aimStep == 1) Glyphs.Text(g, "Velocity: " + typing, x, 38, 15);
        }
    }

    private void DrawText(DrawingContext g)
    {
        var grid = _grid;
        grid.Fill(0, 0, 80, 25, ' ', 7, 0);
        if (_stage == Stage.Intro)
        {
            // A frame of stars that chase each other round the edge.
            var phase = (int)(_t * 18);
            for (var i = 0; i < 80; i++) { grid.Put(i, 0, (i + phase) % 5 == 0 ? '*' : ' ', 12, 0); grid.Put(i, 24, (i - phase + 400) % 5 == 0 ? '*' : ' ', 12, 0); }
            for (var j = 1; j < 24; j++) { grid.Put(0, j, (j - phase + 400) % 5 == 0 ? '*' : ' ', 12, 0); grid.Put(79, j, (j + phase) % 5 == 0 ? '*' : ' ', 12, 0); }
            Centre(grid, 4, "Q B a s i c    C A T F I S H", 15);
            Centre(grid, 6, "Copyright (C) avamux soft 1996", 7);
            Centre(grid, 9, "Two cats. One city. A great many fish.", 7);
            Centre(grid, 11, "Take turns throwing an exploding fish at the other cat. Choose", 7);
            Centre(grid, 12, "the angle (0 to 90 degrees) and how hard to throw. Mind the wind:", 7);
            Centre(grid, 13, "the arrow at the bottom shows which way it blows and how strongly.", 7);
            Centre(grid, 14, "Buildings get in the way, until enough fish have gone through them.", 7);
            Centre(grid, 17, "Esc stops the game at any time.", 8);
            Centre(grid, 21, "Press any key to continue", (int)(_t * 2) % 2 == 0 ? 15 : 7);
        }
        else if (_stage == Stage.Setup)
        {
            string[] questions =
            [
                $"Name of Player 1 (Default = '{_names[0]}'): ",
                "Name of Player 2 (Default = 'Computer'): ",
                "Play to how many total points (Default = 3)? ",
                "Gravity in Meters/Sec (Earth = 9.8)? ",
            ];
            string[] answers = [_names[0], _cpu ? "Computer" : _names[1], _target.ToString(CultureInfo.InvariantCulture), _gravity.ToString(CultureInfo.InvariantCulture)];
            for (var i = 0; i <= _setupStep && i < 4; i++)
            {
                var y = 6 + i * 2;
                grid.Text(10, y, questions[i], 7, 0);
                grid.Text(10 + questions[i].Length, y, i < _setupStep ? answers[i] : _input + (_t % 0.5 < 0.27 ? "_" : ""), 15, 0);
            }
        }
        else
        {
            Centre(grid, 8, "G A M E   O V E R !", 15);
            Centre(grid, 11, "Score:", 7);
            Centre(grid, 13, $"{_names[0],-12}{_score[0],4}", 14);
            Centre(grid, 14, $"{_names[1],-12}{_score[1],4}", 14);
            Centre(grid, 18, "Press any key to continue", 7);
        }
        grid.Draw(g, false);
    }

    private static void Centre(TextGrid grid, int y, string text, int fg) => grid.Text((80 - text.Length) / 2, y, text, fg, 0);

    private static void DrawCat(DrawingContext g, Point at, IBrush fur, int facing, bool pawUp, bool otherPawUp)
    {
        var dark = new Pen(Brushes.Black, 1);
        using (g.PushTransform(Matrix.CreateScale(facing, 1) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            // Tail, curling up behind.
            g.DrawGeometry(null, new Pen(fur, 3.5, lineCap: PenLineCap.Round), StreamGeometry.Parse("M-8,10 C-18,8 -16,-6 -12,-10"));
            // Body and head.
            g.DrawEllipse(fur, dark, new Point(0, 6), 9, 8);
            g.DrawEllipse(fur, dark, new Point(2, -7), 7, 6);
            g.DrawGeometry(fur, dark, StreamGeometry.Parse("M-4,-10 L-3,-17 L1,-12 Z M3,-12 L7,-17 L8,-10 Z"));
            g.FillRectangle(Brushes.Black, new Rect(0, -9, 2, 2));
            g.FillRectangle(Brushes.Black, new Rect(5, -9, 2, 2));
            g.FillRectangle(Boom1, new Rect(3, -6, 2, 1.5));
            // Paws: the throwing one goes up.
            var paw = new Pen(fur, 3, lineCap: PenLineCap.Round);
            g.DrawLine(paw, new Point(6, 2), pawUp ? new Point(10, -12) : new Point(9, 12));
            g.DrawLine(paw, new Point(-5, 2), otherPawUp ? new Point(-9, -12) : new Point(-7, 12));
        }
    }

    private static void DrawFish(DrawingContext g, Point at, double spin)
    {
        using (g.PushTransform(Matrix.CreateRotation(spin) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            g.DrawGeometry(FishBrush, new Pen(Brushes.White, 0.8), StreamGeometry.Parse("M-6,0 C-3,-4 3,-4 5,0 C3,4 -3,4 -6,0 Z M5,0 L9,-4 L9,4 Z"));
            g.FillRectangle(Brushes.Black, new Rect(-4, -1, 1.5, 1.5));
        }
    }
}
