using Avamux.Core;
using Avamux.Core.Config;
using Avamux.Core.Events;
using Avamux.Core.Repos;
using Avamux.Core.Terminal;
using Avamux.Core.Text;
using Avamux.Core.Theming;
using Avamux.Core.Watching;
using Avamux.Core.Wiki;

namespace Avamux.Tests;

public class ThemeTests
{
    [Fact]
    public void Every_theme_resolves_every_token_to_a_colour()
    {
        foreach (var theme in Themes.All)
            foreach (var token in Themes.Simple.Colors.Keys)
                Assert.True(Rgb.TryParse(theme[token], out _), $"{theme.Id}.{token}");
        Assert.All(Themes.All, t => Assert.Equal(16, t.Ansi.Length));
    }

    [Fact]
    public void Accent_override_rebuilds_the_accent_family_and_bad_input_is_ignored()
    {
        var pink = Themes.Simple.WithAccent("#ff2fd4");
        Assert.Equal("#ff2fd4", pink["accent"]);
        Assert.NotEqual(Themes.Simple["link"], pink["link"]);
        Assert.Equal(Themes.Simple["bg"], pink["bg"]);
        Assert.Same(Themes.Simple, Themes.Simple.WithAccent("not a colour"));
        Assert.Same(Themes.Simple, Themes.Simple.WithAccent(""));
    }

    [Fact]
    public void Settings_pick_theme_and_chrome()
    {
        Assert.Equal("classic90s", Themes.Find("90's Desktop Computer OS Classic").Id);
        Assert.Equal("simple", Themes.Find("no such theme").Id);
        var retro = new ThemeSettings { Name = "classic90s" };
        Assert.True(Themes.CustomChrome(retro, Themes.Resolve(retro)));
        retro.WindowChrome = "system";
        Assert.False(Themes.CustomChrome(retro, Themes.Resolve(retro)));
        Assert.False(Themes.CustomChrome(new ThemeSettings(), Themes.Simple));
        Assert.True(Themes.CustomChrome(new ThemeSettings { WindowChrome = "custom" }, Themes.Simple));
    }

    [Fact]
    public void Every_sound_renders_in_every_set_and_is_written_as_a_wav()
    {
        foreach (var set in SoundService.Sets)
            foreach (var e in Enum.GetValues<SoundEvent>())
            {
                var samples = SoundService.Synthesize(set, e);
                Assert.True(samples.Length > 4000, $"{set}/{e} is too short");
                Assert.True(samples.Max(Math.Abs) is > 0.2f and <= 1f, $"{set}/{e} level");
            }
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var sounds = new SoundService(box.Paths, config);
        var file = sounds.Resolve(SoundEvent.Timer);
        Assert.Equal("RIFF"u8.ToArray(), File.ReadAllBytes(file)[..4]);
        // A file of the user's own wins.
        Directory.CreateDirectory(sounds.CustomFolder);
        File.WriteAllText(Path.Combine(sounds.CustomFolder, "timer.ogg"), "x");
        Assert.EndsWith("timer.ogg", sounds.Resolve(SoundEvent.Timer));
    }
}

// Tests that start processes on a pty share a collection so they never fork at the same moment.
[Collection("pty")]
public class TerminalStateTests
{
    private static readonly string[] Agents = ["claude", "codex", "aider", "pi"];

    [Fact]
    public void Classify_tells_idle_running_working_and_waiting_apart()
    {
        var idle = TimeSpan.FromSeconds(3);
        Assert.Equal(TerminalState.Idle, TerminalSession.Classify(true, false, false, TimeSpan.Zero, idle));
        Assert.Equal(TerminalState.Running, TerminalSession.Classify(false, false, false, TimeSpan.FromMinutes(5), idle));
        Assert.Equal(TerminalState.AgentWorking, TerminalSession.Classify(false, true, false, TimeSpan.FromSeconds(1), idle));
        Assert.Equal(TerminalState.AgentWaiting, TerminalSession.Classify(false, true, false, TimeSpan.FromSeconds(4), idle));
        Assert.Equal(TerminalState.AgentWaiting, TerminalSession.Classify(false, true, true, TimeSpan.Zero, idle)); // the bell
    }

    [Fact]
    public void Agents_are_recognised_by_name_script_or_package()
    {
        Assert.Equal("claude", TerminalSession.MatchAgent("claude", null, Agents));
        Assert.Equal("aider", TerminalSession.MatchAgent("python3", ["python3", "/home/u/.local/bin/aider", "--model", "x"], Agents));
        Assert.Equal("codex", TerminalSession.MatchAgent("node", ["node", "/usr/lib/node_modules/@openai/codex/bin/codex.js"], Agents));
        Assert.Equal("claude", TerminalSession.MatchAgent("node", ["node", "/x/node_modules/@anthropic-ai/claude-code/cli.js"], Agents));
        Assert.Null(TerminalSession.MatchAgent("vim", ["vim", "claude-notes.md"], Agents));
        Assert.Null(TerminalSession.MatchAgent("node", ["node", "server.js", "--pi"], Agents));
        Assert.Null(TerminalSession.MatchAgent("claude", null, []));
    }

