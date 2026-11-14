using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avamux.EasterEggs;

/// <summary>
/// SHMUP.EXE, a.k.a. NEBULA BLASTER '96: a side-scrolling shooter with an intro screen straight off a shareware
/// CD. The ship is on the left; saucers, rocks and power-ups come from the right.
/// </summary>
public sealed class Shmup : DosProgram
{
    private enum Stage { Title, Play, Over }
    private enum Kind { Saucer, Rock, PowerSpread, PowerShield, PowerLife, EnemyShot }
    private sealed class Thing
    {
        public Kind Kind;
        public double X, Y, Vx, Vy, R, Spin, Angle, Phase, Cooldown;
        public int Hp;
        public Point[]? Shape;
    }
    private struct Star { public double X, Y, Speed; public int Layer; }
    private struct Spark { public double X, Y, Vx, Vy, Life; public IBrush Brush; }

    private const double ShipSpeed = 230;
    private Stage _stage = Stage.Title;
    private readonly Star[] _stars = new Star[120];
    private readonly List<Thing> _things = [];
    private readonly List<(double X, double Y, double Vy)> _shots = [];
    private readonly List<Spark> _sparks = [];
    private double _t, _stageT, _x, _y, _fireWait, _spawnSaucer, _spawnRock, _spawnPower, _spread, _shield, _invulnerable, _playT;
    private int _score, _lives;
    private bool _newHigh;

    private static readonly IBrush Hull = new ImmutableSolidColorBrush(Color.Parse("#c9d3e6"));
    private static readonly IBrush HullDark = new ImmutableSolidColorBrush(Color.Parse("#5d6b8a"));
    private static readonly IBrush Cockpit = new ImmutableSolidColorBrush(Color.Parse("#55ffff"));
    private static readonly IBrush Flame1 = new ImmutableSolidColorBrush(Color.Parse("#ffff55"));
    private static readonly IBrush Flame2 = new ImmutableSolidColorBrush(Color.Parse("#ff5555"));
    private static readonly IBrush ShotBrush = new ImmutableSolidColorBrush(Color.Parse("#55ff55"));
    private static readonly IBrush EnemyShotBrush = new ImmutableSolidColorBrush(Color.Parse("#ff55ff"));
    private static readonly IBrush SaucerBrush = new ImmutableSolidColorBrush(Color.Parse("#ff5555"));
    private static readonly IBrush DomeBrush = new ImmutableSolidColorBrush(Color.Parse("#aaffaa"));
    private static readonly IBrush RockBrush = new ImmutableSolidColorBrush(Color.Parse("#8a6d55"));
    private static readonly IPen RockEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#d0b090")), 1.2);
    private static readonly IPen ShieldPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#55ffff")), 1.5);
    private static readonly IBrush[] StarBrushes = new[] { "#555555", "#aaaaaa", "#ffffff" }.Select(h => (IBrush)new ImmutableSolidColorBrush(Color.Parse(h))).ToArray();
    private static readonly IBrush[] Rainbow = [Cga.Brush(12), Cga.Brush(14), Cga.Brush(10), Cga.Brush(11), Cga.Brush(9), Cga.Brush(13)];
    private const string Marquee = "*** SHAREWARE VERSION *** THIS IS EPISODE 1 OF 1 *** PLEASE REGISTER! SEND $15 AND A SELF-ADDRESSED FLOPPY TO AVAMUX SOFT *** " +
                                   "GREETZ TO EVERY BBS SYSOP WHO EVER LEFT THE LINE OPEN AT 3AM *** CODE: AVAMUX *** MUSIC: NONE (SOUND CARD SET TO IRQ 7?) *** " +
                                   "NOW WITH 256 COLOURS (WE USE 16) ***          ";

    /// <summary>Drawn at 320x200, the size such a game would really have had.</summary>
    public override int PixelScale => 2;

    // Text is collected while drawing and written over the chunky picture in the full-size VGA font.
    private readonly List<(string Text, double X, double Y, int Fg, int Bg)> _texts = [];
    private void Text(string text, double x, double y, int fg, int bg = -1) => _texts.Add((text, x, y, fg, bg));
    private void Centre(string text, double y, int fg, int bg = -1) => Text(text, Math.Round((DosScreen.Width - text.Length * Glyphs.W) / 2.0), y, fg, bg);

