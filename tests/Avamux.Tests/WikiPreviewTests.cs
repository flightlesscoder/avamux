using System.IO.Compression;
using Avamux.Core.Wiki;

namespace Avamux.Tests;

public class WikiPreviewTests
{
    [Fact]
    public void Big_media_waits_for_a_click_and_small_media_does_not_preload()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths, () => 1000);
        File.WriteAllBytes(Path.Combine(wiki.Root, "big.mp4"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(wiki.Root, "small.mp3"), new byte[10]);
        File.WriteAllBytes(Path.Combine(wiki.Root, "big.png"), new byte[5000]);

        var big = wiki.RenderFile("big.mp4");
        Assert.Contains("data-load", big);
        Assert.Contains("<template>", big);
        Assert.Contains("data-load", wiki.RenderFile("big.png"));

        var small = wiki.RenderFile("small.mp3");
        Assert.DoesNotContain("data-load", small);
        Assert.Contains("preload=\"none\"", small);
    }

    [Fact]
    public void A_limit_of_zero_loads_everything_at_once()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths, () => 0);
        File.WriteAllBytes(Path.Combine(wiki.Root, "big.png"), new byte[5000]);
        Assert.DoesNotContain("data-load", wiki.RenderFile("big.png"));
    }

    [Fact]
    public void Csv_becomes_a_table_with_quoted_fields_understood()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        File.WriteAllText(Path.Combine(wiki.Root, "t.csv"), "name,note\n\"Smith, J\",\"say \"\"hi\"\"\nthere\"\n<b>,x\n");
        var html = wiki.RenderFile("t.csv");
        Assert.Contains("<th>name</th>", html);
        Assert.Contains("<td>Smith, J</td>", html);
        Assert.Contains("say &quot;hi&quot;\nthere", html);
        Assert.Contains("&lt;b&gt;", html);
    }

    [Fact]
    public void Zip_files_are_listed()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        using (var zip = ZipFile.Open(Path.Combine(wiki.Root, "a.zip"), ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("docs/readme.txt");
            using var w = new StreamWriter(e.Open()); w.Write("hello");
        }
        Assert.Equal(WikiKind.Archive, WikiService.KindOf("a.zip"));
        var html = wiki.RenderFile("a.zip");
        Assert.Contains("docs/readme.txt", html);
        Assert.Contains("1 file", html);
    }
}
