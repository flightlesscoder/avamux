using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.TestSupport;
using Avamux.Views;

namespace Avamux.UiTests;

/// <summary>
/// UI load tests: the real wiki editor and page with very large pages, very long lines and thousands of pictures
/// and files. Opt in with AVAMUX_LOAD_TESTS=1 (see <see cref="Load"/> and scripts/load-tests.sh). Budgets are
/// what feels responsive: a keystroke well under 100 ms, opening a page in a second or two.
/// </summary>
[Trait("Category", "Load")]
public class LoadTests(ITestOutputHelper output)
{
    private void Check(string what, Load.Sample s, double? budgetMs, string? note = null, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        var (ok, line) = Load.Report(test, what, s, budgetMs, note);
        output.WriteLine(line);
        Assert.True(ok, $"{what}: {s.Ms:0} ms, over the budget of {budgetMs} ms");
    }

    private static readonly string[] Three = ["wide.png", "small.png", "tall.png"];

    /// <summary>Times <paramref name="keys"/> keystrokes typed at the caret, each followed by layout; returns the slowest.</summary>
    private static (Load.Sample Total, double SlowestMs) TypeKeys(EditorHarness h, int keys)
    {
        var slowest = 0.0;
        var total = Load.Measure(() =>
        {
            for (var i = 0; i < keys; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                h.Type("x");
                slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);
            }
        });
        return (total with { Ms = total.Ms / keys }, slowest);
    }

    /// <summary>Runs the UI for a while and says how many times pictures made the editor redraw meanwhile.</summary>
    private static int RedrawsWhileIdle(EditorHarness h, int ms = 1000)
    {
        var before = h.Pictures.LoadRedraws;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms) { Dispatcher.UIThread.RunJobs(); h.Window.UpdateLayout(); Thread.Sleep(10); }
        return h.Pictures.LoadRedraws - before;
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void A_chat_of_thousands_of_messages()
    {
        var count = Load.N(2000);
        var body = Load.Markdown(1200);
        ChatHarness? h = null;
        var s = Load.Measure(() => h = new ChatHarness(x =>
        {
            var history = new List<(string, string)>();
            for (var i = 0; i < count / 2; i++) { history.Add(("user", $"Question {i}: what about the lanterns?")); history.Add(("assistant", body)); }
            x.Services.Ai.Chats.Create(new Avamux.Plugins.AiChatRequest { Title = "Long chat", ProviderId = "claude-code", History = history });
        }));
        using (h)
        {
            Check($"open a chat of {count} messages", s, budgetMs: 2500 * Load.Scale, note: "only the messages in view are built");
            var scroll = h!.Page.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().First(v => v.Content is Avalonia.Controls.ItemsControl);
            s = Load.Measure(() => { for (var i = 0; i < 40; i++) { scroll.Offset = new Avalonia.Vector(0, scroll.Offset.Y - 600); h.Settle(); } });
            Check("scroll up 40 screens", s with { Ms = s.Ms / 40 }, budgetMs: 120, note: "per screen");

            // A reply streaming into the end of it: 400 pieces, as a fast model writes.
            var chats = h.Services.Ai.Chats;
            var chat = chats.List()[0];
            var reply = Avamux.Core.Ai.ChatService.Thread(chat).Last();
            reply.Text = "";
            reply.IsWriting = true;
            var row = h.Page.GetVisualDescendants().OfType<ChatMessageView>().Count();
            var slowest = 0.0;
            s = Load.Measure(() =>
            {
                for (var i = 0; i < 400; i++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    reply.Text += "word " + (i % 12 == 11 ? "and so it goes.\n\n" : "");
                    typeof(Avamux.Core.Ai.ChatService).GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(chats, [chat.Id]);
                    h.Settle();
                    slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);
                }
            });
            Check("400 pieces of a reply streamed in", s with { Ms = s.Ms / 400 }, budgetMs: 30, note: $"per piece; slowest {slowest:0} ms");
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void Opening_big_pages()
    {
        foreach (var (mb, pictures) in new[] { (1, false), (1, true), (5, false), (5, true) })
        {
            var text = Load.Markdown(Load.N(mb * 1024 * 1024), pictureEvery: pictures ? 25 : 0, pictures: Three);
            EditorHarness? h = null;
            var s = Load.Measure(() => h = new EditorHarness(text, waitForPictures: false));
            using (h)
            {
                Check($"show a {mb} MB page{(pictures ? $" with {h!.Pictures.Lines.Count} picture lines" : "")}", s, budgetMs: 1000.0 * mb * Load.Scale + 500, note: "first layout: the UI waits for this");
                if (pictures)
                {
                    s = Load.Measure(() => h!.Settle(60_000, search: true));
                    Check("until loaded and every line is measured", s, budgetMs: 3000.0 * mb * Load.Scale, note: "in slices between other work");
                }
            }
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void Typing_in_big_pages()
    {
        foreach (var (mb, pictures) in new[] { (1, false), (1, true), (5, false), (5, true) })
        {
            var text = Load.Markdown(Load.N(mb * 1024 * 1024), pictureEvery: pictures ? 25 : 0, pictures: Three);
            using var h = new EditorHarness(text, settleMs: 60_000);
            h.Editor.CaretOffset = text.Length / 2;
            h.Settle(60_000);
            var (each, slowest) = TypeKeys(h, 20);
            // Every keystroke reads the whole page again: highlighting, and finding pictures when there are any.
            Check($"keystroke in the middle of a {mb} MB page{(pictures ? " with pictures" : "")}", each, budgetMs: 100, note: $"slowest {slowest:0} ms");
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void Thousands_of_different_pictures()
    {
        var count = Load.N(3000);
        var names = Enumerable.Range(0, count).Select(i => $"p{i:00000}.png").ToList();
        var page = string.Concat(names.Select((n, i) => $"Photo {i}\n![photo {i}]({n})\n"));
        Load.Sample setup = null!;
        EditorHarness? h = null;
        var decodes = EditorPictures.Decodes;
        var s = Load.Measure(() => h = new EditorHarness(page, waitForPictures: false, prepare: e =>
        {
            setup = Load.Measure(() => { for (var i = 0; i < count; i++) e.WritePicture(names[i], 64 + i % 50, 48, Color.FromRgb((byte)i, 120, 200)); });
        }));
        using (h)
        {
            Check($"write {count} picture files (setup)", setup, budgetMs: null);
            Check($"show a page of {count} different pictures", s with { Ms = s.Ms - h!.PrepareMs }, budgetMs: 1000, note: "first layout: the UI waits for this");
            s = Load.Measure(() => h!.Settle(120_000));
            Check("until the pictures in view have loaded and every line is measured", s, budgetMs: 3000 * Load.Scale, note: $"{EditorPictures.Decodes - decodes} decodes, in the background");
            // Nothing may keep going once the page is quiet: no endless reloading of pictures that fell out of a cache.
            var redraws = RedrawsWhileIdle(h!);
            Assert.True(redraws == 0, $"the editor kept redrawing while idle ({redraws} times in a second)");
            s = Load.Measure(() => h!.Keys(PhysicalKey.End, RawInputModifiers.Control));
            Check("Ctrl+End", s, budgetMs: 500);
            s = Load.Measure(() => { for (var i = 0; i < 30; i++) h!.Key(Key.PageUp); h!.Settle(60_000); });
            Check("30 × Page Up, pictures loading as they come into view", s, budgetMs: 3000);
            var (each, slowest) = TypeKeys(h!, 10);
            Check("keystroke on that page", each, budgetMs: 100, note: $"slowest {slowest:0} ms");
            Assert.True(RedrawsWhileIdle(h!) == 0, "the editor kept redrawing while idle after scrolling");
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void The_same_picture_thousands_of_times()
    {
        var count = Load.N(5000);
        var page = string.Concat(Enumerable.Range(0, count).Select(i => $"Line {i}\n![w](wide.png)\n"));
        EditorHarness? h = null;
        var s = Load.Measure(() => h = new EditorHarness(page, waitForPictures: false));
        using (h)
        {
            Check($"show a page showing one picture {count} times", s, budgetMs: 1000, note: "first layout: the UI waits for this");
            s = Load.Measure(() => h!.Settle(60_000));
            Check("until loaded and every line is measured", s, budgetMs: 3000 * Load.Scale, note: "measuring runs in slices between other work");
            s = Load.Measure(() => h!.Keys(PhysicalKey.End, RawInputModifiers.Control));
            Check("Ctrl+End", s, budgetMs: 500);
            Assert.InRange(h!.CaretPoint().Y, 0, h.Editor.Bounds.Height);
            s = Load.Measure(() => { for (var i = 0; i < 50; i++) h.Key(Key.PageUp); });
            Check("50 × Page Up", s, budgetMs: 2500);
            var (each, slowest) = TypeKeys(h, 10);
            Check("keystroke on that page", each, budgetMs: 100, note: $"slowest {slowest:0} ms");
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void Big_pictures()
    {
        var count = Load.N(24);
        var page = string.Concat(Enumerable.Range(0, count).Select(i => $"![big {i}](big{i}.png)\n"));
        Load.Sample setup = null!;
        EditorHarness? h = null;
        var s = Load.Measure(() => h = new EditorHarness(page, waitForPictures: false, prepare: e =>
        {
            setup = Load.Measure(() => { for (var i = 0; i < count; i++) e.WritePicture($"big{i}.png", 4000, 3000, Color.FromRgb((byte)(i * 10), 80, 160)); });
        }));
        using (h)
        {
            Check($"write {count} pictures of 4000 × 3000 (setup)", setup, budgetMs: null);
            Check($"show a page of {count} pictures of 4000 × 3000", s with { Ms = s.Ms - h!.PrepareMs }, budgetMs: 1000, note: "first layout, places held at their sizes");
            s = Load.Measure(() => h!.Settle(120_000));
            Check("until the pictures in view have loaded", s, budgetMs: 5000 * Load.Scale, note: "decoded straight to a smaller size, in the background");
            s = Load.Measure(() => { h!.Keys(PhysicalKey.End, RawInputModifiers.Control); h.Settle(120_000); });
            Check("Ctrl+End", s, budgetMs: 1000);
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void A_thousand_pictures_on_one_line()
    {
        var count = Load.N(1000);
        var line = string.Join(' ', Enumerable.Range(0, count).Select(i => $"![s{i}](small.png)"));
        EditorHarness? h = null;
        var s = Load.Measure(() => h = new EditorHarness(line + "\nNext\n", settleMs: 60_000));
        using (h)
        {
            var note = line.Length > 3000 ? "line over 3000 characters: shown as plain text, no pictures" : null;
            Check($"open a line of {count} pictures ({line.Length} characters)", s, budgetMs: 2000, note: note);
            h!.Editor.CaretOffset = h.Line(1).EndOffset;
            var (each, slowest) = TypeKeys(h, 10);
            Check("keystroke at the end of that line", each, budgetMs: 100, note: $"slowest {slowest:0} ms");
        }
        // The longest line that still shows its pictures.
        var fits = string.Join(' ', Enumerable.Range(0, 150).Select(i => $"![{i}](small.png)"));
        fits = fits[..Math.Min(fits.Length, 2990)];
        fits = fits[..(fits.LastIndexOf(')') + 1)];
        s = Load.Measure(() => h = new EditorHarness(fits + "\n", settleMs: 60_000));
        using (h)
        {
            Check($"open a line of {h!.Images().Count} pictures ({fits.Length} characters)", s, budgetMs: 2000);
            h.Editor.CaretOffset = h.Line(1).EndOffset;
            var (each, slowest) = TypeKeys(h, 10);
            Check("keystroke at the end of that line", each, budgetMs: 100, note: $"slowest {slowest:0} ms");
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void Very_long_lines_in_the_editor()
    {
        foreach (var chars in new[] { 100_000, 1_000_000 })
        {
            var words = string.Join(' ', Enumerable.Repeat("lorem", Load.N(chars / 6)));
            EditorHarness? h = null;
            var s = Load.Measure(() => h = new EditorHarness(words + " ![w](wide.png)\nNext ![s](small.png)\n", settleMs: 60_000));
            using (h)
            {
                Check($"open a page with one {words.Length / 1000} K character line", s, budgetMs: 3000);
                h!.Editor.CaretOffset = h.Line(1).EndOffset;
                h.Settle();
                var (each, slowest) = TypeKeys(h, 5);
                Check($"keystroke at the end of the {words.Length / 1000} K character line", each, budgetMs: 150, note: $"slowest {slowest:0} ms");
            }
        }
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void Pasting_and_undoing_a_big_block_with_pictures()
    {
        using var h = new EditorHarness("Start\n", settleMs: 60_000);
        var block = Load.Markdown(Load.N(1024 * 1024), pictureEvery: 10, pictures: Three);
        h.Window.Clipboard!.SetTextAsync(block).GetAwaiter().GetResult();
        h.Editor.CaretOffset = h.Text.Length;
        var s = Load.Measure(() => { h.Keys(PhysicalKey.V, RawInputModifiers.Control); h.Settle(60_000, search: true); });
        Check($"paste 1 MB of markdown with {h.Pictures.Lines.Count} picture lines", s, budgetMs: 2000);
        s = Load.Measure(() => { h.Keys(PhysicalKey.Z, RawInputModifiers.Control); h.Settle(60_000, search: true); });
        Check("undo it", s, budgetMs: 2000);
        Assert.Equal("Start\n", h.Text);
        s = Load.Measure(() => { h.Keys(PhysicalKey.Y, RawInputModifiers.Control); h.Settle(60_000, search: true); });
        Check("redo it", s, budgetMs: 2000);
    }

    [AvaloniaFact(Skip = Load.Why, SkipUnless = nameof(Load.On), SkipType = typeof(Load))]
    public void A_wiki_of_thousands_of_files()
    {
        var count = Load.N(20_000);
        WikiHarness? h = null;
        Load.Sample setup = null!;
        var s = Load.Measure(() => h = new WikiHarness(w =>
        {
            setup = Load.Measure(() =>
            {
                var png = new byte[16];
                Directory.CreateDirectory(w.Full("Attachments"));
                Directory.CreateDirectory(w.Full("Notes"));
                for (var i = 0; i < count; i++) File.WriteAllBytes(w.Full($"Attachments/file {i:00000}.png"), png);
                for (var i = 0; i < 200; i++) File.WriteAllText(w.Full($"Notes/Note {i}.md"), "# Note\n");
                File.WriteAllText(w.Full("Index.md"), "# Index\n");
            });
        }, open: "Index.md"));
        using (h)
        {
            Check($"create {count} files (setup)", setup, budgetMs: null);
            Check($"open the wiki page over {count} files", s, budgetMs: 3000 * Load.Scale, note: "the folder tree lists every file");
            // Every change to the wiki (each autosave too) rebuilds the folder tree.
            s = Load.Measure(() => { h!.Wiki.NotifyChanged(); h.Settle(minMs: 200); });
            Check("rebuild after a change to the wiki", s, budgetMs: 1000 * Load.Scale, note: "includes a 150 ms pause before the rebuild");
            s = Load.Measure(() => { h!.Page.Open("Notes/Note 150.md"); h.Settle(); });
            Check("open another page", s, budgetMs: 500 * Load.Scale);
        }
    }
}