    public override void DrawOverlay(DrawingContext g)
    {
        foreach (var t in _texts) Glyphs.Text(g, t.Text, t.X, t.Y, t.Fg, t.Bg);
        _texts.Clear();
    }

    public override void Start()
    {
        for (var i = 0; i < _stars.Length; i++)
        {
            var layer = Rnd.Next(3);
            _stars[i] = new Star { X = R(0, DosScreen.Width), Y = R(0, DosScreen.Height), Layer = layer, Speed = 20 + layer * 45 + R(0, 15) };
        }
    }

    private void NewGame()
    {
        _stage = Stage.Play;
        _stageT = _playT = 0;
        _things.Clear(); _shots.Clear(); _sparks.Clear();
        (_x, _y) = (60, DosScreen.Height / 2.0);
        _lives = 3; _score = 0;
        _spread = _shield = 0;
        _invulnerable = 2;
        _spawnSaucer = 1.2; _spawnRock = 2.5; _spawnPower = 9;
        _newHigh = false;
    }

    public override void KeyDown(Key key, KeyModifiers mods)
    {
        if (key != Key.Space && key != Key.Enter) return;
        if (_stage == Stage.Title) NewGame();
        else if (_stage == Stage.Over && _stageT > 1.2) NewGame();
    }

    public override void Update(double dt)
    {
        _t += dt;
        _stageT += dt;
        for (var i = 0; i < _stars.Length; i++)
        {
            ref var s = ref _stars[i];
            s.X -= s.Speed * dt * (_stage == Stage.Title ? 2.2 : 1);
            if (s.X < 0) { s.X += DosScreen.Width; s.Y = R(0, DosScreen.Height); }
        }
        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var p = _sparks[i];
            p.X += p.Vx * dt; p.Y += p.Vy * dt; p.Life -= dt;
            if (p.Life <= 0) _sparks.RemoveAt(i); else _sparks[i] = p;
        }
        if (_stage != Stage.Play) return;
        _playT += dt;
        var level = 1 + _playT / 40;

        // The ship.
        double mx = 0, my = 0;
        if (Held(Key.Left) || Held(Key.A)) mx -= 1;
        if (Held(Key.Right) || Held(Key.D)) mx += 1;
        if (Held(Key.Up) || Held(Key.W)) my -= 1;
        if (Held(Key.Down) || Held(Key.S)) my += 1;
        _x = Math.Clamp(_x + mx * ShipSpeed * dt, 16, DosScreen.Width * 0.6);
        _y = Math.Clamp(_y + my * ShipSpeed * dt, 24, DosScreen.Height - 12);
        _spread = Math.Max(0, _spread - dt);
        _shield = Math.Max(0, _shield - dt);
        _invulnerable = Math.Max(0, _invulnerable - dt);
        _fireWait -= dt;
        if ((Held(Key.Space) || Held(Key.LeftCtrl)) && _fireWait <= 0)
        {
            _fireWait = 0.16;
            _shots.Add((_x + 18, _y, 0));
            if (_spread > 0) { _shots.Add((_x + 14, _y - 4, -140)); _shots.Add((_x + 14, _y + 4, 140)); }
        }
        for (var i = _shots.Count - 1; i >= 0; i--)
        {
            var (sx, sy, svy) = _shots[i];
            sx += 520 * dt; sy += svy * dt;
            if (sx > DosScreen.Width + 10 || sy < 0 || sy > DosScreen.Height) _shots.RemoveAt(i);
            else _shots[i] = (sx, sy, svy);
        }