    [Fact]
    public async Task A_shell_is_idle_and_a_program_in_it_is_running()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var manager = new TerminalManager(box.Paths, config);
        var s = manager.Create(new TerminalStartOptions("/bin/sh"));
        await Task.Delay(300);
        s.Refresh(Agents, TimeSpan.FromSeconds(3));
        Assert.Equal(TerminalState.Idle, s.State);
        s.Write("sleep 5\n");
        for (var i = 0; i < 30 && s.State != TerminalState.Running; i++) { await Task.Delay(100); s.Refresh(Agents, TimeSpan.FromSeconds(3)); }
        Assert.Equal(TerminalState.Running, s.State);
        Assert.Equal("Running", s.Info().State);
        manager.Close(s.Id);
    }
}

public class SyntaxTests
{
    [Fact]
    public void Languages_are_found_from_fence_labels_and_file_names()
    {
        Assert.Equal("csharp", Syntax.Normalize("cs"));
        Assert.Equal("javascript", Syntax.Normalize("js {1,3}"));
        Assert.Equal("bash", Syntax.Normalize("Shell"));
        Assert.Null(Syntax.Normalize("klingon"));
        Assert.Null(Syntax.Normalize(""));
        Assert.Equal("python", Syntax.LanguageOf("tools/run.py"));
        Assert.Equal("dockerfile", Syntax.LanguageOf("Dockerfile"));
        Assert.Equal("xml", Syntax.LanguageOf("App.axaml"));
    }

    [Fact]
    public void Tokens_cover_keywords_strings_comments_and_numbers_and_lose_no_text()
    {
        const string code = "// hi\npublic static int Add(int a) { return a + 42; } /* multi\nline */ var s = \"x // not a comment\";";
        var lines = Syntax.Highlight(code, "csharp");
        Assert.Equal(code, string.Join("\n", lines.Select(l => string.Concat(l.Select(s => s.Text)))));
        Assert.Equal(Tok.Comment, lines[0][0].Kind);
        Assert.Contains(lines[1], s => s is { Kind: Tok.Keyword, Text: "public" });
        Assert.Contains(lines[1], s => s is { Kind: Tok.Func, Text: "Add" });
        Assert.Contains(lines[1], s => s is { Kind: Tok.Number, Text: "42" });
        Assert.Equal(Tok.Comment, lines[2][0].Kind); // the block comment continues onto the next line
        Assert.Contains(lines[2], s => s.Kind == Tok.String && s.Text.Contains("not a comment"));
    }

    [Theory]
    [InlineData("python", "def f(x):\n    '''doc\n    more'''\n    return x # c")]
    [InlineData("json", "{ \"a\": [1, true, \"b\"] }")]
    [InlineData("yaml", "key: value # c\nlist:\n  - 1")]
    [InlineData("xml", "<a href=\"x\"><!-- c\n c --></a>")]
    [InlineData("bash", "if [ -n \"$HOME\" ]; then echo ${X}; fi # c")]
    [InlineData("markdown", "# T\n- a `b` [[c]]\n```\ncode\n```")]
    [InlineData("sql", "SELECT a FROM t WHERE x = 'y' -- c")]
    [InlineData("rust", "fn main() { let a: &'static str = \"x\"; println!(\"{}\", a); }")]
    [InlineData("ini", "[s]\nk = v ; c")]
    public void Highlighting_never_loses_or_invents_text(string lang, string code)
    {
        var lines = Syntax.Highlight(code, lang);
        Assert.Equal(code, string.Join("\n", lines.Select(l => string.Concat(l.Select(s => s.Text)))));
        Assert.Contains(lines.SelectMany(l => l), s => s.Kind != Tok.Text);
        Assert.Contains("<span class=\"tk-", Syntax.ToHtml(code, lang));
    }

    [Fact]
    public void Json_keys_differ_from_string_values_and_html_is_escaped()
    {
        var spans = Syntax.Highlight("{ \"name\": \"<b>\" }", "json")[0];
        Assert.Contains(spans, s => s is { Kind: Tok.Attr, Text: "\"name\"" });
        Assert.Contains(spans, s => s is { Kind: Tok.String, Text: "\"<b>\"" });
        Assert.Contains("&lt;b&gt;", Syntax.ToHtml("{ \"name\": \"<b>\" }", "json"));
        Assert.Equal("a &lt; b", Syntax.ToHtml("a < b", null));
    }
}

public class WikiFeatureTests
{
    [Fact]
    public void Front_matter_is_split_and_rendered_as_a_table()
    {
        const string md = "---\ntitle: \"Hello: world\"\ntags: [a, b]\naliases:\n  - one\n  - two\nurl: https://example.com\n---\n# Body\n";
        var (meta, body) = WikiService.SplitFrontMatter(md);
        Assert.NotNull(meta);
        Assert.Equal("# Body\n", body);
        Assert.Equal("Hello: world", meta!.First(m => m.Key == "title").Values[0]);
        Assert.Equal(["a", "b"], meta.First(m => m.Key == "tags").Values);
        Assert.Equal(["one", "two"], meta.First(m => m.Key == "aliases").Values);
        var html = WikiService.Render(md);
        Assert.Contains("class=\"frontmatter\"", html);
        Assert.Contains("<span class=\"chip\">one</span>", html);
        Assert.Contains("<a href=\"https://example.com\">", html);
        Assert.Contains("<h1", html);
        Assert.DoesNotContain("title:", html);
    }

