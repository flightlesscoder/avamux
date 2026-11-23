using Avamux.Core.Wiki;

namespace Avamux.Tests;

/// <summary>Finding the pictures a page shows, for the editor to show them too: the same ones the preview shows.</summary>
public class WikiPicturesTests
{
    private static string Source(string text, WikiPicture p) => text.Substring(p.Start, p.Length);

    [Fact]
    public void Finds_markdown_pictures_with_their_exact_source()
    {
        var text = "# T\n\nSee ![a cat](cat.png) and ![dog](<my dog.jpg> \"title\").\n";
        var found = WikiPictures.Find(text);
        Assert.Equal(2, found.Count);
        Assert.Equal("![a cat](cat.png)", Source(text, found[0]));
        Assert.Equal("cat.png", found[0].Url);
        Assert.Equal("a cat", found[0].Alt);
        Assert.Equal("![dog](<my dog.jpg> \"title\")", Source(text, found[1]));
        Assert.Equal("my dog.jpg", found[1].Url);
    }

    [Fact]
    public void Nothing_in_code_blocks_or_code_spans()
    {
        var text = "```\n![x](a.png)\n```\n\n    ![y](b.png)\n\nInline `![z](c.png)` code.\n\n~~~md\n![w](d.png)\n~~~\n";
        Assert.Empty(WikiPictures.Find(text));
    }

    [Fact]
    public void Positions_are_exact_inside_lists_and_quotes()
    {
        var text = "- item\n  - nested ![n](n.png)\n> quote ![q](q.png)\n> > deeper ![d](d.png)\n1. first ![o](o.png)\n";
        var found = WikiPictures.Find(text);
        Assert.Equal(["![n](n.png)", "![q](q.png)", "![d](d.png)", "![o](o.png)"], found.Select(p => Source(text, p)));
    }

    [Fact]
    public void Reference_pictures_use_their_definition()
    {
        var text = "Look: ![logo][l]\n\n[l]: images/logo.png\n";
        var p = Assert.Single(WikiPictures.Find(text));
        Assert.Equal("![logo][l]", Source(text, p));
        Assert.Equal("images/logo.png", p.Url);
    }

    [Fact]
    public void Html_img_tags_count_too()
    {
        var text = "Inline <img src=\"a.png\" alt=\"A\" width=\"20\"> here.\n\n<div>\n<img src='b.png'>\n</div>\n";
        var found = WikiPictures.Find(text);
        Assert.Equal(2, found.Count);
        Assert.Equal("<img src=\"a.png\" alt=\"A\" width=\"20\">", Source(text, found[0]));
        Assert.Equal("A", found[0].Alt);
        Assert.Equal("b.png", found[1].Url);
        Assert.Equal("<img src='b.png'>", Source(text, found[1]));
    }

    [Fact]
    public void Videos_recordings_and_players_are_not_pictures()
    {
        var text = "![v](clip.mp4) ![a](talk.mp3) ![y](https://www.youtube.com/watch?v=x) ![p](photo.webp)";
        var p = Assert.Single(WikiPictures.Find(text));
        Assert.Equal("photo.webp", p.Url);
    }

    [Fact]
    public void Front_matter_is_not_part_of_the_page_but_offsets_still_count_it()
    {
        var text = "---\ntitle: ![no](x.png)\ntags: [a]\n---\n![yes](y.png)\n";
        var p = Assert.Single(WikiPictures.Find(text));
        Assert.Equal("![yes](y.png)", Source(text, p));
    }

    [Fact]
    public void Crlf_line_ends_keep_offsets_right()
    {
        var text = "Line\r\n\r\n![a](a.png)\r\ntext ![b](b.png)\r\n";
        Assert.Equal(["![a](a.png)", "![b](b.png)"], WikiPictures.Find(text).Select(p => Source(text, p)));
    }

    [Fact]
    public void Pages_without_pictures_are_cheap_and_empty()
    {
        Assert.Empty(WikiPictures.Find(""));
        Assert.Empty(WikiPictures.Find("# Just text\n\n[a link](x.png)\n"));
    }

    [Theory]
    [InlineData("a.png", "", "a.png")]
    [InlineData("a.png", "Trips", "Trips/a.png")]
    [InlineData("img/my%20dog.png", "Trips", "Trips/img/my dog.png")]
    [InlineData("../shared/a.png", "Trips/2026", "Trips/shared/a.png")]
    [InlineData("./a.png?v=2#top", "Trips", "Trips/a.png")]
    [InlineData("/wiki/raw/x/a.png", "Trips", "x/a.png")]
    public void Addresses_resolve_against_the_page_folder_like_the_preview(string url, string folder, string expected)
    {
        Assert.Equal(expected, WikiPictures.Resolve(url, folder)?.WikiPath);
    }

    [Theory]
    [InlineData("../../escape.png", "Trips")]
    [InlineData("/etc/passwd", "")]
    [InlineData("file:///etc/passwd", "")]
    [InlineData("ftp://host/a.png", "")]
    [InlineData("data:text/plain;base64,aGk=", "")]
    public void Addresses_the_preview_cannot_show_resolve_to_nothing(string url, string folder)
    {
        Assert.Null(WikiPictures.Resolve(url, folder));
    }

    [Fact]
    public void Web_addresses_and_written_in_pictures_resolve()
    {
        Assert.Equal("https://example.com/a.png", WikiPictures.Resolve("https://example.com/a.png", "x")?.Remote);
        var data = WikiPictures.Resolve("data:image/png;base64,iVBORw0KGgo=", "");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, data?.Data);
    }

    [Fact]
    public void Img_tags_bring_the_size_they_ask_for()
    {
        var found = WikiPictures.Find("<img src=\"a.png\" width=\"32\"> <img src=b.png height=20px> <img src='c.png' width=\"50%\">");
        Assert.Equal(3, found.Count);
        Assert.Equal((32.0, (double?)null), (found[0].Width!.Value, found[0].Height));
        Assert.Equal(20.0, found[1].Height);
        Assert.Null(found[2].Width); // a share of the page is not a size the editor can follow
    }
}
