using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avamux.Core.EasterEggs;
using Avamux.Views;

namespace Avamux.EasterEggs;

/// <summary>The 16 CGA/EGA text colours.</summary>
public static class Cga
{
    public static readonly Color[] Colors =
    [
        Color.Parse("#000000"), Color.Parse("#0000aa"), Color.Parse("#00aa00"), Color.Parse("#00aaaa"),
        Color.Parse("#aa0000"), Color.Parse("#aa00aa"), Color.Parse("#aa5500"), Color.Parse("#aaaaaa"),
        Color.Parse("#555555"), Color.Parse("#5555ff"), Color.Parse("#55ff55"), Color.Parse("#55ffff"),
        Color.Parse("#ff5555"), Color.Parse("#ff55ff"), Color.Parse("#ffff55"), Color.Parse("#ffffff"),
    ];

    private static readonly IBrush[] Brushes = Colors.Select(c => (IBrush)new ImmutableSolidColorBrush(c)).ToArray();
    public static IBrush Brush(int i) => Brushes[i & 15];
}

/// <summary>
/// Character cells of 8x16 pixels, drawn once each into a small bitmap without anti-aliasing so they look like a
/// VGA text mode. Block, shade and line-drawing characters are drawn as pixels rather than taken from a font,
/// so boxes join up and blocks fill their cell.
/// </summary>
public static class Glyphs
{
    public const int W = 8, H = 16;
    private static readonly Dictionary<(char, int, int), RenderTargetBitmap> Cache = new();
    private static readonly Typeface Face = new(Ui.Mono, FontStyle.Normal, FontWeight.Bold);
    private static readonly double Size = PickSize();
    private static readonly RenderOptions Crisp = new() { EdgeMode = EdgeMode.Aliased, BitmapInterpolationMode = BitmapInterpolationMode.None };
    private static readonly TextOptions CrispText = new() { TextRenderingMode = TextRenderingMode.Alias };

