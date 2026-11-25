using Avamux.Core.Wiki;

namespace Avamux.Tests;

public class MarkdownLinkTests
{
    [Fact]
    public void Finds_markdown_wiki_auto_and_bare_links()
    {
        var line = "See [the docs](https://example.com/a_(b)) and [[Projects/Avamux|Avamux]], <https://x.org/y>, https://plain.example/path?q=1. Also file:///home/me/notes.txt and [here](Daily/2026-11-19%20Thursday.md \"title\") and [top](#heading).";
        var links = MarkdownLinks.Find(line);
        Assert.Equal(["https://example.com/a_(b)", "Projects/Avamux", "https://x.org/y", "https://plain.example/path?q=1", "file:///home/me/notes.txt", "Daily/2026-11-19%20Thursday.md", "#heading"], links.Select(l => l.Target));
        Assert.Equal(["the docs", "Avamux", "https://x.org/y", "https://plain.example/path?q=1", "file:///home/me/notes.txt", "here", "top"], links.Select(l => l.Label));
        Assert.Equal([LinkKind.Web, LinkKind.Wiki, LinkKind.Web, LinkKind.Web, LinkKind.File, LinkKind.Wiki, LinkKind.Anchor], links.Select(l => l.Kind));
        foreach (var l in links) Assert.Equal(l.Label, line.Substring(l.LabelStart, l.Label.Length));
        Assert.Equal("[the docs](https://example.com/a_(b))", line.Substring(links[0].Start, links[0].Length));
        Assert.True(links[1].IsWikiLink);
    }

    [Fact]
    public void Pictures_and_code_are_not_links()
    {
        Assert.Empty(MarkdownLinks.Find("![a picture](https://example.com/p.png) and `https://in.code/x` and ``[x](y)``"));
        Assert.Equal("https://after.example", MarkdownLinks.Find("`code` then https://after.example").Single().Target);
    }

    [Fact]
    public void The_link_under_a_column()
    {
        var line = "go to [the page](Other.md) now";
        Assert.Null(MarkdownLinks.At(line, 2));
        Assert.Equal("Other.md", MarkdownLinks.At(line, 6)!.Target);
        Assert.Equal("Other.md", MarkdownLinks.At(line, 25)!.Target);
        Assert.Null(MarkdownLinks.At(line, 26));
    }

    [Fact]
    public void Bare_file_addresses_become_links_in_the_preview_but_not_in_code()
    {
        var html = WikiService.Render("See file:///etc/hostname and [named](file:///tmp/a.txt) and <file:///tmp/b.txt>.\n\n```\nfile:///in/code\n```\n");
        Assert.Contains("<a href=\"file:///etc/hostname\">file:///etc/hostname</a>", html);
        Assert.Contains("<a href=\"file:///tmp/a.txt\">named</a>", html);
        Assert.Contains("<a href=\"file:///tmp/b.txt\">file:///tmp/b.txt</a>", html);
        Assert.DoesNotContain("<a href=\"file:///in/code", html);
    }
}
