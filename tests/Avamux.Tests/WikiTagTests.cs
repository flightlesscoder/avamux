using Avamux.Core;
using Avamux.Core.Wiki;

namespace Avamux.Tests;

public class WikiTagParsingTests
{
    private static List<string> Tags(string md) => WikiTags.Find(md).Select(m => m.Tag).ToList();

    [Fact]
    public void Finds_tags_in_text_lists_and_quotes()
    {
        Assert.Equal(["work", "idea", "avamux/browser", "Later"], Tags("Some #work here\n- a #idea.\n> quoted #avamux/browser, then #Later!"));
    }

    [Fact]
    public void Headings_are_not_tags_but_a_tag_at_the_start_of_a_line_is()
    {
        Assert.Equal(["start"], Tags("# Title\n## Sub heading\n#start of a line"));
    }

    [Fact]
    public void Code_links_urls_and_html_are_skipped()
    {
        var md = """
            Inline `#notatag` and ``#also `nope` ``
            ```
            #include <stdio.h>
            ```
            ~~~~
            #fenced
            ~~~~
            [see #inlink](Page.md) [[Page#anchor]] ![pic #x](a.png) [ref #r][1]
            https://example.com/#frag www.example.com/#frag <a href="#top">x</a> &#38;
            page#anchor C# issue#12 #123 #-dash
            after all that #real
            """;
        Assert.Equal(["real"], Tags(md));
    }

    [Fact]
    public void Indented_code_after_a_blank_line_is_skipped_but_list_continuations_are_not()
    {
        Assert.Empty(Tags("Text\n\n    #code here\n    #more code\n"));
        Assert.Equal(["nested"], Tags("- item\n\n    continued #nested\n"));
    }

    [Fact]
    public void Trailing_punctuation_and_slashes_are_not_part_of_a_tag()
    {
        Assert.Equal(["a/b", "x-y", "z"], Tags("#a/b/ and #x-y- and (#z)"));
        Assert.Equal("a/b", WikiTags.Normalize("#A/B/"));
    }

    [Fact]
    public void Front_matter_tags_count_in_every_shape()
    {
        Assert.Equal(["one", "two", "body"], Tags("---\ntitle: x\ntags: [one, \"#two\"]\n---\n#body"));
        Assert.Equal(["a", "b"], Tags("---\ntags:\n  - a\n  - b\n---\ntext"));
        Assert.Equal(["a", "b"], Tags("---\ntags: a, b\n---\n"));
        // Front matter lines themselves are not scanned for #words.
        Assert.Empty(Tags("---\ncolor: \"#cafe\"\n---\n"));
    }

    [Fact]
    public void Unicode_letters_work_and_hex_looking_words_are_tags()
    {
        Assert.Equal(["café", "日本", "fade"], Tags("#café #日本 #fade"));
    }

    [Fact]
    public void Linkify_turns_tags_into_preview_links_and_leaves_code_alone()
    {
        var html = WikiService.Render("Hello #World and `#code`\n");
        Assert.Contains("<a class=\"tag\" href=\"#\" data-tag=\"world\">#World</a>", html);
        Assert.Contains("<code>#code</code>", html);
        Assert.DoesNotContain("data-tag=\"code\"", html);
    }

    [Fact]
    public void AddTags_extends_a_Tags_line_or_adds_one_and_skips_tags_already_there()
    {
        Assert.Equal("# T\n\nText #a\n\nTags: #b #c\n", WikiTags.AddTags("# T\n\nText #a\n", ["a", "#b", "c", "B", "not valid"]));
        Assert.Equal("Text\n\nTags: #x #y\n", WikiTags.AddTags("Text\n\nTags: #x\n", ["y"]));
        Assert.Equal("Text #x\n", WikiTags.AddTags("Text #x\n", ["#X"])); // nothing new: unchanged
    }

    [Fact]
    public void AddTags_goes_into_front_matter_when_the_page_has_it()
    {
        Assert.Equal("---\ntitle: t\ntags: [a, b]\n---\nbody", WikiTags.AddTags("---\ntitle: t\ntags: [a]\n---\nbody", ["b"]));
        Assert.Equal("---\ntitle: t\ntags: [b]\n---\nbody", WikiTags.AddTags("---\ntitle: t\n---\nbody", ["b"]));
        Assert.Equal("---\ntags:\n  - a\n  - b\n---\nbody", WikiTags.AddTags("---\ntags:\n  - a\n---\nbody", ["b"]));
    }
}

public class WikiTagIndexTests
{
    private static (Sandbox Box, WikiService Wiki, WikiTagIndex Index) Make(Action<WikiService>? fill = null)
    {
        var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        fill?.Invoke(wiki);
        var index = new WikiTagIndex(wiki, box.Paths);
        return (box, wiki, index);
    }

