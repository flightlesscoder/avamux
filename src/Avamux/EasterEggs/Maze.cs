using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avamux.Views;

namespace Avamux.EasterEggs;

/// <summary>
/// MAZE.EXE: a walk through a brick maze, seen in first person. The walker keeps a hand on the right-hand wall.
/// Spinning avamux logos float in the corridors; walking into one turns the world upside down. Reaching the
/// exit sign starts a new maze.
/// </summary>
public sealed class Maze : DosProgram
{
    private const int Cells = 8, Size = Cells * 2 + 1, Columns = 320, ViewH = DosScreen.Height;
    private bool[,] _wall = new bool[Size, Size];
    private static readonly int[] Dx = [1, 0, -1, 0], Dy = [0, 1, 0, -1];
    private double _x, _y, _angle, _targetAngle, _roll, _rollTarget, _t;
    private int _dir, _cellX, _cellY, _goalX, _goalY;
    private (int X, int Y)? _moveTo;
    private readonly List<(double X, double Y, bool Exit)> _sprites = [];
    private readonly double[] _depth = new double[Columns];
    private static readonly IBrush[,] Brick = BuildShades("#b4532a", "#86391b");
    private static readonly IBrush[,] Mortar = BuildShades("#5a5a5a", "#404040");
    private static readonly IBrush Ceiling = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#20263a"), 0), new GradientStop(Color.Parse("#05060a"), 1) },
    }.ToImmutable();
    private static readonly IBrush Floor = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#0a0a0a"), 0), new GradientStop(Color.Parse("#5b5249"), 1) },
    }.ToImmutable();
    private static readonly Bitmap? Logo = AppIcon.Bitmap;
    private static RenderTargetBitmap? _exitSign;

    public override bool IsSaver => true;

    private static IBrush[,] BuildShades(string lit, string shaded)
    {
        var shades = new IBrush[2, 24];
        for (var side = 0; side < 2; side++)
        {
            var c = Color.Parse(side == 0 ? lit : shaded);
            for (var i = 0; i < 24; i++)
            {
                var k = (i + 1) / 24.0;
                shades[side, i] = new ImmutableSolidColorBrush(Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k)));
            }
        }
        return shades;
    }

    public override void Start() => NewMaze();

    private void NewMaze()
    {
        _wall = new bool[Size, Size];
        for (var y = 0; y < Size; y++) for (var x = 0; x < Size; x++) _wall[x, y] = true;
        // Depth-first carving makes a perfect maze: one path between any two cells.
        var stack = new Stack<(int, int)>();
        var seen = new bool[Cells, Cells];
        stack.Push((0, 0));
        seen[0, 0] = true;
        _wall[1, 1] = false;
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Peek();
            var options = Enumerable.Range(0, 4).Where(d => cx + Dx[d] is >= 0 and < Cells && cy + Dy[d] is >= 0 and < Cells && !seen[cx + Dx[d], cy + Dy[d]]).ToList();
            if (options.Count == 0) { stack.Pop(); continue; }
            var dir = options[Rnd.Next(options.Count)];
            var (nx, ny) = (cx + Dx[dir], cy + Dy[dir]);
            seen[nx, ny] = true;
            _wall[cx * 2 + 1 + Dx[dir], cy * 2 + 1 + Dy[dir]] = false;
            _wall[nx * 2 + 1, ny * 2 + 1] = false;
            stack.Push((nx, ny));
        }

        // The exit is the cell furthest from the start.
        var dist = new int[Cells, Cells];
        foreach (var i in Enumerable.Range(0, Cells * Cells)) dist[i % Cells, i / Cells] = -1;
        var queue = new Queue<(int, int)>();
        queue.Enqueue((0, 0));
        dist[0, 0] = 0;
        (int X, int Y) far = (0, 0);
        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            if (dist[cx, cy] > dist[far.X, far.Y]) far = (cx, cy);
            for (var d = 0; d < 4; d++)
            {
                var (nx, ny) = (cx + Dx[d], cy + Dy[d]);
                if (nx is < 0 or >= Cells || ny is < 0 or >= Cells || dist[nx, ny] >= 0 || _wall[cx * 2 + 1 + Dx[d], cy * 2 + 1 + Dy[d]]) continue;
                dist[nx, ny] = dist[cx, cy] + 1;
                queue.Enqueue((nx, ny));
            }
        }
        (_goalX, _goalY) = (far.X * 2 + 1, far.Y * 2 + 1);

        _sprites.Clear();
        _sprites.Add((_goalX + 0.5, _goalY + 0.5, true));
        var cells = Enumerable.Range(0, Cells * Cells).Select(i => (X: i % Cells, Y: i / Cells)).Where(c => c != (0, 0) && c != far).OrderBy(_ => Rnd.Next()).Take(4);
        foreach (var c in cells) _sprites.Add((c.X * 2 + 1.5, c.Y * 2 + 1.5, false));

        (_cellX, _cellY) = (1, 1);
        (_x, _y) = (1.5, 1.5);
        _dir = !_wall[2, 1] ? 0 : 1;
        _angle = _targetAngle = _dir * Math.PI / 2;
        _moveTo = null;
    }

    public override void Update(double dt)
    {
        _t += dt;
        _roll += Math.Clamp(_rollTarget - _roll, -dt * Math.PI * 1.2, dt * Math.PI * 1.2);

        // Turning first, then walking.
        var turn = _targetAngle - _angle;
        if (Math.Abs(turn) > 1e-3)
        {
            _angle += Math.Clamp(turn, -dt * 2.6, dt * 2.6);
            return;
        }
        _angle = _targetAngle;
        if (_moveTo is { } to)
        {
            var (tx, ty) = (to.X + 0.5, to.Y + 0.5);
            var step = dt * 1.7;
            var (dx, dy) = (tx - _x, ty - _y);
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= step) { (_x, _y) = (tx, ty); (_cellX, _cellY) = to; _moveTo = null; Arrived(); }
            else { _x += dx / d * step; _y += dy / d * step; }
            return;
        }
        // At a cell: right hand on the wall. Try right, straight on, left, then back.
        foreach (var rel in new[] { 1, 0, 3, 2 })
        {
            var d = (_dir + rel) % 4;
            if (_wall[_cellX + Dx[d], _cellY + Dy[d]]) continue;
            var delta = rel switch { 1 => Math.PI / 2, 3 => -Math.PI / 2, 2 => Math.PI, _ => 0 };
            _targetAngle = _angle + delta;
            _dir = d;
            _moveTo = (_cellX + Dx[d] * 2, _cellY + Dy[d] * 2);
            break;
        }
    }

    private void Arrived()
    {
        if (_cellX == _goalX && _cellY == _goalY) { NewMaze(); return; }
        var hit = _sprites.FindIndex(s => !s.Exit && Math.Abs(s.X - (_x)) < 0.6 && Math.Abs(s.Y - _y) < 0.6);
        if (hit >= 0)
        {
            _sprites.RemoveAt(hit);
            _rollTarget = _rollTarget == 0 ? Math.PI : 0;
        }
    }

    public override void Draw(DrawingContext g)
    {
        var w = (double)DosScreen.Width;
        using var roll = g.PushTransform(Matrix.CreateTranslation(-w / 2, -ViewH / 2.0) * Matrix.CreateRotation(_roll) * Matrix.CreateTranslation(w / 2, ViewH / 2.0));
        g.FillRectangle(Ceiling, new Rect(-20, -20, w + 40, ViewH / 2.0 + 20));
        g.FillRectangle(Floor, new Rect(-20, ViewH / 2.0, w + 40, ViewH / 2.0 + 20));

        var (dirX, dirY) = (Math.Cos(_angle), Math.Sin(_angle));
        var (planeX, planeY) = (-dirY * 0.7, dirX * 0.7);
        var colW = w / Columns;
        for (var col = 0; col < Columns; col++)
        {
            var camera = 2.0 * col / Columns - 1;
            var (rayX, rayY) = (dirX + planeX * camera, dirY + planeY * camera);
            int mapX = (int)_x, mapY = (int)_y;
            var deltaX = rayX == 0 ? 1e30 : Math.Abs(1 / rayX);
            var deltaY = rayY == 0 ? 1e30 : Math.Abs(1 / rayY);
            int stepX = rayX < 0 ? -1 : 1, stepY = rayY < 0 ? -1 : 1;
            var sideX = rayX < 0 ? (_x - mapX) * deltaX : (mapX + 1 - _x) * deltaX;
            var sideY = rayY < 0 ? (_y - mapY) * deltaY : (mapY + 1 - _y) * deltaY;
            var side = 0;
            for (var guard = 0; guard < 64; guard++)
            {
                if (sideX < sideY) { sideX += deltaX; mapX += stepX; side = 0; }
                else { sideY += deltaY; mapY += stepY; side = 1; }
                if (mapX < 0 || mapY < 0 || mapX >= Size || mapY >= Size || _wall[mapX, mapY]) break;
            }
            var dist = Math.Max(0.05, side == 0 ? sideX - deltaX : sideY - deltaY);
            _depth[col] = dist;
            var h = ViewH / dist * 0.9;
            var top = ViewH / 2.0 - h / 2;
            var u = side == 0 ? _y + dist * rayY : _x + dist * rayX;
            u -= Math.Floor(u);
            var shade = Math.Clamp((int)(24 / (1 + dist * 0.35)) - 1, 0, 23);
            var x = col * colW;
            g.FillRectangle(Brick[side, shade], new Rect(x, top, colW + 0.5, h));
            // Mortar: four courses of bricks per wall, every other course shifted by half a brick.
            var mortar = Mortar[side, shade];
            var line = Math.Max(1, h / 70);
            for (var k = 0; k < 4; k++)
            {
                var y0 = top + h * k / 4;
                g.FillRectangle(mortar, new Rect(x, y0, colW + 0.5, line));
                var f = (u * 2 + (k % 2) * 0.5) % 1;
                if (f < 0.045) g.FillRectangle(mortar, new Rect(x, y0, colW + 0.5, h / 4));
            }
        }

        // Sprites, furthest first, cut off where a wall is nearer.
        _exitSign ??= BuildExitSign();
        foreach (var s in _sprites.OrderByDescending(s => (s.X - _x) * (s.X - _x) + (s.Y - _y) * (s.Y - _y)))
        {
            var (sx, sy) = (s.X - _x, s.Y - _y);
            var inv = 1 / (planeX * dirY - dirX * planeY);
            var tx = inv * (dirY * sx - dirX * sy);
            var ty = inv * (-planeY * sx + planeX * sy);
            if (ty <= 0.15) continue;
            var screenX = w / 2 * (1 + tx / ty);
            var size = ViewH / ty * (s.Exit ? 0.6 : 0.45);
            var bob = s.Exit ? 0 : Math.Sin(_t * 2 + s.X) * size * 0.08;
            var spin = s.Exit ? 1 : Math.Abs(Math.Cos(_t * 1.8 + s.Y));
            var width = Math.Max(2, size * spin);
            Bitmap? image = s.Exit ? _exitSign : Logo;
            if (image is null) continue;
            var top = ViewH / 2.0 - size / 2 + bob + (s.Exit ? size * 0.12 : 0);
            var left = screenX - width / 2;
            var first = Math.Max(0, (int)(left / colW));
            var last = Math.Min(Columns - 1, (int)((left + width) / colW));
            var pw = image.PixelSize.Width;
            for (var col = first; col <= last; col++)
            {
                if (ty >= _depth[col]) continue;
                var x = col * colW;
                var u0 = Math.Clamp((x - left) / width, 0, 1);
                var u1 = Math.Clamp((x + colW - left) / width, 0, 1);
                if (u1 <= u0) continue;
                g.DrawImage(image, new Rect(u0 * pw, 0, Math.Max(1, (u1 - u0) * pw), image.PixelSize.Height), new Rect(x, top, colW + 0.5, size));
            }
        }
    }

    private static RenderTargetBitmap BuildExitSign()
    {
        var bmp = new RenderTargetBitmap(new PixelSize(64, 64));
        using var g = bmp.CreateDrawingContext();
        g.DrawRectangle(new SolidColorBrush(Color.Parse("#0b5d1e")), new Pen(Brushes.White, 2), new Rect(4, 14, 56, 28), 4, 4);
        Glyphs.Text(g, "EXIT", 16, 20, 15);
        g.FillRectangle(Brushes.Gray, new Rect(30, 42, 4, 22));
        return bmp;
    }
}
