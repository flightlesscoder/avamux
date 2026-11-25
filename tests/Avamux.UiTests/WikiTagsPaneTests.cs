using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avamux.Core.Wiki;
using Avamux.Views;

namespace Avamux.UiTests;

/// <summary>The wiki's Tags view: the switch beside Explorer, the tag list, a tag's pages and opening one.</summary>
public class WikiTagsPaneTests
{
    private static void Fill(WikiHarness h)
    {
        h.Wiki.Write("Home.md", "# Home\n");
        h.Wiki.Write("Projects/Avamux.md", "# Avamux\n\nThe #avamux/browser panes and #ideas\n");
        h.Wiki.Write("Daily/2026-11-19.md", "notes\n\n#avamux work today\n");
        h.Services.Tags.Start();
        h.Services.Tags.Ready.Wait(5000);
    }

    private static List<ListBox> Lists(WikiHarness h) => h.Page.TagsPane.GetVisualDescendants().OfType<ListBox>().ToList();

    [AvaloniaFact]
    public void Tags_view_lists_tags_nested_and_opens_a_page_at_its_line()
    {
        using var h = new WikiHarness(Fill);
        h.Page.ShowPane("tags");
        h.Settle(minMs: 300);
        var tags = Lists(h)[0];
        var rows = Assert.IsAssignableFrom<IList<WikiTagsPane.TagRow>>(tags.ItemsSource);
        Assert.Equal(["avamux", "avamux/browser", "ideas"], rows.Select(r => r.Key));
        Assert.Equal(1, rows.Single(r => r.Key == "avamux/browser").Depth);
        Assert.Equal("browser", rows.Single(r => r.Key == "avamux/browser").Label);

        h.Page.TagsPane.Show("avamux");
        h.Settle();
        var pages = Lists(h)[1];
        Assert.True(pages.IsVisible);
        var hits = Assert.IsAssignableFrom<IList<TagHit>>(pages.ItemsSource);
        Assert.Equal(["Daily/2026-11-19.md", "Projects/Avamux.md"], hits.Select(x => x.Page));

        pages.SelectedIndex = 1;
        pages.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = pages });
        h.Settle(minMs: 100);
        Assert.Equal(h.Wiki.Read("Projects/Avamux.md"), h.Text);
        // The editor went to the tag's line and selected it.
        Assert.Equal(3, h.Editor.TextArea.Caret.Line);

        h.Page.TagsPane.Back();
        h.Settle();
        Assert.Null(h.Page.TagsPane.CurrentTag);
        Assert.True(Lists(h)[0].IsVisible);
    }

    [AvaloniaFact]
    public void The_pane_choice_is_remembered_and_ShowTag_switches_to_it()
    {
        using (var h = new WikiHarness(Fill))
        {
            h.Page.ShowTag("ideas");
            h.Settle();
            Assert.Equal("tags", h.Services.Ui.Data.WikiPane);
            Assert.Equal("ideas", h.Page.TagsPane.CurrentTag);
            Assert.Single((IList<TagHit>)Lists(h)[1].ItemsSource!);
            h.Page.ShowPane("explorer");
            Assert.Equal("explorer", h.Services.Ui.Data.WikiPane);
        }
    }

    [AvaloniaFact]
    public void Opening_on_the_tags_view_shows_tags_indexed_before_the_page_existed()
    {
        using var h = new WikiHarness(x => { Fill(x); x.Services.Ui.Data.WikiPane = "tags"; });
        h.Settle(minMs: 100);
        var rows = Assert.IsAssignableFrom<IList<WikiTagsPane.TagRow>>(Lists(h)[0].ItemsSource);
        Assert.Equal(3, rows.Count);
    }

    [AvaloniaFact]
    public void Filtering_narrows_tags_and_pages()
    {
        using var h = new WikiHarness(Fill);
        h.Page.ShowPane("tags");
        h.Settle();
        var filter = h.Page.TagsPane.GetVisualDescendants().OfType<TextBox>().First();
        filter.Text = "idea";
        h.Settle();
        Assert.Equal(["ideas"], ((IList<WikiTagsPane.TagRow>)Lists(h)[0].ItemsSource!).Select(r => r.Key));
        h.Page.TagsPane.Show("avamux");
        h.Settle();
        filter.Text = "daily";
        h.Settle();
        Assert.Equal(["Daily/2026-11-19.md"], ((IList<TagHit>)Lists(h)[1].ItemsSource!).Select(x => x.Page));
    }

    [Fact]
    public void Rows_put_nested_tags_under_parents_that_have_no_pages_of_their_own()
    {
        var rows = WikiTagsPane.Rows([new TagCount("a/b", "A/B", 3), new TagCount("a-c", "a-c", 1), new TagCount("z", "Z", 2)]);
        Assert.Equal(["a", "a/b", "a-c", "z"], rows.Select(r => r.Key));
        Assert.Equal(0, rows[0].Count);
        Assert.Equal("B", rows[1].Label);
    }
}
