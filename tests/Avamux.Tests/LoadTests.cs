using System.IO.Compression;
using System.Text;
using Avamux.Core;
using Avamux.Core.Text;
using Avamux.Core.Wiki;
using Avamux.TestSupport;
using Xunit.Abstractions;

namespace Avamux.Tests;

/// <summary>A test that runs only when load tests are opted in (see <see cref="Load"/>).</summary>
public sealed class LoadFactAttribute : FactAttribute
{
    public LoadFactAttribute() { if (!Load.On) Skip = Load.Why; }
}

/// <summary>
/// Core load tests: how the wiki copes with very large pages, very long lines, deep nesting and folders of
/// thousands of files, without the UI. Budgets are what the app can afford where it does the work: rendering and
/// highlighting run again after every edit (after a short pause), so they must stay well under a second.
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

    // ---- big pages ----

    [LoadFact]
    public void Rendering_big_pages()
    {
        foreach (var mb in new[] { 1, 5, 20 })
        {
            var text = Load.Markdown(Load.N(mb * 1024 * 1024));
            string html = "";
            var s = Load.Measure(() => html = WikiService.Render(text));
            Assert.Contains("<h2", html);
            // The preview renders the page again after every pause in typing: a few hundred ms per MB is the limit.
            Check($"render {mb} MB page", s, budgetMs: 400.0 * mb * Load.Scale + 200);
        }
    }

    [LoadFact]
    public void Highlighting_big_pages_as_the_editor_does_after_every_change()
    {
        // MarkdownColorizer tokenizes the whole page again after every change (fenced blocks need state across lines).
        foreach (var mb in new[] { 1, 5 })
        {
            var text = Load.Markdown(Load.N(mb * 1024 * 1024));
            var s = Load.Measure(() => Syntax.Highlight(text, "markdown"));
            Check($"highlight {mb} MB page", s, budgetMs: 150.0 * mb * Load.Scale, note: "runs on every keystroke");
        }
    }

    [LoadFact]
    public void Finding_pictures_in_big_pages_as_the_editor_does_after_every_change()
    {
        var plain = Load.Markdown(Load.N(5 * 1024 * 1024));
        var s = Load.Measure(() => Assert.Empty(WikiPictures.Find(plain)));
        Check("5 MB page, no pictures (quick path)", s, budgetMs: 50 * Load.Scale + 20);

        var one = plain + "\n![last](last.png)\n";
        s = Load.Measure(() => Assert.Single(WikiPictures.Find(one)));
        Check("5 MB page, one picture (full parse)", s, budgetMs: 1500 * Load.Scale, note: "runs on every keystroke");

        var many = Load.Markdown(Load.N(2 * 1024 * 1024), pictureEvery: 3, pictures: ["a.png", "b.png", "c.png"]);
        IReadOnlyList<WikiPicture> found = [];
        s = Load.Measure(() => found = WikiPictures.Find(many));
        Check($"2 MB page, {found.Count} pictures", s, budgetMs: 1000 * Load.Scale);
        Assert.True(found.Count > 1000);
    }

    // ---- long lines ----

    [LoadFact]
    public void Very_long_lines()
    {
        var words = string.Join(' ', Enumerable.Repeat("lorem", Load.N(400_000))); // ~2.4 M characters on one line
        var s = Load.Measure(() => WikiService.Render(words));
        Check($"render one line of {words.Length / 1_000_000.0:0.0} M characters", s, budgetMs: 2000 * Load.Scale);

        var solid = new string('x', Load.N(5_000_000));
        s = Load.Measure(() => WikiService.Render(solid));
        Check("render one 5 M character word", s, budgetMs: 2000 * Load.Scale);

        s = Load.Measure(() => Syntax.Highlight(words + "\n" + solid, "markdown"));
        Check("highlight both long lines", s, budgetMs: 1000 * Load.Scale);

        var picture = words + " ![end](end.png)";
        s = Load.Measure(() => Assert.Single(WikiPictures.Find(picture)));
        Check("find a picture at the end of a 2.4 M character line", s, budgetMs: 1000 * Load.Scale);

        var url = "![long](" + new string('a', Load.N(1_000_000)) + ".png)";
        s = Load.Measure(() => Assert.Single(WikiPictures.Find(url)));
        Check("picture with a 1 M character address", s, budgetMs: 1000 * Load.Scale);
    }

    [LoadFact]
    public void Many_wiki_links_and_a_huge_front_matter()
    {
        var links = string.Join(' ', Enumerable.Range(0, Load.N(50_000)).Select(i => $"[[Page {i}|label {i}]]"));
        var s = Load.Measure(() => WikiService.Render(links));
        Check($"render {Load.N(50_000)} wiki links", s, budgetMs: 2000 * Load.Scale);

        var front = "---\n" + string.Concat(Enumerable.Range(0, Load.N(20_000)).Select(i => $"key{i}: value {i}\n")) + "---\n# Body\n";
        s = Load.Measure(() => Assert.Contains("frontmatter", WikiService.Render(front)));
        Check($"render front matter of {Load.N(20_000)} keys", s, budgetMs: 1500 * Load.Scale);
    }

    [LoadFact]
    public void Deep_nesting_is_refused_or_rendered_but_never_crashes()
    {
        // Markdig stops at a depth limit with an ArgumentException; the preview shows the message instead of the page.
        var cases = new (string Name, string Text)[]
        {
            ("quotes x 5000", new string('>', 5000) + " deep\n"),
            ("emphasis x 5000", new string('*', 5000) + "x" + new string('*', 5000)),
            ("brackets x 20000", new string('[', 20000) + "x" + new string(']', 20000)),
            ("list items x 300", string.Concat(Enumerable.Range(0, 300).Select(i => new string(' ', i * 2) + "- item\n"))),
            ("link parentheses x 50000", string.Concat(Enumerable.Repeat("[a](", 50000)) + "x" + new string(')', 50000)),
        };
        foreach (var (name, text) in cases)
        {
            string result = "";
            var s = Load.Measure(() =>
            {
                try { WikiService.Render(text); result = "rendered"; }
                catch (ArgumentException) { result = "refused (depth limit)"; }
                Assert.NotNull(WikiPictures.Find(text + "\n![p](p.png)"));
            });
            Check(name, s, budgetMs: 3000, note: result);
        }
    }

    // ---- thousands of files ----

    [LoadFact]
    public void A_folder_of_thousands_of_attachments()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        var count = Load.N(20_000);
        var folder = Path.Combine(wiki.Root, "Attachments");
        Directory.CreateDirectory(folder);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var s = Load.Measure(() => { for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(folder, $"photo {i:00000}.png"), png); });
        Check($"write {count} files (setup)", s, budgetMs: null);

        WikiNode tree = null!;
        s = Load.Measure(() => tree = wiki.Tree());
        Check($"tree of {count} files", s, budgetMs: 1500 * Load.Scale, note: "rebuilt on every wiki change");
        Assert.Equal(count, tree.Children.Single(c => c.Name == "Attachments").Children.Count);

        s = Load.Measure(() => Assert.Equal(count + 1, wiki.Pages().Count())); // and Home.md
        Check($"list {count} files", s, budgetMs: 1500 * Load.Scale);

        s = Load.Measure(() => { for (var i = 0; i < 20; i++) Assert.Null(wiki.FindPage("No such page " + i)); });
        Check($"20 lookups of a missing [[page]] among {count} files", s, budgetMs: 2000 * Load.Scale, note: "each one lists every file");

        // A page that embeds every one of them.
        var page = string.Join("\n", Enumerable.Range(0, count).Select(i => $"![photo {i}](<Attachments/photo {i:00000}.png>)"));
        IReadOnlyList<WikiPicture> found = [];
        s = Load.Measure(() => found = WikiPictures.Find(page));
        Check($"find {count} pictures on one page", s, budgetMs: 1500 * Load.Scale);
        Assert.Equal(count, found.Count);
        s = Load.Measure(() => WikiService.Render(page));
        Check($"render a page of {count} pictures", s, budgetMs: 2000 * Load.Scale);
    }

    [LoadFact]
    public async Task Tagging_a_wiki_of_thousands_of_pages()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        var pages = Load.N(10_000);
        var body = Load.Markdown(4_000);
        // Every other page carries #common (a tag on thousands of pages); each also has its own tag and a nested one.
        var s = Load.Measure(() =>
        {
            for (var i = 0; i < pages; i++)
            {
                var folder = Path.Combine(wiki.Root, $"Folder {i % 50}");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"Page {i}.md"), body + $"\n\n{(i % 2 == 0 ? "#common " : "")}#own{i} #area/sub{i % 20}\n");
            }
        });
        Check($"write {pages} tagged pages (setup)", s, budgetMs: null);

        var index = new WikiTagIndex(wiki, box.Paths);
        s = Load.Measure(() => index.RefreshAsync().GetAwaiter().GetResult());
        Check($"index tags of {pages} pages from scratch", s, budgetMs: 8000 * Load.Scale, note: "in the background at first start");
        index.Dispose(); // writes the cache

        using var again = new WikiTagIndex(wiki, box.Paths);
        var load = typeof(WikiTagIndex).GetMethod("LoadCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        s = Load.Measure(() => { load.Invoke(again, null); again.RefreshAsync().GetAwaiter().GetResult(); });
        Check($"start again from the cache over {pages} pages", s, budgetMs: 1500 * Load.Scale, note: "every later start");
        Assert.Equal(pages + 1, again.PageCount); // and Home.md

        List<TagHit> hits = [];
        s = Load.Measure(() => hits = again.Pages("common"));
        Check($"pages of a tag on {pages / 2} pages", s, budgetMs: 150 * Load.Scale, note: "opening a tag in the Tags view");
        Assert.Equal((pages + 1) / 2, hits.Count);
        s = Load.Measure(() => hits = again.Pages("area"));
        Check($"pages under a parent of 20 nested tags ({pages} pages)", s, budgetMs: 250 * Load.Scale);
        Assert.Equal(pages, hits.Count);
        List<TagCount> tags = [];
        s = Load.Measure(() => tags = again.Tags());
        Check($"list {pages + 21} tags", s, budgetMs: 150 * Load.Scale);
        s = Load.Measure(() => { for (var i = 0; i < 100; i++) wiki.Write($"Folder 0/Page {i * 50}.md", body + $"\n#edited{i}\n"); });
        s = Load.Measure(() => { for (var i = 0; i < 100; i++) again.UpdateAsync($"Folder 0/Page {i * 50}.md").GetAwaiter().GetResult(); });
        Check("re-read 100 edited pages", s, budgetMs: 300 * Load.Scale, note: "after each save");
        Assert.Single(again.Pages("edited7"));
    }

    [LoadFact]
    public void Importing_the_same_file_name_again_and_again()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        var count = Load.N(2_000);
        var data = new byte[64];
        var names = new HashSet<string>();
        var s = Load.Measure(() => { for (var i = 0; i < count; i++) names.Add(wiki.ImportBytes(data, "", "Pasted image.png")); });
        Assert.Equal(count, names.Count);
        // Each import tries "name-1", "name-2", … until one is free, so the n-th costs n checks.
        Check($"import \"Pasted image.png\" {count} times", s, budgetMs: 1500 * Load.Scale);
    }

    [LoadFact]
    public void Searching_a_wiki_of_thousands_of_pages()
    {
        using var box = new Sandbox();
        var app = new AppServices(box.Paths);
        var pages = Load.N(3_000);
        for (var i = 0; i < pages; i++)
            File.WriteAllText(Path.Combine(app.Wiki.Root, $"Page {i}.md"), Load.Markdown(20_000) + (i % 500 == 0 ? "\nneedle-word\n" : ""));
        IReadOnlyList<Avamux.Core.Search.SearchHit> hits = [];
        var s = Load.Measure(() => hits = app.Search.SearchAsync("needle-word", ["wiki"]).GetAwaiter().GetResult());
        Check($"search {pages} pages of 20 KB", s, budgetMs: 3000 * Load.Scale);
        Assert.NotEmpty(hits);
    }

    // ---- big files of other kinds ----

    [LoadFact]
    public void Previews_of_big_files()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        // Text files (CSV included) up to WikiService.MaxEditableBytes (2 MB) are previewed; bigger ones say so.
        var rows = 60_000;
        var csv = new StringBuilder("id,name,note\n");
        for (var i = 0; i < rows; i++) csv.Append(i).Append(",name ").Append(i).Append(",\"a, quoted note\"\n");
        Assert.True(csv.Length < WikiService.MaxEditableBytes);
        File.WriteAllText(Path.Combine(wiki.Root, "big.csv"), csv.ToString());
        string html = "";
        var s = Load.Measure(() => html = wiki.RenderFile("big.csv"));
        Check($"preview a CSV of {rows} rows ({csv.Length / 1048576.0:0.0} MB)", s, budgetMs: 3000, note: $"{html.Length / 1048576.0:0.0} MB of HTML for the preview to lay out");
        // Tables stop after WikiService.MaxTableCells cells, with a note that the rest is not shown.
        Assert.Contains("<td>name 0</td>", html);

        File.WriteAllText(Path.Combine(wiki.Root, "huge.csv"), csv.ToString() + csv);
        s = Load.Measure(() => html = wiki.RenderFile("huge.csv"));
        Check("preview a CSV over 2 MB", s, budgetMs: 100, note: "limit: not previewed (\"too large to preview\")");
        Assert.Contains("too large", html);

        var entries = Load.N(50_000);
        using (var zip = ZipFile.Open(Path.Combine(wiki.Root, "big.zip"), ZipArchiveMode.Create))
            for (var i = 0; i < entries; i++) zip.CreateEntry($"folder {i % 50}/file {i}.txt");
        s = Load.Measure(() => html = wiki.RenderFile("big.zip"));
        Check($"preview a zip of {entries} entries", s, budgetMs: 3000 * Load.Scale, note: $"{html.Length / 1048576.0:0.0} MB of HTML");

        var log = string.Concat(Enumerable.Repeat("2026-11-23 12:00:00 [INFO] something happened here\n", 38_000));
        Assert.True(log.Length < WikiService.MaxEditableBytes);
        File.WriteAllText(Path.Combine(wiki.Root, "big.log"), log);
        s = Load.Measure(() => html = wiki.RenderFile("big.log"));
        Check($"preview a {log.Length / 1048576.0:0.0} MB log (highlighted)", s, budgetMs: 3000, note: $"{html.Length / 1048576.0:0.0} MB of HTML for the preview to lay out");
    }
}