    [Theory]
    [InlineData("# No front matter\n---\nx: y\n---\n")]
    [InlineData("---\nnever closed\n")]
    [InlineData("--- not a fence\n")]
    public void Text_without_front_matter_is_left_alone(string md)
    {
        var (meta, body) = WikiService.SplitFrontMatter(md);
        Assert.Null(meta);
        Assert.Equal(md, body);
    }

    [Fact]
    public void Fenced_code_is_highlighted_for_its_language()
    {
        var html = WikiService.Render("```python\ndef f():\n    return 1  # c\n```\n\n```\nplain <x>\n```\n\n```nosuchlang\na < b\n```");
        Assert.Contains("class=\"language-python\"", html);
        Assert.Contains("<span class=\"tk-k\">def</span>", html);
        Assert.Contains("<span class=\"tk-c\"># c</span>", html);
        Assert.Contains("plain &lt;x&gt;", html);
        Assert.Contains("a &lt; b", html);
    }

    [Fact]
    public void Daily_note_uses_the_configured_name_and_is_created_once()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        var settings = new WikiSettings();
        var day = new DateTime(2026, 11, 12);
        var path = WikiService.DailyNotePath(day, settings);
        Assert.Equal($"Daily/2026-11-12 {day:dddd}.md", path);
        Assert.Equal(path, wiki.EnsureDailyNote(day, settings));
        Assert.StartsWith($"# 2026-11-12 {day:dddd}", wiki.Read(path));
        wiki.Write(path, "mine");
        Assert.Equal(path, wiki.EnsureDailyNote(day, settings));
        Assert.Equal("mine", wiki.Read(path)); // an existing note is never overwritten

        Assert.Equal("2026/11/12.md", WikiService.DailyNotePath(day, new WikiSettings { DailyNoteFolder = "", DailyNoteFormat = "yyyy'/'MM'/'dd" }));
        Assert.Equal("J/2026-11-12.md", WikiService.DailyNotePath(day, new WikiSettings { DailyNoteFolder = "/J/", DailyNoteFormat = "yyyy-MM-dd" }));
        Assert.DoesNotContain("..", WikiService.DailyNotePath(day, new WikiSettings { DailyNoteFormat = "'../../x'" }).Split('/'));
    }

    [Fact]
    public void Pasted_bytes_land_next_to_the_page_without_overwriting()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        var first = wiki.ImportBytes([1, 2, 3], "Notes", "Pasted image.png");
        var second = wiki.ImportBytes([4], "Notes", "Pasted image.png");
        Assert.Equal("Notes/Pasted image.png", first);
        Assert.Equal("Notes/Pasted image-1.png", second);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(wiki.Resolve(first)));
        Assert.Equal("![Pasted image](<Pasted image.png>)", WikiService.LinkMarkdown("Pasted image.png"));
        Assert.Equal("[report](report.pdf)", WikiService.LinkMarkdown("report.pdf"));
        Assert.Throws<UnauthorizedAccessException>(() => wiki.ImportBytes([1], "../outside", "x.png"));
        Assert.Contains("<img src=\"Pasted%20image.png\"", WikiService.Render("![Pasted image](<Pasted image.png>)"));
    }

    [Fact]
    public void Files_are_previewed_by_kind()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        Assert.Equal(WikiKind.Image, WikiService.KindOf("a/b.PNG"));
        Assert.Equal(WikiKind.Pdf, WikiService.KindOf("x.pdf"));
        Assert.Equal(WikiKind.Audio, WikiService.KindOf("x.mp3"));
        Assert.Equal(WikiKind.Video, WikiService.KindOf("x.webm"));
        Assert.Equal(WikiKind.Text, WikiService.KindOf("x.json"));
        Assert.Equal(WikiKind.Markdown, WikiService.KindOf("x.md"));
        Assert.Equal(WikiKind.Other, WikiService.KindOf("x.bin"));

        wiki.ImportBytes("{ \"a\": 1 }"u8.ToArray(), "", "data.json");
        wiki.ImportBytes("plain words"u8.ToArray(), "", "README");
        wiki.ImportBytes([0, 1, 2, 0], "", "blob.bin");
        wiki.ImportBytes([1], "", "pic.png");
        Assert.True(wiki.IsEditable("data.json"));
        Assert.True(wiki.IsEditable("README"));       // no extension, but it reads as text
        Assert.False(wiki.IsEditable("blob.bin"));
        Assert.Contains("class=\"imgview\" data-src=\"pic.png\"", wiki.RenderFile("pic.png"));
        Assert.Contains("<span class=\"l\">", wiki.RenderFile("data.json"));
        Assert.Contains("tk-a", wiki.RenderFile("data.json"));
        Assert.Contains("no built-in preview", wiki.RenderFile("blob.bin"));
        Assert.Contains("data-act=\"open\"", wiki.RenderFile("blob.bin"));
        Assert.Contains("unsaved", wiki.RenderFile("README", "unsaved text"));
    }

    [Fact]
    public void Scroll_positions_are_remembered_up_to_a_limit()
    {
        using var box = new Sandbox();
        var limit = 3;
        var store = new UiStateStore(box.Paths, () => limit);
        foreach (var name in new[] { "a", "b", "c", "d" }) store.RememberWiki(name + ".md", editor: name[0], preview: 1);
        Assert.Null(store.WikiPosition("a.md"));                       // the oldest fell off
        Assert.Equal('d', store.WikiPosition("d.md")!.Editor);
        store.RememberWiki("b.md", preview: 99);                       // touching one makes it the newest
        store.RememberWiki("e.md");
        Assert.Null(store.WikiPosition("c.md"));
        Assert.Equal(99, store.WikiPosition("b.md")!.Preview);
        Assert.Equal('b', store.WikiPosition("b.md")!.Editor);         // fields not given are kept
        store.RenameWiki("b.md", "z.md");
        Assert.NotNull(store.WikiPosition("z.md"));
        store.ForgetWiki("z.md");
        Assert.Null(store.WikiPosition("z.md"));
        store.Save();
        Assert.Equal(2, new UiStateStore(box.Paths).Data.WikiPositions.Count);
    }
}