    private static double PickSize()
    {
        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 100, Avalonia.Media.Brushes.White);
        var perChar = probe.WidthIncludingTrailingWhitespace / 10;
        return Math.Min(8.4 * 100 / perChar, 15);
    }

    public static RenderTargetBitmap Get(char c, int fg, int bg)
    {
        if (Cache.TryGetValue((c, fg, bg), out var bmp)) return bmp;
        bmp = new RenderTargetBitmap(new PixelSize(W, H));
        using (var g = bmp.CreateDrawingContext())
        using (g.PushRenderOptions(Crisp))
        using (g.PushTextOptions(CrispText))
        {
            if (bg >= 0) g.FillRectangle(Cga.Brush(bg), new Rect(0, 0, W, H));
            if (!DrawSpecial(g, c, Cga.Brush(fg)) && c != ' ')
            {
                var text = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Size, Cga.Brush(fg));
                g.DrawText(text, new Point(Math.Round((W - text.WidthIncludingTrailingWhitespace) / 2), Math.Round((H - text.Height) / 2)));
            }
        }
        return Cache[(c, fg, bg)] = bmp;
    }

    // Arms of the box-drawing characters: left, right, up, down; 1 = single line, 2 = double line.
    private static readonly Dictionary<char, (int L, int R, int U, int D)> Lines = new()
    {
        ['─'] = (1, 1, 0, 0), ['│'] = (0, 0, 1, 1), ['┌'] = (0, 1, 0, 1), ['┐'] = (1, 0, 0, 1), ['└'] = (0, 1, 1, 0), ['┘'] = (1, 0, 1, 0),
        ['├'] = (0, 1, 1, 1), ['┤'] = (1, 0, 1, 1), ['┬'] = (1, 1, 0, 1), ['┴'] = (1, 1, 1, 0), ['┼'] = (1, 1, 1, 1),
        ['═'] = (2, 2, 0, 0), ['║'] = (0, 0, 2, 2),
    };

    private static bool DrawSpecial(DrawingContext g, char c, IBrush fg)
    {
        void R(double x, double y, double w, double h) => g.FillRectangle(fg, new Rect(x, y, w, h));
        switch (c)
        {
            case '█': R(0, 0, W, H); return true;
            case '▀': R(0, 0, W, H / 2); return true;
            case '▄': R(0, H / 2, W, H / 2); return true;
            case '▌': R(0, 0, W / 2, H); return true;
            case '▐': R(W / 2, 0, W / 2, H); return true;
            case '■': R(1, 5, 6, 6); return true;
            case '░' or '▒' or '▓':
                for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var on = c switch { '░' => x % 2 == 0 && y % 2 == 0 && (x + y) % 4 == 0 || x % 4 == 2 && y % 4 == 2, '▒' => (x + y) % 2 == 0, _ => !(x % 2 == 0 && y % 2 == 0) };
                    if (on) R(x, y, 1, 1);
                }
                return true;
        }
        if (!Lines.TryGetValue(c, out var a)) return false;
        const int cx = 3, cy = 7;
        void Arm(int kind, bool horizontal, bool toStart)
        {
            if (kind == 0) return;
            var offsets = kind == 2 ? new[] { -1, 2 } : new[] { 0 };
            foreach (var o in offsets)
            {
                if (horizontal) { if (toStart) R(0, cy + o, cx + 1, 1); else R(cx, cy + o, W - cx, 1); }
                else { if (toStart) R(cx + o, 0, 1, cy + 1); else R(cx + o, cy, 1, H - cy); }
            }
        }
        Arm(a.L, true, true); Arm(a.R, true, false); Arm(a.U, false, true); Arm(a.D, false, false);
        return true;
    }

    /// <summary>Writes text at a pixel position (not necessarily on the grid). bg -1 leaves the background.</summary>
    public static void Text(DrawingContext g, string text, double x, double y, int fg, int bg = -1)
    {
        foreach (var ch in text)
        {
            if (ch != ' ' || bg >= 0) g.DrawImage(Get(ch, fg, bg), new Rect(x, y, W, H));
            x += W;
        }
    }

    /// <summary>Text centred on a line of the 640-pixel screen.</summary>
    public static void Centre(DrawingContext g, string text, double y, int fg, int bg = -1) => Text(g, text, Math.Round((DosScreen.Width - text.Length * W) / 2.0), y, fg, bg);
}

/// <summary>An 80x25 text screen with a cursor.</summary>
public sealed class TextGrid
{
    public const int Cols = 80, Rows = 25;
    private readonly char[] _ch = new char[Cols * Rows];
    private readonly byte[] _fg = new byte[Cols * Rows], _bg = new byte[Cols * Rows];
    public int X, Y, Fg = 7, Bg;
    public bool CursorVisible = true;
    /// <summary>Lines scrolled off the top so far (the line editor uses it to follow its input).</summary>
    public int Scrolled;

    public TextGrid() => Clear();

    public void Clear()
    {
        Array.Fill(_ch, ' ');
        Array.Fill(_fg, (byte)Fg);
        Array.Fill(_bg, (byte)Bg);
        X = Y = 0;
    }

    public void Put(int x, int y, char c, int fg, int bg)
    {
        if (x < 0 || y < 0 || x >= Cols || y >= Rows) return;
        var i = y * Cols + x;
        _ch[i] = c; _fg[i] = (byte)fg; _bg[i] = (byte)bg;
    }

    public void Text(int x, int y, string s, int fg, int bg)
    {
        for (var i = 0; i < s.Length; i++) Put(x + i, y, s[i], fg, bg);
    }

    public void Fill(int x, int y, int w, int h, char c, int fg, int bg)
    {
        for (var j = y; j < y + h; j++)
        for (var i = x; i < x + w; i++) Put(i, j, c, fg, bg);
    }

    public void Box(int x, int y, int w, int h, int fg, int bg, string? title = null)
    {
        Fill(x, y, w, h, ' ', fg, bg);
        for (var i = x + 1; i < x + w - 1; i++) { Put(i, y, '─', fg, bg); Put(i, y + h - 1, '─', fg, bg); }
        for (var j = y + 1; j < y + h - 1; j++) { Put(x, j, '│', fg, bg); Put(x + w - 1, j, '│', fg, bg); }
        Put(x, y, '┌', fg, bg); Put(x + w - 1, y, '┐', fg, bg); Put(x, y + h - 1, '└', fg, bg); Put(x + w - 1, y + h - 1, '┘', fg, bg);
        if (title is not null) Text(x + (w - title.Length - 2) / 2, y, $" {title} ", fg, bg);
    }

