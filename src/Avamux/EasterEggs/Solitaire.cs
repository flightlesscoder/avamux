using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;

namespace Avamux.EasterEggs;

/// <summary>
/// SOL.EXE: the best bit of a game of patience, the end. The game is already won; the cards leave the foundations
/// one at a time and bounce away across the table, leaving a trail of themselves.
/// </summary>
public sealed class Solitaire : DosProgram
{
    private const int CardW = 50, CardH = 70, Top = 14;
    private static readonly string[] Ranks = ["A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K"];
    private static readonly string Suits = "♠♥♣♦";
    private static readonly IBrush Felt = new ImmutableSolidColorBrush(Color.Parse("#007a00"));
    private static readonly IPen Slot = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#00a000")), 1.5);
    private static readonly Dictionary<(int, int), RenderTargetBitmap> Faces = new();
    private static RenderTargetBitmap? _back;
    private RenderTargetBitmap _table = null!;
    private readonly int[] _left = [13, 13, 13, 13];
    private int _next; // cards launched so far
    private double _x, _y, _vx, _vy, _wait;
    private bool _flying;
    private (int Suit, int Rank) _card;

    public override bool IsSaver => true;

    private static double PileX(int i) => DosScreen.Width - 14 - (4 - i) * (CardW + 12) + 12;

    public override void Start() => Reset();

    private void Reset()
    {
        _table = new RenderTargetBitmap(new PixelSize(DosScreen.Width, DosScreen.Height));
        using var g = _table.CreateDrawingContext();
        g.FillRectangle(Felt, new Rect(0, 0, DosScreen.Width, DosScreen.Height));
        g.DrawImage(Back(), new Rect(14, Top, CardW, CardH));
        g.DrawRectangle(null, Slot, new Rect(14 + CardW + 12, Top, CardW, CardH), 4, 4);
        for (var i = 0; i < 7; i++) g.DrawRectangle(null, Slot, new Rect(14 + i * (CardW + 12), Top + CardH + 26, CardW, CardH), 4, 4);
        for (var i = 0; i < 4; i++) { _left[i] = 13; g.DrawImage(Face(i, 12), new Rect(PileX(i), Top, CardW, CardH)); }
        _next = 0;
        _flying = false;
        _wait = 1.0;
    }

    public override void Update(double dt)
    {
        if (!_flying)
        {
            _wait -= dt;
            if (_wait > 0) return;
            if (_next >= 52) { Reset(); return; }
            Launch();
        }
        // Small steps so the trail is the same at any frame rate.
        const double step = 1 / 60.0;
        for (var t = 0.0; t < dt && _flying; t += step)
        {
            _vy += 1150 * step;
            _x += _vx * step;
            _y += _vy * step;
            if (_y + CardH > DosScreen.Height)
            {
                _y = DosScreen.Height - CardH;
                _vy = -_vy * R(0.72, 0.86);
            }
            using (var g = _table.CreateDrawingContext(false))
                g.DrawImage(Face(_card.Suit, _card.Rank), new Rect(Math.Round(_x), Math.Round(_y), CardW, CardH));
            if (_x < -CardW || _x > DosScreen.Width) { _flying = false; _wait = 0.05; }
        }
    }

    private void Launch()
    {
        // Kings first, one from each foundation in turn, then the queens, and so on.
        var pile = _next % 4;
        _left[pile]--;
        _card = (pile, _left[pile]);
        _next++;
        (_x, _y) = (PileX(pile), Top);
        var dir = Rnd.Next(2) == 0 ? -1 : 1;
        _vx = dir * R(120, 330);
        _vy = -R(0, 420);
        _flying = true;
        using var g = _table.CreateDrawingContext(false);
        if (_left[pile] > 0) g.DrawImage(Face(pile, _left[pile] - 1), new Rect(PileX(pile), Top, CardW, CardH));
        else
        {
            g.FillRectangle(Felt, new Rect(PileX(pile) - 1, Top - 1, CardW + 2, CardH + 2));
            g.DrawRectangle(null, Slot, new Rect(PileX(pile), Top, CardW, CardH), 4, 4);
        }
    }

    public override void Draw(DrawingContext g) => g.DrawImage(_table, new Rect(0, 0, DosScreen.Width, DosScreen.Height));

    private static RenderTargetBitmap Face(int suit, int rank)
    {
        if (Faces.TryGetValue((suit, rank), out var bmp)) return bmp;
        bmp = new RenderTargetBitmap(new PixelSize(CardW, CardH));
        var red = suit is 1 or 3;
        IBrush ink = red ? new SolidColorBrush(Color.Parse("#d00000")) : Brushes.Black;
        using (var g = bmp.CreateDrawingContext())
        {
            g.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(0.5, 0.5, CardW - 1, CardH - 1), 4, 4);
            var r = Ranks[rank];
            var corner = new FormattedText(r, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Avamux.Views.Ui.Mono, FontStyle.Normal, FontWeight.Bold), 12, ink);
            g.DrawText(corner, new Point(4, 2));
            var pip = new FormattedText(Suits[suit].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);
            g.DrawText(pip, new Point(4, 15));
            using (g.PushTransform(Matrix.CreateRotation(Math.PI) * Matrix.CreateTranslation(CardW, CardH)))
            {
                g.DrawText(corner, new Point(4, 2));
                g.DrawText(pip, new Point(4, 15));
            }
            if (rank >= 10)
            {
                // Court cards: a framed panel with a big letter.
                g.DrawRectangle(red ? new SolidColorBrush(Color.Parse("#fde7a0")) : new SolidColorBrush(Color.Parse("#cfe0ff")), new Pen(ink, 1), new Rect(13, 12, CardW - 26, CardH - 24));
                var big = new FormattedText(r, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 26, ink);
                g.DrawText(big, new Point((CardW - big.Width) / 2, (CardH - big.Height) / 2));
            }
            else
            {
                var centre = new FormattedText(Suits[suit].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, rank == 0 ? 34 : 24, ink);
                g.DrawText(centre, new Point((CardW - centre.Width) / 2, (CardH - centre.Height) / 2));
            }
        }
        return Faces[(suit, rank)] = bmp;
    }

    private static RenderTargetBitmap Back()
    {
        if (_back is not null) return _back;
        _back = new RenderTargetBitmap(new PixelSize(CardW, CardH));
        using var g = _back.CreateDrawingContext();
        g.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(0.5, 0.5, CardW - 1, CardH - 1), 4, 4);
        g.FillRectangle(new SolidColorBrush(Color.Parse("#1f3fbf")), new Rect(4, 4, CardW - 8, CardH - 8));
        var line = new Pen(new SolidColorBrush(Color.Parse("#7f9fff")), 1);
        using (g.PushClip(new Rect(4, 4, CardW - 8, CardH - 8)))
            for (var i = -CardH; i < CardW; i += 6)
            {
                g.DrawLine(line, new Point(i, 0), new Point(i + CardH, CardH));
                g.DrawLine(line, new Point(i + CardH, 0), new Point(i, CardH));
            }
        return _back;
    }
}