        // New arrivals, more often as time goes on.
        if ((_spawnSaucer -= dt) <= 0)
        {
            _spawnSaucer = R(0.6, 1.6) / Math.Sqrt(level);
            var wave = Rnd.Next(3) == 0 ? 3 : 1;
            var y = R(40, DosScreen.Height - 40);
            for (var k = 0; k < wave; k++)
                _things.Add(new Thing { Kind = Kind.Saucer, X = DosScreen.Width + 20 + k * 34, Y = y, Vx = -R(100, 140) * Math.Min(1.8, level), R = 11, Hp = 1, Phase = R(0, 6) + k * 0.6, Cooldown = R(1, 3) });
        }
        if ((_spawnRock -= dt) <= 0)
        {
            _spawnRock = R(1.5, 3.2) / Math.Sqrt(level);
            var r = R(12, 26);
            var points = Enumerable.Range(0, 9).Select(k => { var a = k * Math.PI * 2 / 9; var d = r * R(0.7, 1.1); return new Point(Math.Cos(a) * d, Math.Sin(a) * d); }).ToArray();
            _things.Add(new Thing { Kind = Kind.Rock, X = DosScreen.Width + r, Y = R(30, DosScreen.Height - 30), Vx = -R(50, 95), Vy = R(-15, 15), R = r * 0.9, Hp = (int)(r / 7), Spin = R(-2, 2), Shape = points });
        }
        if ((_spawnPower -= dt) <= 0)
        {
            _spawnPower = R(8, 14);
            var kind = Rnd.Next(10) switch { < 5 => Kind.PowerSpread, < 9 => Kind.PowerShield, _ => Kind.PowerLife };
            _things.Add(new Thing { Kind = kind, X = DosScreen.Width + 12, Y = R(40, DosScreen.Height - 40), Vx = -70, R = 10, Hp = 1, Phase = R(0, 6) });
        }

        foreach (var th in _things)
        {
            th.X += th.Vx * dt;
            th.Y += th.Vy * dt;
            th.Angle += th.Spin * dt;
            th.Phase += dt;
            if (th.Kind == Kind.Saucer)
            {
                th.Y += Math.Cos(th.Phase * 3) * 70 * dt;
                // Later on, saucers shoot back.
                if (level > 1.4 && (th.Cooldown -= dt) <= 0 && th.X < DosScreen.Width - 20 && th.X > _x + 60)
                {
                    th.Cooldown = R(1.6, 3.2) / level;
                    var (dx, dy) = (_x - th.X, _y - th.Y);
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    _things.Add(new Thing { Kind = Kind.EnemyShot, X = th.X - 8, Y = th.Y, Vx = dx / d * 190, Vy = dy / d * 190, R = 3, Hp = 1 });
                }
            }
            else if (th.Kind is Kind.PowerSpread or Kind.PowerShield or Kind.PowerLife) th.Y += Math.Sin(th.Phase * 2.5) * 30 * dt;
        }

        // Shots against things.
        for (var i = _shots.Count - 1; i >= 0; i--)
        {
            var (sx, sy, _) = _shots[i];
            var target = _things.FirstOrDefault(th => th.Kind is Kind.Saucer or Kind.Rock && Math.Abs(th.X - sx) < th.R + 4 && Math.Abs(th.Y - sy) < th.R + 3);
            if (target is null) continue;
            _shots.RemoveAt(i);
            if (--target.Hp > 0) { Burst(sx, sy, 4, RockEdge.Brush!); continue; }
            _things.Remove(target);
            _score += target.Kind == Kind.Saucer ? 100 : 50 * (int)(target.R / 6);
            Burst(target.X, target.Y, target.Kind == Kind.Saucer ? 26 : 34, target.Kind == Kind.Saucer ? SaucerBrush : RockBrush);
        }