    public void Write(string s)
    {
        foreach (var c in s)
        {
            if (c == '\n') { X = 0; NewLine(); continue; }
            if (c == '\r') { X = 0; continue; }
            Put(X, Y, c, Fg, Bg);
            if (++X >= Cols) { X = 0; NewLine(); }
        }
    }

    private void NewLine()
    {
        if (++Y < Rows) return;
        Y = Rows - 1;
        Array.Copy(_ch, Cols, _ch, 0, Cols * (Rows - 1));
        Array.Copy(_fg, Cols, _fg, 0, Cols * (Rows - 1));
        Array.Copy(_bg, Cols, _bg, 0, Cols * (Rows - 1));
        for (var i = 0; i < Cols; i++) { var k = (Rows - 1) * Cols + i; _ch[k] = ' '; _fg[k] = (byte)Fg; _bg[k] = (byte)Bg; }
        Scrolled++;
    }

    public void Draw(DrawingContext g, bool cursorOn)
    {
        for (var y = 0; y < Rows; y++)
        for (var x = 0; x < Cols; x++)
        {
            var i = y * Cols + x;
            g.DrawImage(Glyphs.Get(_ch[i], _fg[i], _bg[i]), new Rect(x * Glyphs.W, y * Glyphs.H, Glyphs.W, Glyphs.H));
        }
        if (cursorOn && CursorVisible && Y < Rows)
            g.FillRectangle(Cga.Brush(_fg[Math.Min(Y * Cols + X, _fg.Length - 1)] is var f && f == _bg[Math.Min(Y * Cols + X, _bg.Length - 1)] ? 7 : f), new Rect(X * Glyphs.W, Y * Glyphs.H + 13, Glyphs.W, 2));
    }
}

/// <summary>A full-screen program run from the prompt: a screen saver, a game, DEFRAG.</summary>
public abstract class DosProgram
{
    protected static readonly Random Rnd = new();
    public DosScreen Screen { get; set; } = null!;
    /// <summary>Set to go back to the prompt.</summary>
    public bool Done { get; protected set; }
    /// <summary>Screen savers stop at any key or mouse movement.</summary>
    public virtual bool IsSaver => false;
    /// <summary>Printed at the prompt after the program ends.</summary>
    public string? ExitMessage { get; set; }

    /// <summary>
    /// Draw at a fraction of the 640x400 screen (2 = 320x200) without smoothing, so shapes come out as chunky
    /// pixels. Drawing still uses 640x400 coordinates.
    /// </summary>
    public virtual int PixelScale => 1;

    public virtual void Start() { }
    public abstract void Update(double dt);
    public abstract void Draw(DrawingContext g);
    /// <summary>Drawn at full resolution over <see cref="Draw"/>: text in the VGA font stays readable.</summary>
    public virtual void DrawOverlay(DrawingContext g) { }
    public virtual void KeyDown(Key key, KeyModifiers mods) { }
    /// <summary>A program with its own mouse pointer: the real one is hidden over the screen.</summary>
    public virtual bool WantsMouse => false;
    /// <summary>Escape goes to <see cref="KeyDown"/> instead of leaving the program.</summary>
    public virtual bool HandlesEscape => false;
    /// <summary>Mouse positions are in the 640x400 frame.</summary>
    public virtual void PointerMoved(Point p) { }
    public virtual void PointerPressed(Point p, int clicks) { }
    public virtual void PointerReleased(Point p) { }
    public virtual void Wheel(Point p, double dy) { }
    public virtual void TextInput(string text) { }
    public virtual void Stop() { }

    protected bool Held(Key key) => Screen.Held.Contains(key);
    protected static double R(double min, double max) => min + Rnd.NextDouble() * (max - min);
}

