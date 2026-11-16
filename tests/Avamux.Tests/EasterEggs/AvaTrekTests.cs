#if EASTER_EGGS
using System.Globalization;
using System.Text;
using Avamux.Core.EasterEggs;

namespace Avamux.Tests.EasterEggs;

public class AvaTrekTests
{
    private sealed class Screen : IDosConsole
    {
        public readonly StringBuilder Text = new();
        public void Write(string text) => Text.Append(text);
        public void Color(int fg, int bg) { }
        public void Clear() => Text.Clear();
        public void Beep() { }
        public string Take() { var s = Text.ToString(); Text.Clear(); return s; }
    }

    private static string N(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A plain captain: flies to the nearest Barkons, fluffs the shields, coughs hairballs, then lasers.</summary>
    private static AvaTrek Play(int seed, Screen screen, out int orders)
    {
        var game = new AvaTrek(screen, seed);
        game.Start();
        game.Enter("");
        var rnd = new Random(seed);
        for (orders = 0; orders < 3000 && !game.Finished; orders++)
        {
            if (game.Prompt != "Command? ") { game.Enter("n"); continue; }
            // Energy stuck in the shields: take some (in a fight) or all of it back out.
            if (game.Energy < 50 && game.Shields >= 1 && !game.Broken(6))
            {
                game.Enter(game.BarkonsHere > 0 && game.Shields > 40 ? "SHI " + (int)(game.Shields / 2) : "SHI 0");
                continue;
            }
            if (game.BarkonsHere > 0)
            {
                var shields = (int)Math.Min(1200, (game.Energy + game.Shields) / 2);
                if (game.Shields < 600 && shields > game.Shields + 50 && !game.Broken(6)) game.Enter("SHI " + shields);
                else if (game.Hairballs > 0 && !game.Broken(4))
                {
                    var (x, y) = game.BarkonSectors.First();
                    game.Enter("HAI " + N(game.CourseTo(x, y)));
                }
                else if (!game.Broken(3) && game.Energy > 50) game.Enter("LAS " + Math.Min(game.Energy - 20, 400).ToString(CultureInfo.InvariantCulture));
                else game.Enter($"NAV {rnd.Next(1, 9)} {(game.Broken(0) || game.Energy < 18 ? "0.125" : "1")}");
                continue;
            }
            // A broken drive: nap until it is mended.
            if (game.Broken(0)) { game.Enter("NAP 1"); continue; }
            // The nearest quadrant with Barkons in it.
            var (qx, qy) = game.Quadrant;
            var (sx, sy) = game.Sector;
            var target = Enumerable.Range(0, 64).Select(i => (X: i % 8, Y: i / 8)).Where(q => game.BarkonsIn(q.X, q.Y) > 0)
                .OrderBy(q => Math.Max(Math.Abs(q.X - qx), Math.Abs(q.Y - qy))).First();
            double dx = target.X * 8 + 3.5 - (qx * 8 + sx), dy = (qy * 8 + sy) - (target.Y * 8 + 3.5);
            var course = 1 + (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360 / 45;
            var warp = Math.Min(8, Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))) / 8);
            var before = (game.Quadrant, game.Sector);
            game.Enter($"NAV {N(course)} {N(warp)}");
            // Stopped by a star: step aside.
            if ((game.Quadrant, game.Sector) == before && !game.Finished) game.Enter($"NAV {rnd.Next(1, 9)} 0.25");
        }
        return game;
    }

    [Fact]
    public void Games_play_to_an_end_and_can_be_won()
    {
        var wins = 0;
        var wide = new HashSet<string>();
        for (var seed = 1; seed <= 30; seed++)
        {
            var screen = new Screen();
            var game = Play(seed, screen, out var orders);
            wide.UnionWith(screen.Text.ToString().Split('\n').Where(line => line.Length > 80));
            Assert.True(game.Finished, $"seed {seed} still going after {orders} orders:\n" + screen.Text.ToString()[Math.Max(0, screen.Text.Length - 3000)..]);
            if (game.Won) { wins++; Assert.Equal(0, game.BarkonsLeft); Assert.Contains("efficiency rating", screen.Text.ToString()); }
        }
        // Everything fits the 80-column screen.
        Assert.Empty(wide);
        Assert.True(wins >= 5, $"only {wins} of 30 won");
    }

    [Fact]
    public void Orders_are_understood_and_nonsense_is_not_fatal()
    {
        var screen = new Screen();
        var game = new AvaTrek(screen, 7);
        game.Start();
        game.Enter("");
        Assert.Contains("Barkons remaining", screen.Take());
        game.Enter("lrs");
        Assert.Contains("Long-range scan", screen.Take());
        game.Enter("com 0");
        Assert.Contains("Computer record of the galaxy", screen.Take());
        game.Enter("help");
        var help = screen.Take();
        Assert.Contains("NAV", help);
        Assert.All(help.Split('\n'), line => Assert.True(line.Length <= 80, line));
        game.Enter("nav");
        Assert.Equal("Course (1-9)? ", game.Prompt);
        game.Enter("12");
        Assert.Contains("Incorrect course", screen.Take());
        game.Enter("shi");
        game.Enter("lots");
        Assert.Contains("not a number", screen.Take());
        game.Enter("shi 99999");
        Assert.Contains("not made of catnip", screen.Take());
        game.Enter("shi 200");
        Assert.Equal(200, game.Shields);
        game.Enter("dam");
        Assert.Contains("State of repair", screen.Take());
        game.Enter("fish");
        Assert.Contains("Orders are", screen.Take());
        foreach (var junk in new[] { "nav 1 99", "nav 3 0", "hai 0", "las -5", "nap -1", "com 9", "nav 1 0.001" }) game.Enter(junk);
        Assert.False(game.Finished);
        game.Enter("quit");
        Assert.True(game.Finished);
        Assert.False(game.Won);
    }
}
#endif
