namespace Avamux.Core.SystemInfo;

/// <summary>A rectangle of the treemap, in pixels, and the rectangles inside it.</summary>
public sealed class TreeMapTile(DiskNode node, double x, double y, double w, double h)
{
    public readonly DiskNode Node = node;
    public readonly double X = x, Y = y, W = w, H = h;
    public TreeMapTile[] Kids = [];

    public bool Contains(double px, double py) => px >= X && py >= Y && px < X + W && py < Y + H;

    /// <summary>The innermost tile under a point, and the chain of tiles leading to it.</summary>
    public List<TreeMapTile> PathTo(double px, double py)
    {
        var path = new List<TreeMapTile>();
        if (!Contains(px, py)) return path;
        var t = this;
        while (true)
        {
            path.Add(t);
            var next = t.Kids.FirstOrDefault(k => k.Contains(px, py));
            if (next is null) return path;
            t = next;
        }
    }

    /// <summary>The tile showing <paramref name="node"/>, or the innermost one around it when it is too small to have its own.</summary>
    public TreeMapTile? Find(DiskNode node)
    {
        var chain = node.Ancestry();
        var start = chain.FindIndex(n => ReferenceEquals(n, Node));
        if (start < 0) return null;
        var t = this;
        for (var i = start + 1; i < chain.Count; i++)
        {
            var next = t.Kids.FirstOrDefault(k => ReferenceEquals(k.Node, chain[i]));
            if (next is null) break;
            t = next;
        }
        return t;
    }
}

/// <summary>
/// A squarified cushion treemap (van Wijk and van de Wetering), painted into a pixel buffer: every file is a
/// rectangle sized to the space it takes, folders nest, and each level is shaded like a pillow so the nesting shows.
/// </summary>
public static class TreeMap
{
    private const double Height = 0.38, Falloff = 0.91, Ambient = 0.15;
    private static readonly double Lx, Ly, Lz;

    static TreeMap()
    {
        var len = Math.Sqrt(1 + 1 + 100);
        (Lx, Ly, Lz) = (-1 / len, -1 / len, 10 / len);
    }

    /// <summary>
    /// Lays out <paramref name="view"/> over a <paramref name="width"/> × <paramref name="height"/> buffer of
    /// 0xAARRGGBB pixels, colouring each rectangle with <paramref name="colour"/> (files, and folders too small to
    /// open up or not read yet). Folder totals may still be growing: the sizes are read once, as they stand.
    /// </summary>
    public static TreeMapTile Render(DiskNode view, int width, int height, uint[] pixels, uint background, Func<DiskNode, uint> colour, CancellationToken cancel = default)
    {
        Array.Fill(pixels, background, 0, Math.Min(pixels.Length, width * height));
        var root = new TreeMapTile(view, 0, 0, width, height);
        if (width <= 0 || height <= 0 || view.Size <= 0) return root;
        var ctx = new Context(pixels, width, height, colour, cancel);
        Lay(ctx, root, new double[4], Height);
        return root;
    }

    private sealed record Context(uint[] Pixels, int Width, int Height, Func<DiskNode, uint> Colour, CancellationToken Cancel);

    private static void Lay(Context ctx, TreeMapTile tile, double[] surface, double h)
    {
        ctx.Cancel.ThrowIfCancellationRequested();
        AddRidge(tile, surface, h);
        var node = tile.Node;
        var children = node.Children;
        if (!node.IsFolder || children.Length == 0 || tile.W * tile.H < 6 || tile.W < 2 || tile.H < 2)
        {
            Paint(ctx, tile, surface, ctx.Colour(node));
            return;
        }
        // Sizes as they stand right now, biggest first.
        var items = new List<(DiskNode Node, long Size)>(children.Length);
        foreach (var c in children)
        {
            var size = Interlocked.Read(ref c.Size);
            if (size > 0) items.Add((c, size));
        }
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (items.Count == 0) { Paint(ctx, tile, surface, ctx.Colour(node)); return; }
        var kids = new List<TreeMapTile>(Math.Min(items.Count, 4096));
        Squarify(items, tile.X, tile.Y, tile.W, tile.H, (i, x, y, w, hh) =>
        {
            // Too small to cover a pixel centre: nothing to draw or point at.
            if (Math.Round(x + w) <= Math.Round(x) || Math.Round(y + hh) <= Math.Round(y)) return;
            var kid = new TreeMapTile(items[i].Node, x, y, w, hh);
            kids.Add(kid);
            Lay(ctx, kid, (double[])surface.Clone(), h * Falloff);
        });
        tile.Kids = kids.ToArray();
    }