        // Things against the ship.
        for (var i = _things.Count - 1; i >= 0; i--)
        {
            var th = _things[i];
            if (th.X < -40 || th.Y < -40 || th.Y > DosScreen.Height + 40) { _things.RemoveAt(i); continue; }
            var (dx, dy) = (th.X - _x, th.Y - _y);
            if (dx * dx + dy * dy > (th.R + 9) * (th.R + 9)) continue;
            _things.RemoveAt(i);
            switch (th.Kind)
            {
                case Kind.PowerSpread: _spread = 12; _score += 250; Burst(th.X, th.Y, 14, Cga.Brush(14)); break;
                case Kind.PowerShield: _shield = 9; _score += 250; Burst(th.X, th.Y, 14, Cga.Brush(11)); break;
                case Kind.PowerLife: _lives = Math.Min(9, _lives + 1); _score += 500; Burst(th.X, th.Y, 14, Cga.Brush(13)); break;
                default:
                    Burst(th.X, th.Y, 20, SaucerBrush);
                    if (_shield > 0 || _invulnerable > 0) break;
                    Burst(_x, _y, 60, Flame1);
                    DosScreen.Beep();
                    if (--_lives <= 0) GameOver();
                    else { _invulnerable = 2.5; _spread = 0; }
                    break;
            }
        }
    }

    private void GameOver()
    {
        _stage = Stage.Over;
        _stageT = 0;
        var state = DriveAState.Current;
        if (_score > state.ShmupHighScore) { state.ShmupHighScore = _score; state.Save(); _newHigh = true; }
    }

    private void Burst(double x, double y, int count, IBrush brush)
    {
        for (var i = 0; i < count; i++)
        {
            var a = R(0, Math.PI * 2);
            var s = R(30, 190);
            _sparks.Add(new Spark { X = x, Y = y, Vx = Math.Cos(a) * s, Vy = Math.Sin(a) * s, Life = R(0.25, 0.7), Brush = Rnd.Next(3) == 0 ? Flame1 : brush });
        }
    }

    public override void Draw(DrawingContext g)
    {
        _texts.Clear();
        g.FillRectangle(Brushes.Black, new Rect(0, 0, DosScreen.Width, DosScreen.Height));
        foreach (var s in _stars) g.FillRectangle(StarBrushes[s.Layer], new Rect(Math.Round(s.X), Math.Round(s.Y), s.Layer == 2 ? 2 : 1, 1));
        switch (_stage)
        {
            case Stage.Title: DrawTitle(g); break;
            case Stage.Play: DrawPlay(g); break;
            case Stage.Over: DrawPlay(g); DrawOver(g); break;
        }
        foreach (var p in _sparks) g.FillRectangle(p.Brush, new Rect(p.X, p.Y, 2, 2));
    }

    private void DrawTitle(DrawingContext g)
    {
        // Each letter bobs on a sine wave and cycles through the colours. Very 1996.
        const string title = "NEBULA BLASTER";
        var face = new Typeface(Avamux.Views.Ui.Mono, FontStyle.Normal, FontWeight.Black);
        var x = (DosScreen.Width - title.Length * 38) / 2.0;
        for (var i = 0; i < title.Length; i++)
        {
            var brush = Rainbow[(int)(i + _t * 8) % Rainbow.Length];
            var letter = new FormattedText(title[i].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 52, brush);
            var y = 70 + Math.Sin(_t * 4 + i * 0.5) * 10;
            var shadow = new FormattedText(title[i].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 52, Cga.Brush(1));
            g.DrawText(shadow, new Point(x + i * 38 + 4, y + 4));
            g.DrawText(letter, new Point(x + i * 38, y));
        }
        var year = new FormattedText("'96", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 34, Cga.Brush((int)(_t * 6) % 2 == 0 ? 14 : 12));
        using (g.PushTransform(Matrix.CreateRotation(-0.25) * Matrix.CreateTranslation(510, 140)))
            g.DrawText(year, new Point(0, 0));
        Centre("THE ULTIMATE SPACE ADVENTURE!!!", 162, 11);
        Centre("~ Turbo Edition ~", 180, 13);

        DrawShip(g, 160 + Math.Sin(_t * 1.5) * 40, 236 + Math.Sin(_t * 2.3) * 12, true);
        Centre("ARROWS MOVE   SPACE FIRES   ESC QUITS", 280, 7);
        Centre($"HIGH SCORE {DriveAState.Current.ShmupHighScore:000000}", 300, 14);
        if ((int)(_t * 2.5) % 2 == 0) Centre("PRESS SPACE TO START", 326, 15);

        var scroll = (int)(_t * 14) % Marquee.Length;
        var text = (Marquee + Marquee)[scroll..(scroll + 80)];
        Text(text, 0, DosScreen.Height - 24, 10, 1);
    }

    private void DrawPlay(DrawingContext g)
    {
        foreach (var th in _things)
        {
            switch (th.Kind)
            {
                case Kind.Saucer:
                    g.DrawEllipse(DomeBrush, null, new Point(th.X, th.Y - 4), 6, 5);
                    g.DrawEllipse(SaucerBrush, null, new Point(th.X, th.Y), 12, 4.5);
                    for (var k = -1; k <= 1; k++) g.FillRectangle((int)(th.Phase * 6 + k) % 2 == 0 ? Flame1 : Cockpit, new Rect(th.X + k * 6 - 1, th.Y - 1, 2, 2));
                    break;
                case Kind.Rock:
                    using (g.PushTransform(Matrix.CreateRotation(th.Angle) * Matrix.CreateTranslation(th.X, th.Y)))
                    {
                        var geo = new StreamGeometry();
                        using (var c = geo.Open())
                        {
                            c.BeginFigure(th.Shape![0], true);
                            foreach (var p in th.Shape.Skip(1)) c.LineTo(p);
                            c.EndFigure(true);
                        }
                        g.DrawGeometry(RockBrush, RockEdge, geo);
                        g.DrawEllipse(HullDark, null, new Point(th.R * 0.2, -th.R * 0.25), th.R * 0.18, th.R * 0.15);
                    }
                    break;
                case Kind.EnemyShot:
                    g.DrawEllipse(EnemyShotBrush, null, new Point(th.X, th.Y), 3, 3);
                    break;
                default:
                    var (letter, colour) = th.Kind switch { Kind.PowerSpread => ("P", 14), Kind.PowerShield => ("S", 11), _ => ("+", 13) };
                    g.DrawRectangle(Cga.Brush(1), new Pen(Cga.Brush(colour), 2), new Rect(th.X - 9, th.Y - 9, 18, 18), 4, 4);
                    Text(letter, th.X - 4, th.Y - 8, colour);
                    break;
            }
        }
        foreach (var (sx, sy, _) in _shots) g.FillRectangle(ShotBrush, new Rect(sx - 6, sy - 1, 10, 2));
        if (_stage == Stage.Play && (_invulnerable <= 0 || (int)(_t * 12) % 2 == 0)) DrawShip(g, _x, _y, false);
        if (_stage == Stage.Play && _shield > 0 && (_shield > 2 || (int)(_t * 8) % 2 == 0)) g.DrawEllipse(null, ShieldPen, new Point(_x + 2, _y), 22, 16);

        Text($"SCORE {_score:000000}", 8, 4, 15);
        Text($"HI {Math.Max(_score, DriveAState.Current.ShmupHighScore):000000}", 260, 4, 7);
        Text("SHIPS " + new string('▲', Math.Max(0, _lives)), 500, 4, 10);
        if (_spread > 0) Text($"SPREAD {_spread:0}", 8, DosScreen.Height - 20, 14);
        if (_shield > 0) Text($"SHIELD {_shield:0}", 120, DosScreen.Height - 20, 11);
        if (_stage == Stage.Play && _playT < 2.5) Centre("GET READY!", 190, (int)(_t * 6) % 2 == 0 ? 15 : 14);
    }

    private void DrawOver(DrawingContext g)
    {
        var face = new Typeface(Avamux.Views.Ui.Mono, FontStyle.Normal, FontWeight.Black);
        var over = new FormattedText("GAME OVER", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 56, Cga.Brush(12));
        g.DrawText(over, new Point((DosScreen.Width - over.Width) / 2, 110));
        Centre($"FINAL SCORE {_score:000000}", 200, 15);
        if (_newHigh) Centre("*** NEW HIGH SCORE! ***", 222, (int)(_t * 6) % 2 == 0 ? 14 : 13);
        if (_stageT > 1.2) Centre("SPACE PLAYS AGAIN   ESC QUITS", 270, 7);
    }

    private void DrawShip(DrawingContext g, double x, double y, bool big)
    {
        using (g.PushTransform(Matrix.CreateScale(big ? 2 : 1, big ? 2 : 1) * Matrix.CreateTranslation(x, y)))
        {
            var flicker = (int)(_t * 20) % 2 == 0;
            g.DrawGeometry(flicker ? Flame1 : Flame2, null, StreamGeometry.Parse(flicker ? "M-12,-3 L-22,0 L-12,3 Z" : "M-12,-3 L-18,0 L-12,3 Z"));
            g.DrawGeometry(HullDark, null, StreamGeometry.Parse("M-8,-6 L-14,-12 L-4,-10 L2,-5 Z M-8,6 L-14,12 L-4,10 L2,5 Z"));
            g.DrawGeometry(Hull, new Pen(HullDark, 1), StreamGeometry.Parse("M-13,-5 L4,-6 L18,0 L4,6 L-13,5 Z"));
            g.DrawEllipse(Cockpit, null, new Point(5, -1.5), 4, 2.5);
        }
    }
}