/// <summary>
/// The pretend monitor: everything is drawn into a 640x400 frame which is stretched to 4:3 with square, unsmoothed
/// pixels, like a VGA text mode on a CRT.
/// </summary>
public sealed class DosScreen : Control, IDosConsole
{
    public new const int Width = 640, Height = 400;
    private readonly RenderTargetBitmap _frame = new(new PixelSize(Width, Height));
    private readonly RenderTargetBitmap _lowRes = new(new PixelSize(Width / 2, Height / 2));
    // A small copy of the frame, stretched smoothly over it: the soft glow of phosphor.
    private readonly RenderTargetBitmap _glow = new(new PixelSize(Width / 4, Height / 4));
    private RenderTargetBitmap? _tube;
    private string _tubeKey = "";
    private static readonly RenderOptions Chunky = new() { EdgeMode = EdgeMode.Aliased, BitmapInterpolationMode = BitmapInterpolationMode.None };
    private static readonly TextOptions ChunkyText = new() { TextRenderingMode = TextRenderingMode.Alias };
    private readonly TextGrid _grid = new();
    private readonly DosShell _shell;
    private readonly Func<string, string[], DosProgram?> _programs;
    private readonly Func<string, string[], string?> _textCommands;
    private readonly List<string> _history = [];
    private DosProgram? _program;
    private string _input = "";
    private int _caret, _historyAt, _lineX, _lineY, _lineScrolled, _drawnLength;
    private bool _lineShown, _running = true;
    private TimeSpan? _lastFrame;
    private double _blink;
    private Point? _saverMouse;
    public readonly HashSet<Key> Held = [];

    public DosScreen(Func<string, string[], DosProgram?> programs, Func<string, string[], string?> textCommands, Action exit)
    {
        _programs = programs;
        _textCommands = textCommands;
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        _shell = new DosShell(this, Launch, exit);
        _shell.Start();
        ShowLine();
        // A theme change can rebuild the window frame around the screen; keep the keyboard when that happens.
        AttachedToVisualTree += (_, _) => { _running = true; RequestFrame(); Dispatcher.UIThread.Post(() => Focus()); };
        DetachedFromVisualTree += (_, _) => _running = false;
    }

    public DosShell Shell => _shell;

    // IDosConsole, for the shell and AVAZORK.
    void IDosConsole.Write(string text)
    {
        // Words that would run past the edge start a new line, as a text adventure expects.
        var i = 0;
        while (i < text.Length)
        {
            var end = i;
            while (end < text.Length && text[end] is not (' ' or '\n')) end++;
            var word = text[i..end];
            if (_grid.X > 0 && word.Length < TextGrid.Cols && _grid.X + word.Length > TextGrid.Cols) _grid.Write("\n");
            _grid.Write(word);
            if (end < text.Length && !(text[end] == ' ' && _grid.X == 0 && word.Length > 0)) _grid.Write(text[end].ToString());
            i = end + 1;
        }
    }
    void IDosConsole.Color(int fg, int bg) { _grid.Fg = fg; _grid.Bg = bg; }
    void IDosConsole.Clear() => _grid.Clear();
    void IDosConsole.Beep() => Beep();

    public static void Beep() => Dispatcher.UIThread.Post(() => App.Services.Sounds.Play(Core.Theming.SoundEvent.Timer, force: true));

    private string? Launch(string name, string[] args)
    {
        if (_textCommands(name, args) is { } text) return text;
        var program = _programs(name, args);
        if (program is null) return "This program cannot be run in DOS mode.\n";
        Run(program);
        return null;
    }

    public void Run(DosProgram program)
    {
        _program?.Stop();
        _program = program;
        program.Screen = this;
        Held.Clear();
        _saverMouse = null;
        Cursor = program.WantsMouse ? new Cursor(StandardCursorType.None) : null;
        program.Start();
    }

    private void EndProgram()
    {
        var p = _program;
        if (p is null) return;
        _program = null;
        p.Stop();
        Cursor = null;
        _grid.Fg = 7; _grid.Bg = 0;
        // The shell already left a blank line after the command; a closing message gets one of its own.
        if (p.ExitMessage is { } m) _grid.Write(m.Trim('\n') + "\n\n");
        _lineShown = false;
        ShowLine();
    }