    private static void WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time.");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public async Task Builds_counts_and_finds_pages_with_nested_tags()
    {
        var (box, wiki, index) = Make(w =>
        {
            w.Write("A.md", "#avamux and #avamux/browser\n");
            w.Write("Notes/B.md", "line one\nsee #Avamux/Browser here\n");
            w.Write("C.md", "#other\n");
        });
        using var _ = box;
        using var __ = index;
        index.Start();
        await index.Ready;

        var tags = index.Tags();
        Assert.Equal(["avamux", "avamux/browser", "other"], tags.Select(t => t.Key));
        Assert.Equal(2, tags.Single(t => t.Key == "avamux/browser").Pages);
        Assert.Equal(["A.md", "Notes/B.md"], index.Pages("avamux").Select(h => h.Page));
        Assert.Equal(["A.md"], index.Pages("avamux", nested: false).Select(h => h.Page));
        var b = index.Pages("#AVAMUX/browser").Single(h => h.Page == "Notes/B.md");
        Assert.Equal(2, b.Line);
        Assert.Equal("see #Avamux/Browser here", b.Snippet);
        Assert.Equal(["avamux", "avamux/browser"], index.TagsOf("A.md"));
    }

    [Fact]
    public async Task Follows_writes_moves_and_deletes_made_through_the_wiki()
    {
        var (box, wiki, index) = Make(w => w.Write("A.md", "#one\n"));
        using var _ = box;
        using var __ = index;
        index.Start();
        await index.Ready;

        wiki.Write("B.md", "#two\n");
        WaitFor(() => index.Pages("two").Count == 1);
        wiki.Move("A.md", "Moved/A2.md");
        WaitFor(() => index.Pages("one").SingleOrDefault()?.Page == "Moved/A2.md");
        wiki.Move("Moved", "Elsewhere");
        WaitFor(() => index.Pages("one").SingleOrDefault()?.Page == "Elsewhere/A2.md");
        wiki.Delete("B.md");
        WaitFor(() => index.Pages("two").Count == 0);
        Assert.DoesNotContain(index.Tags(), t => t.Key == "two");
    }

    [Fact]
    public async Task Picks_up_changes_made_outside_avamux()
    {
        var (box, wiki, index) = Make();
        using var _ = box;
        using var __ = index;
        index.Start();
        await index.Ready;
        File.WriteAllText(Path.Combine(wiki.Root, "Outside.md"), "#external\n");
        WaitFor(() => index.Pages("external").Count == 1);
    }

    [Fact]
    public async Task The_cache_is_used_and_only_changed_pages_are_read_again()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        wiki.Write("A.md", "#cached\n");
        wiki.Write("B.md", "#b\n");
        using (var first = new WikiTagIndex(wiki, box.Paths)) { first.Start(); await first.Ready; }
        Assert.True(File.Exists(box.Paths.StateFile("wiki-tags")));

        // Change B behind the cache's back (and give it a different size so the change shows).
        File.WriteAllText(Path.Combine(wiki.Root, "B.md"), "#bee changed\n");
        using var second = new WikiTagIndex(wiki, box.Paths);
        second.Start();
        await second.Ready;
        Assert.Single(second.Pages("cached"));
        Assert.Single(second.Pages("bee"));
        Assert.Empty(second.Pages("b", nested: false));
    }

    [Fact]
    public async Task Search_has_a_tags_source()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Wiki.Write("Diary.md", "about the #browser panes\n");
        await app.Tags.RefreshAsync();
        var hits = await app.Search.SearchAsync("#brow", ["tags"]);
        var hit = Assert.Single(hits);
        Assert.Equal("Diary.md", hit.Target);
        Assert.Equal(1, hit.Line);
        Assert.StartsWith("#browser", hit.Detail);
    }

    [Fact]
    public async Task Cli_lists_tags_and_their_pages()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Wiki.Write("X.md", "#alpha #alpha/beta\n");
        await app.Tags.RefreshAsync();
        var registry = Avamux.Core.Commands.CommandRegistry.Build(null);
        async Task<string> Run(params string[] argv)
        {
            var parsed = Avamux.Core.Commands.CliParser.Parse(registry, argv);
            Assert.Null(parsed.Error);
            var reply = await app.Commands.ExecuteAsync(parsed.Command!.Name, parsed.Args);
            Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
            return reply["result"]!.ToJsonString();
        }
        Assert.Contains("alpha/beta", await Run("wiki", "tags"));
        Assert.Contains("X.md", await Run("wiki", "tag", "alpha", "--exact"));
    }
}