public class EventFeatureTests
{
    [Fact]
    public void Attachments_are_copied_listed_and_removed_with_the_event()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var events = new EventService(box.Paths, config);
        var source = box.Home("agenda.txt");
        File.WriteAllText(source, "items");
        var e = new EventItem { Title = "Review", Start = DateTime.Now.AddHours(1), Location = "https://meet.example/x", Color = "#ff2fd4" };
        Assert.Throws<InvalidOperationException>(() => events.Attach(e, source)); // needs an id first
        events.Add(e);
        Assert.Equal("agenda.txt", events.Attach(e, source));
        Assert.Equal("agenda-1.txt", events.Attach(e, source));
        events.Save();
        Assert.Equal("items", File.ReadAllText(events.AttachmentPath(e, "agenda.txt")));

        using var reloaded = new EventService(box.Paths, config);
        var again = reloaded.Get("Review");
        Assert.Equal(["agenda.txt", "agenda-1.txt"], again.Attachments);
        Assert.Equal("https://meet.example/x", again.Location);
        Assert.Equal("#ff2fd4", again.Color);

        events.Detach(e, "agenda.txt");
        Assert.False(File.Exists(events.AttachmentPath(e, "agenda.txt")));
        var folder = events.AttachmentFolder(e);
        events.Remove(e.Id);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(source)); // the original is never touched
    }

    [Fact]
    public async Task Commands_set_location_colour_and_attachments()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        var file = box.Home("map.png");
        File.WriteAllBytes(file, [1]);
        var args = new System.Text.Json.Nodes.JsonObject { ["title"] = "Trip", ["location"] = "12 Main St", ["color"] = "#0af", ["attach"] = new System.Text.Json.Nodes.JsonArray(file) };
        var r = await app.Commands.ExecuteAsync("event add", args);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        Assert.Equal("12 Main St", r["result"]!["location"]!.GetValue<string>());
        Assert.Equal("#00aaff", r["result"]!["color"]!.GetValue<string>());
        Assert.Single(app.Events.Get("Trip").Attachments);
        r = await app.Commands.ExecuteAsync("event edit", new System.Text.Json.Nodes.JsonObject { ["id"] = "Trip", ["color"] = "purple-ish" });
        Assert.False(r["ok"]!.GetValue<bool>());
    }
}

public class DiffTests
{
    private const string Sample = "diff --git a/f.txt b/f.txt\nindex 1..2 100644\n--- a/f.txt\n+++ b/f.txt\n@@ -1,5 +1,6 @@ header\n one\n-two\n+TWO\n+two and a half\n three\n-- four\n+four\n five\n";