    /// <summary>The squarified layout: rows along the shorter side, each kept as square as it can be.</summary>
    internal static void Squarify(List<(DiskNode Node, long Size)> items, double x, double y, double w, double h, Action<int, double, double, double, double> place)
    {
        double remaining = 0;
        foreach (var it in items) remaining += it.Size;
        var i = 0;
        while (i < items.Count && w > 0 && h > 0 && remaining > 0)
        {
            var wide = w >= h;
            var side = wide ? h : w;
            var scale = w * h / remaining;
            var largest = items[i].Size * scale;
            double rowSum = 0, worst = double.MaxValue;
            var j = i;
            while (j < items.Count)
            {
                var s = items[j].Size * scale;
                var sum = rowSum + s;
                var sq = sum * sum;
                var side2 = side * side;
                var ratio = Math.Max(Math.Max(side2 * largest / sq, sq / (side2 * largest)), Math.Max(side2 * s / sq, sq / (side2 * s)));
                if (j > i && ratio > worst) break;
                worst = ratio;
                rowSum = sum;
                j++;
            }
            // The last row takes whatever is left, so rounding never leaves a sliver unpainted.
            var thick = j == items.Count ? (wide ? w : h) : rowSum / side;
            double pos = 0;
            for (var k = i; k < j; k++)
            {
                var len = k == j - 1 ? side - pos : items[k].Size * scale / thick;
                if (wide) place(k, x, y + pos, thick, len);
                else place(k, x + pos, y, len, thick);
                pos += len;
            }
            if (wide) { x += thick; w -= thick; } else { y += thick; h -= thick; }
            remaining -= rowSum / scale;
            i = j;
        }
    }

    private static void AddRidge(TreeMapTile t, double[] s, double h)
    {
        if (t.W > 0) { s[2] += 4 * h * (2 * t.X + t.W) / t.W; s[0] -= 4 * h / t.W; }
        if (t.H > 0) { s[3] += 4 * h * (2 * t.Y + t.H) / t.H; s[1] -= 4 * h / t.H; }
    }

    private static void Paint(Context ctx, TreeMapTile t, double[] s, uint colour)
    {
        int x0 = (int)Math.Round(t.X), x1 = Math.Min(ctx.Width, (int)Math.Round(t.X + t.W));
        int y0 = (int)Math.Round(t.Y), y1 = Math.Min(ctx.Height, (int)Math.Round(t.Y + t.H));
        double r = (colour >> 16) & 0xff, g = (colour >> 8) & 0xff, b = colour & 0xff;
        var flat = (colour >> 24) != 0xff; // not fully opaque: drawn without the cushion, opaque
        for (var py = Math.Max(0, y0); py < y1; py++)
        {
            var ny = -(2 * s[1] * (py + 0.5) + s[3]);
            var row = py * ctx.Width;
            for (var px = Math.Max(0, x0); px < x1; px++)
            {
                if (flat) { ctx.Pixels[row + px] = colour | 0xff000000; continue; }
                var nx = -(2 * s[0] * (px + 0.5) + s[2]);
                var cos = (nx * Lx + ny * Ly + Lz) / Math.Sqrt(nx * nx + ny * ny + 1);
                var light = (Ambient + (1 - Ambient) * Math.Max(0, cos)) * 1.18;
                ctx.Pixels[row + px] = 0xff000000 | (Byte(r * light) << 16) | (Byte(g * light) << 8) | Byte(b * light);
            }
        }
    }

    private static uint Byte(double v) => v >= 255 ? 255u : v <= 0 ? 0u : (uint)v;
}