    private void RequestFrame()
    {
        if (!_running) return;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 1 / 60.0;
        _lastFrame = now;
        _blink += dt;
        using (var g = _frame.CreateDrawingContext())
        using (g.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            if (_program is { } p)
            {
                try
                {
                    p.Update(dt);
                    if (!p.Done) DrawProgram(g, p);
                }
                catch (Exception ex)
                {
                    Core.Logging.Log.Error("drive-a", "A program crashed", ex);
                    p.ExitMessage = "General Protection Fault. (" + ex.Message + ")\n";
                    EndProgram();
                }
                if (_program is { Done: true }) EndProgram();
            }
            if (_program is null)
            {
                if (_shell.Prompt != "" && !_lineShown) ShowLine();
                _grid.Draw(g, _blink % 0.5 < 0.27);
            }
        }
        using (var g = _glow.CreateDrawingContext())
        using (g.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            g.DrawImage(_frame, new Rect(0, 0, Width, Height), new Rect(0, 0, Width / 4.0, Height / 4.0));
        InvalidateVisual();
        RequestFrame();
    }

    private void DrawProgram(DrawingContext g, DosProgram p)
    {
        if (p.PixelScale <= 1) { p.Draw(g); p.DrawOverlay(g); return; }
        var scale = 1.0 / p.PixelScale;
        using (var low = _lowRes.CreateDrawingContext())
        using (low.PushRenderOptions(Chunky))
        using (low.PushTextOptions(ChunkyText))
        using (low.PushTransform(Matrix.CreateScale(scale, scale)))
            p.Draw(low);
        g.DrawImage(_lowRes, new Rect(0, 0, _lowRes.PixelSize.Width, _lowRes.PixelSize.Height), new Rect(0, 0, Width, Height));
        p.DrawOverlay(g);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        // 640x400 shown at 4:3, as a 640x400 text mode was on a 4:3 monitor.
        var scale = Math.Min(Bounds.Width / 640, Bounds.Height / 480);
        var w = 640 * scale;
        var h = 480 * scale;
        var screen = new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        context.DrawImage(_frame, new Rect(0, 0, Width, Height), screen);
        DrawTube(context, screen);
    }

    /// <summary>
    /// The CRT look, like the terminal's (terminal.crtIntensity sets its strength, terminal.crtFlicker its
    /// flicker): a glow around everything bright, a dark line under every row of pixels, a vignette and edges
    /// that fall into shadow. Always on here: this screen is an old monitor.
    /// </summary>
    private void DrawTube(DrawingContext context, Rect screen)
    {
        var terminal = App.Services.Config.Settings.Terminal;
        var ci = Math.Clamp(terminal.CrtIntensity, 0, 100) / 100.0;
        if (ci <= 0) return;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality, BitmapBlendingMode = BitmapBlendingMode.Screen }))
        using (context.PushOpacity(Math.Min(1, ci * 0.9)))
            context.DrawImage(_glow, new Rect(0, 0, _glow.PixelSize.Width, _glow.PixelSize.Height), screen);

        var key = $"{screen.Width:0}x{screen.Height:0}@{ci}";
        if (_tube is null || key != _tubeKey)
        {
            _tube?.Dispose();
            _tube = BuildTube(screen.Size, ci);
            _tubeKey = key;
        }
        var opacity = terminal.CrtFlicker ? Flicker(Environment.TickCount64 % 5000 / 5000.0) : 1;
        using (context.PushOpacity(opacity))
            context.DrawImage(_tube, new Rect(0, 0, _tube.PixelSize.Width, _tube.PixelSize.Height), screen);
    }

    // The terminal's flicker: brief dips at the same points of a five-second cycle.
    private static double Flicker(double t) => t switch { >= 0.07 and < 0.08 => 0.93, >= 0.52 and < 0.53 => 0.96, >= 0.81 and < 0.82 => 0.92, _ => 1 };

    private static RenderTargetBitmap BuildTube(Size size, double ci)
    {
        var (w, h) = (Math.Max(1, (int)Math.Round(size.Width)), Math.Max(1, (int)Math.Round(size.Height)));
        var bmp = new RenderTargetBitmap(new PixelSize(w, h));
        using var g = bmp.CreateDrawingContext();
        // Scanlines: the lower third of each of the 400 rows of pixels is darker.
        var row = (double)h / Height;
        var dark = new SolidColorBrush(Color.FromArgb((byte)(255 * ci * 0.45), 0, 0, 0));
        for (var y = 0; y < Height; y++) g.FillRectangle(dark, new Rect(0, (y + 0.62) * row, w, Math.Max(1, row * 0.38)));
        // Vignette: clear in the middle, darker towards the corners.
        var vignette = new RadialGradientBrush
        {
            RadiusX = new RelativeScalar(Math.Sqrt(0.5), RelativeUnit.Relative), RadiusY = new RelativeScalar(Math.Sqrt(0.5), RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55), new GradientStop(Color.FromArgb((byte)(255 * ci * 0.55), 0, 0, 0), 1) },
        };
        g.FillRectangle(vignette, new Rect(0, 0, w, h));
        // Edges: a shadow falling inwards from each side.
        var d = ci * 50;
        var edge = Color.FromArgb((byte)(255 * ci * 0.3), 0, 0, 0);
        var mid = Color.FromArgb((byte)(255 * ci * 0.09), 0, 0, 0);
        foreach (var (start, end, rect) in new[]
                 {
                     (new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative), new Rect(0, 0, d, h)),
                     (new RelativePoint(1, 0, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative), new Rect(w - d, 0, d, h)),
                     (new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative), new Rect(0, 0, w, d)),
                     (new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative), new Rect(0, h - d, w, d)),
                 })
        {
            g.FillRectangle(new LinearGradientBrush
            {
                StartPoint = start, EndPoint = end,
                GradientStops = { new GradientStop(edge, 0), new GradientStop(mid, 0.4), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
            }, rect);
        }
        return bmp;
    }

    /// <summary>Converts a point in the control to the 640x400 frame.</summary>
    public Point ToFrame(Point p)
    {
        var scale = Math.Min(Bounds.Width / 640, Bounds.Height / 480);
        var x0 = (Bounds.Width - 640 * scale) / 2;
        var y0 = (Bounds.Height - 480 * scale) / 2;
        return new Point((p.X - x0) / scale, (p.Y - y0) / scale * 400 / 480);
    }

    // ----- The line editor at the prompt -----

    /// <summary>Draws the prompt and the line being typed where the prompt began.</summary>
    private void ShowLine()
    {
        if (!_lineShown)
        {
            _lineX = _grid.X; _lineY = _grid.Y; _lineScrolled = _grid.Scrolled; _drawnLength = 0;
            _lineShown = true;
        }
        var y = _lineY - (_grid.Scrolled - _lineScrolled);
        _grid.X = _lineX; _grid.Y = Math.Max(0, y);
        var text = _shell.Prompt + _input;
        _grid.Write(text + new string(' ', Math.Max(0, _drawnLength - text.Length)));
        _drawnLength = text.Length;
        // Put the cursor at the caret.
        var at = _lineX + _shell.Prompt.Length + _caret;
        var lineY = _lineY - (_grid.Scrolled - _lineScrolled);
        _grid.X = at % TextGrid.Cols;
        _grid.Y = Math.Clamp(lineY + at / TextGrid.Cols, 0, TextGrid.Rows - 1);
        _blink = 0;
    }

    private void Submit()
    {
        var line = _input;
        // Rewrite just the prompt; the shell echoes the line itself.
        var y = _lineY - (_grid.Scrolled - _lineScrolled);
        _grid.X = _lineX; _grid.Y = Math.Max(0, y);
        var prompt = _shell.Prompt;
        _grid.Write(prompt + new string(' ', Math.Max(0, _drawnLength - prompt.Length)));
        _grid.X = (_lineX + prompt.Length) % TextGrid.Cols;
        _grid.Y = Math.Max(0, y) + (_lineX + prompt.Length) / TextGrid.Cols;
        _input = "";
        _caret = 0;
        _lineShown = false;
        if (line.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != line) && !_shell.InGame) _history.Add(line);
        _historyAt = _history.Count;
        _shell.Enter(line);
        if (_program is null) ShowLine();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (TopLevel.GetTopLevel(this) is Window w) w.WindowState = w.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            e.Handled = true;
            return;
        }
        if (_program is { } p)
        {
            // Keys that type something are left unhandled, or their text would never arrive.
            e.Handled = e.Key is Key.Escape or Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab or Key.Enter or Key.Back or Key.Delete or Key.Home or Key.End
                || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (p.IsSaver) { e.Handled = true; p.Stop(); EndProgram(); return; }
            Held.Add(e.Key);
            if (e.Key == Key.Escape && !p.HandlesEscape) { EndProgram(); return; }
            p.KeyDown(e.Key, e.KeyModifiers);
            return;
        }
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Enter: Submit(); return;
            case Key.Back when _caret > 0: _input = _input.Remove(_caret - 1, 1); _caret--; break;
            case Key.Delete when _caret < _input.Length: _input = _input.Remove(_caret, 1); break;
            case Key.Left when _caret > 0: _caret--; break;
            case Key.Right when _caret < _input.Length: _caret++; break;
            case Key.Home: _caret = 0; break;
            case Key.End: _caret = _input.Length; break;
            case Key.Escape: _input = ""; _caret = 0; break;
            case Key.Up when _history.Count > 0 && !_shell.InGame: _historyAt = Math.Max(0, _historyAt - 1); _input = _history[_historyAt]; _caret = _input.Length; break;
            case Key.Down when _history.Count > 0 && !_shell.InGame:
                _historyAt = Math.Min(_history.Count, _historyAt + 1);
                _input = _historyAt < _history.Count ? _history[_historyAt] : "";
                _caret = _input.Length;
                break;
            case Key.F3 when _history.Count > 0: _input = _history[^1]; _caret = _input.Length; break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                var y = _lineY - (_grid.Scrolled - _lineScrolled);
                _grid.X = _lineX + _shell.Prompt.Length + _input.Length; _grid.Y = Math.Max(0, y);
                while (_grid.X >= TextGrid.Cols) { _grid.X -= TextGrid.Cols; _grid.Y++; }
                _input = ""; _caret = 0; _lineShown = false;
                _shell.Break();
                break;
            default: e.Handled = false; return;
        }
        ShowLine();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        Held.Remove(e.Key);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        if (_program is { } p) { if (!p.IsSaver) p.TextInput(e.Text); return; }
        foreach (var c in e.Text)
        {
            if (char.IsControl(c) || _input.Length >= 126) continue;
            _input = _input.Insert(_caret++, c.ToString());
        }
        ShowLine();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_program is { WantsMouse: true } mp) { mp.PointerMoved(ClampedFrame(e.GetPosition(this))); return; }
        if (_program is not { IsSaver: true }) return;
        var p = e.GetPosition(this);
        if (_saverMouse is not { } start) { _saverMouse = p; return; }
        if (Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) > 24) EndProgram();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_program is { IsSaver: true }) EndProgram();
        else if (_program is { WantsMouse: true } p && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Pointer.Capture(this);
            p.PointerPressed(ClampedFrame(e.GetPosition(this)), e.ClickCount);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_program is { WantsMouse: true } p && e.InitialPressMouseButton == MouseButton.Left) p.PointerReleased(ClampedFrame(e.GetPosition(this)));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_program is { WantsMouse: true } p) { p.Wheel(ClampedFrame(e.GetPosition(this)), e.Delta.Y); e.Handled = true; }
    }

    private Point ClampedFrame(Point p)
    {
        var f = ToFrame(p);
        return new Point(Math.Clamp(Math.Floor(f.X), 0, Width - 1), Math.Clamp(Math.Floor(f.Y), 0, Height - 1));
    }
}