    [Fact]
    public void Parse_numbers_lines_and_keeps_removed_lines_that_look_like_headers()
    {
        var doc = DiffDoc.Parse("commit abc\n\n    message\n\n" + Sample);
        Assert.Equal(DiffKind.Meta, doc.Lines[0].Kind);
        var file = Assert.Single(doc.Files);
        Assert.Equal("f.txt", file.Path);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal((1, 5, 1, 6, "header"), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount, hunk.Section));
        var del = doc.Lines.Single(l => l is { Kind: DiffKind.Del, Text: "- four" }); // "-- four" is a removed line, not a file header
        Assert.Equal(4, del.Old);
        var add = doc.Lines.Single(l => l is { Kind: DiffKind.Add, Text: "two and a half" });
        Assert.Equal(3, add.New);
        Assert.Equal((5, 6), (doc.Lines[^1].Old, doc.Lines[^1].New));
    }

    [Fact]
    public void Line_patch_folds_the_rest_of_the_hunk_into_context()
    {
        var doc = DiffDoc.Parse(Sample);
        var add = doc.Lines.Single(l => l.Text == "two and a half");
        var forward = doc.LinePatch(add.File, add.Hunk, add.Body, reverse: false);
        Assert.Contains("@@ -1,5 +1,6 @@ header\n one\n two\n+two and a half\n three\n - four\n five\n", forward);
        var reverse = doc.LinePatch(add.File, add.Hunk, add.Body, reverse: true);
        Assert.Contains("@@ -1,5 +1,6 @@ header\n one\n TWO\n+two and a half\n three\n four\n five\n", reverse);
    }

    /// <summary>The real thing: stage and unstage by hunk and by line in a repository, and check what git ends up with.</summary>
    [Fact]
    public async Task Hunks_and_lines_can_be_staged_unstaged_and_discarded()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var dir = Path.Combine(box.Root, "repo");
        Directory.CreateDirectory(dir);
        async Task<string> Git(params string[] args)
        {
            var r = await Shell.RunAsync("git", args, dir);
            Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Stderr}");
            return r.Stdout;
        }
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.com");
        await Git("config", "user.name", "T");
        await Git("config", "commit.gpgsign", "false");
        var file = Path.Combine(dir, "f.txt");
        var original = Enumerable.Range(1, 30).Select(i => $"line {i}").ToArray();
        File.WriteAllLines(file, original);
        await Git("add", "."); await Git("commit", "-q", "-m", "first");

        var edited = original.ToList();
        edited[1] = "line 2 changed";          // hunk 1: one line replaced…
        edited.Insert(2, "line 2b added");     // …and one added
        edited[25] = "line 25 changed";        // hunk 2 (index shifted by the insert)
        File.WriteAllLines(file, edited);

        var repos = new RepoService(box.Paths, config);
        var repo = await repos.AddAsync(dir);
        async Task<DiffDoc> Diff(bool staged) => DiffDoc.Parse(await repos.DiffAsync(repo, "f.txt", staged));
        async Task<string[]> Index() => (await Git("show", ":f.txt")).TrimEnd('\n').Split('\n');

        var doc = await Diff(false);
        Assert.Equal(2, doc.Files[0].Hunks.Count);

        // Stage only the second hunk.
        await repos.ApplyPatchAsync(repo, doc.HunkPatch(0, 1, false), cached: true, reverse: false);
        var index = await Index();
        Assert.Equal("line 2", index[1]);
        Assert.Contains("line 25 changed", index);
        Assert.Equal(30, index.Length);

        // Stage one added line of the first hunk, by itself.
        doc = await Diff(false);
        var added = doc.Lines.Single(l => l is { Kind: DiffKind.Add, Text: "line 2b added" });
        Assert.True(doc.CanPatchLine(added));
        await repos.ApplyPatchAsync(repo, doc.LinePatch(added.File, added.Hunk, added.Body, false), cached: true, reverse: false);
        index = await Index();
        Assert.Equal(["line 1", "line 2", "line 2b added", "line 3"], index[..4]);

        // Unstage that line again from the staged diff, leaving the second hunk staged.
        var staged = await Diff(true);
        var stagedLine = staged.Lines.Single(l => l is { Kind: DiffKind.Add, Text: "line 2b added" });
        await repos.ApplyPatchAsync(repo, staged.LinePatch(stagedLine.File, stagedLine.Hunk, stagedLine.Body, true), cached: true, reverse: true);
        index = await Index();
        Assert.Equal(["line 1", "line 2", "line 3"], index[..3]);
        Assert.Contains("line 25 changed", index);

        // Unstage the remaining hunk: the index is back to the commit.
        staged = await Diff(true);
        await repos.ApplyPatchAsync(repo, staged.HunkPatch(0, 0, true), cached: true, reverse: true);
        Assert.Equal(original, await Index());

        // Discard one removed line from the working tree: "line 2" comes back, the rest of the edit stays.
        doc = await Diff(false);
        var removed = doc.Lines.Single(l => l is { Kind: DiffKind.Del, Text: "line 2" });
        await repos.ApplyPatchAsync(repo, doc.LinePatch(removed.File, removed.Hunk, removed.Body, true), cached: false, reverse: true);
        var work = File.ReadAllLines(file);
        Assert.Equal(["line 1", "line 2", "line 2 changed", "line 2b added"], work[..4]);
        Assert.Contains("line 25 changed", work);
    }

    [Fact]
    public async Task Commit_count_and_position_in_history()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var dir = Path.Combine(box.Root, "repo");
        Directory.CreateDirectory(dir);
        async Task<string> Git(params string[] args) { var r = await Shell.RunAsync("git", args, dir); Assert.True(r.ExitCode == 0, r.Stderr); return r.Stdout.Trim(); }
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.com"); await Git("config", "user.name", "T"); await Git("config", "commit.gpgsign", "false");
        var repos = new RepoService(box.Paths, config);
        var repo = await repos.AddAsync(dir);
        Assert.Null(await repos.CommitCountAsync(repo));   // no commits yet

        var hashes = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            await Git("commit", "-q", "--allow-empty", "-m", $"c{i}");
            hashes.Add(await Git("rev-parse", "HEAD"));
        }
        Assert.Equal(5, await repos.CommitCountAsync(repo));
        Assert.Equal(0, await repos.CommitPositionAsync(repo, hashes[4]));       // newest
        Assert.Equal(4, await repos.CommitPositionAsync(repo, hashes[0][..7]));  // oldest, by short hash

        // A commit only on another branch has no place in the current history.
        await Git("checkout", "-q", "-b", "side");
        await Git("commit", "-q", "--allow-empty", "-m", "side");
        var side = await Git("rev-parse", "HEAD");
        await Git("checkout", "-q", "main");
        Assert.Null(await repos.CommitPositionAsync(repo, side));
    }

    [Fact]
    public async Task Branches_commits_and_remotes()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var dir = Path.Combine(box.Root, "repo");
        Directory.CreateDirectory(dir);
        async Task Git(params string[] args) { var r = await Shell.RunAsync("git", args, dir); Assert.True(r.ExitCode == 0, r.Stderr); }
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.com"); await Git("config", "user.name", "T"); await Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a\n");
        await Git("add", "."); await Git("commit", "-q", "-m", "first");
        var repos = new RepoService(box.Paths, config);
        var repo = await repos.AddAsync(dir);

        await repos.CreateBranchAsync(repo, "feature");
        Assert.Equal("feature", await repos.CurrentBranchAsync(repo));
        File.WriteAllText(Path.Combine(dir, "b.txt"), "b\n");
        File.Move(Path.Combine(dir, "a.txt"), Path.Combine(dir, "renamed.txt"));
        await repos.StageAllAsync(repo);
        await repos.CommitAsync(repo, "second: add b\n\nwith a body");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repos.CommitAsync(repo, "nothing staged"));

        var branches = await repos.BranchesAsync(repo);
        Assert.Equal(["feature", "main"], branches.Select(b => b.Name).Order());
        Assert.True(branches.Single(b => b.Name == "feature").IsCurrent);
        Assert.Single(await repos.LogAsync(repo, null, "main"));
        var log = await repos.LogAsync(repo, null, "feature");
        Assert.Equal(2, log.Count);

        var info = await repos.CommitInfoAsync(repo, log[0].Hash);
        Assert.Equal("second: add b", info!.Subject);
        Assert.Equal("with a body", info.Body);
        Assert.Equal(40, info.Hash.Length);
        var files = await repos.CommitFilesAsync(repo, log[0].Hash);
        Assert.Contains(files, f => f is { Status: "A", Path: "b.txt" });
        Assert.Contains(files, f => f is { Status: "R", Path: "renamed.txt", OriginalPath: "a.txt" });
        Assert.Single(await repos.CommitFilesAsync(repo, log[1].Hash)); // the root commit lists its files too
        Assert.Contains("+b", await repos.CommitFileDiffAsync(repo, log[0].Hash, "b.txt"));

        await repos.SwitchAsync(repo, "main");
        Assert.False(File.Exists(Path.Combine(dir, "b.txt")));
        await Assert.ThrowsAsync<ArgumentException>(() => repos.SwitchAsync(repo, "--orphan"));
        await repos.MergeAsync(repo, "feature");
        await repos.DeleteBranchAsync(repo, "feature");
        Assert.Single(await repos.BranchesAsync(repo));

        File.WriteAllText(Path.Combine(dir, "junk.tmp"), "x");
        await repos.DiscardAsync(repo, "junk.tmp", untracked: true);
        Assert.False(File.Exists(Path.Combine(dir, "junk.tmp")));
        Assert.Null(await repos.RemoteUrlAsync(repo));
    }

    [Theory]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://user:secret@example.com/a/b", "https://example.com/a/b")]
    [InlineData("ssh://git@host.example:2222/group/repo.git", "https://host.example/group/repo")]
    [InlineData("/srv/git/repo.git", null)]
    [InlineData("", null)]
    public void Remote_addresses_become_web_addresses(string remote, string? expected) => Assert.Equal(expected, RepoService.ToWebUrl(remote));
}

public class PullRequestWatchTests
{
    [Fact]
    public void Github_cli_output_becomes_items_with_mine_and_active()
    {
        const string json = """
            [{"number":7,"title":"Add search","state":"OPEN","isDraft":false,"author":{"login":"alex"},"url":"https://x/pull/7","headRefName":"search","baseRefName":"main","updatedAt":"2026-11-10T10:00:00Z","reviewDecision":"APPROVED"},
             {"number":6,"title":"WIP","state":"OPEN","isDraft":true,"author":{"login":"sam"},"url":"https://x/pull/6","headRefName":"wip","baseRefName":"main"},
             {"number":5,"title":"Old","state":"MERGED","isDraft":false,"author":{"login":"alex"},"url":"https://x/pull/5"}]
            """;
        var result = RepoPullRequestWatchProvider.Parse(json, "Alex");
        Assert.Equal("2 open, 1 mine", result.Summary);
        Assert.Equal("info", result.Status);
        Assert.Equal(["approved", "draft", "merged"], result.Items.Select(i => i.Status));
        Assert.Equal([true, false, true], result.Items.Select(i => i.Mine));
        Assert.Equal([true, true, false], result.Items.Select(i => i.Active));
        Assert.Equal("#7 Add search", result.Items[0].Title);
        Assert.Contains("search → main", result.Items[0].Detail);
    }

    [Fact]
    public void Other_shapes_are_accepted()
    {
        // Azure DevOps style names, and explicit flags from a script.
        var ado = RepoPullRequestWatchProvider.Parse("""[{"pullRequestId":12,"title":"T","status":"active","createdBy":{"displayName":"Sam"},"sourceRefName":"refs/heads/x","mine":true}]""", null);
        Assert.Equal(("open", true, true, "Sam"), (ado.Items[0].Status, ado.Items[0].Mine, ado.Items[0].Active, ado.Items[0].Author));
        // The ordinary watch result object.
        var watch = RepoPullRequestWatchProvider.Parse("""{"status":"ok","summary":"custom","items":[{"title":"A","status":"closed","author":"me"},{"title":"B","status":"open","author":"you"}]}""", "me");
        Assert.Equal("custom", watch.Summary);
        Assert.Equal([false, true], watch.Items.Select(i => i.Active));
        Assert.Equal([true, false], watch.Items.Select(i => i.Mine));
        Assert.Equal("No pull requests", RepoPullRequestWatchProvider.Parse("", null).Summary);
        Assert.Equal("No open pull requests (1 finished)", RepoPullRequestWatchProvider.Parse("""[{"number":1,"title":"x","state":"closed"}]""", null).Summary);
    }

    [Fact]
    public async Task A_repository_pull_request_list_is_a_watch()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        var dir = Path.Combine(box.Root, "repo");
        Directory.CreateDirectory(dir);
        Assert.Null(app.Watches.FindRepoPullRequests(dir));
        var watch = app.Watches.EnsureRepoPullRequests("repo", dir, """printf '[{"number":1,"title":"From %s","state":"open","author":"me","mine":true}]' "$AVAMUX_REPO_NAME" """);
        Assert.Same(watch, app.Watches.EnsureRepoPullRequests("repo", dir + "/"));
        Assert.Same(watch, app.Watches.FindRepoPullRequests(dir));
        Assert.Contains(app.Watches.Items, w => w.Kind == "repo-pr");           // it is on the Watching page like any other
        await app.Watches.CheckAsync(watch);
        Assert.Null(watch.LastError);
        Assert.Equal("#1 From repo", watch.LastResult!.Items.Single().Title);
        Assert.Equal("1 open, 1 mine", watch.Summary);
        Assert.True(WatchService.IsPullRequestKind(watch.Kind));

        // The default command from the settings is used when the watch has none.
        watch.Settings.Remove("command");
        app.Config.Set("repositories.pullRequestCommand", System.Text.Json.Nodes.JsonValue.Create("echo '[]'"));
        await app.Watches.CheckAsync(watch);
        Assert.Equal("No pull requests", watch.Summary);
    }
}

public class SearchTests
{
    [Fact]
    public async Task One_query_finds_things_in_every_source_and_sources_can_be_picked()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Wiki.Write("Recipes/Pancakes", "# Pancakes\n\nUse a zebrafish pan.\n");
        app.Events.Add(new EventItem { Title = "Zebrafish feeding", Start = DateTime.Now.AddDays(1), Location = "Lab 3" });
        app.Watches.Add(new WatchItem { Name = "Tank", Kind = "command", LastResult = new Avamux.Plugins.WatchResult { Summary = "zebrafish count ok", Items = [new() { Title = "zebrafish #4", Link = "https://x/4" }] } });
        File.WriteAllText(Path.Combine(box.Paths.EventLogsDir, "run.log"), "first\nfed the ZEBRAFISH\nlast\n");

        var all = await app.Search.SearchAsync("zebrafish");
        Assert.Contains(all, h => h is { Source: "wiki", Target: "Recipes/Pancakes.md", Line: 3 });
        Assert.Contains(all, h => h.Source == "events" && h.Title == "Zebrafish feeding");
        Assert.Contains(all, h => h.Source == "watches" && h.Title == "Tank");
        Assert.Contains(all, h => h is { Source: "watches", Title: "zebrafish #4", Extra: "https://x/4" });
        Assert.Contains(all, h => h is { Source: "logs", Line: 2 } && h.Detail.Contains("ZEBRAFISH"));

        var onlyWiki = await app.Search.SearchAsync("zebrafish", ["wiki"]);
        Assert.All(onlyWiki, h => Assert.Equal("wiki", h.Source));
        Assert.NotEmpty(onlyWiki);
        Assert.Contains(await app.Search.SearchAsync("pancakes"), h => h is { Source: "wiki", Detail: "file name" });
        Assert.Contains(await app.Search.SearchAsync("scrollback"), h => h is { Source: "settings", Target: "terminal.scrollback" });
        Assert.Contains(await app.Search.SearchAsync("lab 3"), h => h.Source == "events");
        Assert.Empty(await app.Search.SearchAsync("   "));
        Assert.Empty(await app.Search.SearchAsync("no-such-thing-anywhere-xyzzy"));

        var r = await app.Commands.ExecuteAsync("search", new System.Text.Json.Nodes.JsonObject { ["query"] = "zebrafish", ["source"] = "events" });
        Assert.Equal("events", r["result"]![0]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Repositories_are_searched_by_name_file_commit_and_content()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        var dir = Path.Combine(box.Root, "aquarium");
        Directory.CreateDirectory(dir);
        async Task Git(params string[] args) { var r = await Shell.RunAsync("git", args, dir); Assert.True(r.ExitCode == 0, r.Stderr); }
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "t@example.com"); await Git("config", "user.name", "T"); await Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, "tank.cs"), "class Tank { int guppies; }\n");
        await Git("add", "."); await Git("commit", "-q", "-m", "Count the guppies");
        var repo = await app.Repos.AddAsync(dir);

        Assert.Contains(await app.Search.SearchAsync("aquarium"), h => h.Source == "repositories" && h.Target == repo.Id && h.Extra is null);
        var hits = await app.Search.SearchAsync("guppies", ["repositories"]);
        Assert.Contains(hits, h => h.Extra?.StartsWith("commit:") == true && h.Title == "Count the guppies");
        Assert.Contains(hits, h => h is { Extra: "file:tank.cs", Line: 1 });
        Assert.Contains(await app.Search.SearchAsync("tank.cs", ["repositories"]), h => h is { Extra: "file:tank.cs", Line: 0 });
    }

    [Fact]
    public void Snippets_centre_on_the_match()
    {
        var line = new string('a', 300) + " needle " + new string('b', 300);
        var s = Avamux.Core.Search.SearchService.Snippet(line, "needle", 80);
        Assert.Contains("needle", s);
        Assert.StartsWith("…", s);
        Assert.EndsWith("…", s);
        Assert.True(s.Length <= 82);
        Assert.Equal("short", Avamux.Core.Search.SearchService.Snippet("  short  ", "x"));
    }
}

public class SettingsFeatureTests
{
    [Fact]
    public void New_settings_have_defaults_and_can_be_set()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var s = config.Settings;
        Assert.Equal("standard", s.Ui.NavStyle);
        Assert.Equal("simple", s.Theme.Name);
        Assert.True(s.Ai.Chat); // AI Chat is a real page now, shown by default
        Assert.False(s.Ai.Tts.Enabled); // Kokoro is off until asked for
        Assert.Equal("haiku", s.Ai.ActionsModel);
        Assert.True(s.Ai.Workloads); // AI Workloads is a real page now too
        Assert.Equal("yyyy-MM-dd dddd", s.Wiki.DailyNoteFormat);
        Assert.Equal(500, s.Wiki.RememberedPositions);
        config.Set("ui.navStyle", ConfigService.Coerce("ui.navStyle", "mini"));
        config.Set("ai.workloads", ConfigService.Coerce("ai.workloads", "off"));
        config.Set("theme.name", ConfigService.Coerce("theme.name", "classic90s"));
        Assert.Equal("mini", config.Settings.Ui.NavStyle);
        Assert.False(config.Settings.Ai.Workloads);
        Assert.Equal("90's Desktop Computer OS Classic", Themes.Resolve(config.Settings.Theme).Name);
        Assert.Contains("claude", TerminalManager.AgentNames(config.Settings.Terminal));
    }

    [Fact]
    public async Task Theme_and_today_commands()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        var r = await app.Commands.ExecuteAsync("theme set", new System.Text.Json.Nodes.JsonObject { ["name"] = "Synthwave Kat" });
        Assert.Equal("synthwave-kat", r["result"]!.GetValue<string>());
        Assert.Equal("synthwave-kat", app.Config.Settings.Theme.Name);
        Assert.False((await app.Commands.ExecuteAsync("theme set", new System.Text.Json.Nodes.JsonObject { ["name"] = "nope" }))["ok"]!.GetValue<bool>());
        // The terminal page gets the theme's colours unless the settings name their own.
        Assert.Equal("#0d0221", app.Web.TerminalOptions()["background"]!.GetValue<string>());
        app.Config.Set("terminal.background", System.Text.Json.Nodes.JsonValue.Create("#123456"));
        Assert.Equal("#123456", app.Web.TerminalOptions()["background"]!.GetValue<string>());

        r = await app.Commands.ExecuteAsync("wiki today", new System.Text.Json.Nodes.JsonObject { ["no-open"] = true });
        var path = r["result"]!.GetValue<string>();
        Assert.Equal(WikiService.DailyNotePath(DateTime.Now, app.Config.Settings.Wiki), path);
        Assert.True(File.Exists(app.Wiki.Resolve(path)));
    }
}
