using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avamux.Core.Wiki;

namespace Avamux.UiTests;

/// <summary>The wiki's folder tree over folders of many files: listed in batches, closed until opened, menus made on demand.</summary>
public class WikiTreeTests
{
    private static TreeView Tree(WikiHarness h) => h.Page.GetVisualDescendants().OfType<TreeView>().First();

    private static TreeViewItem Item(WikiHarness h, string path) =>
        Tree(h).Items.OfType<TreeViewItem>().First(i => (i.Tag as WikiNode)?.Path == path);

    private static void Many(WikiHarness h)
    {
        Directory.CreateDirectory(h.Full("Photos"));
        for (var i = 0; i < 1200; i++) File.WriteAllBytes(h.Full($"Photos/p{i:0000}.png"), []);
        Directory.CreateDirectory(h.Full("Small"));
        for (var i = 0; i < 5; i++) File.WriteAllText(h.Full($"Small/n{i}.md"), "# n\n");
    }

    [AvaloniaFact]
    public void A_big_folder_starts_closed_and_lists_its_files_in_batches()
    {
        using var h = new WikiHarness(Many);
        var photos = Item(h, "Photos");
        var small = Item(h, "Small");
        Assert.False(photos.IsExpanded);
        Assert.True(small.IsExpanded);
        Assert.Equal(5, small.Items.Count);
        Assert.Single(photos.Items); // only the stand-in that shows the expander

        photos.IsExpanded = true;
        h.Settle();
        Assert.Equal(501, photos.Items.Count); // 500 files and "Show more"
        var more = Assert.IsType<TreeViewItem>(photos.Items[^1]);
        var button = Assert.IsType<Button>(more.Header);
        Assert.Equal("Show 500 more of 700…", button.Content);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        h.Settle();
        Assert.Equal(1001, photos.Items.Count);
        ((Button)((TreeViewItem)photos.Items[^1]!).Header!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        h.Settle();
        Assert.Equal(1200, photos.Items.Count);
        Assert.Equal("p1199.png", ((WikiNode)((TreeViewItem)photos.Items[^1]!).Tag!).Name);
    }

    [AvaloniaFact]
    public void An_opened_big_folder_stays_open_when_the_tree_is_rebuilt()
    {
        using var h = new WikiHarness(Many);
        Item(h, "Photos").IsExpanded = true;
        h.Settle();
        h.Wiki.NotifyChanged(); // the tree is built again after every change to the wiki
        h.Settle(minMs: 300);
        Assert.True(Item(h, "Photos").IsExpanded);
        Item(h, "Small").IsExpanded = false;
        h.Wiki.NotifyChanged();
        h.Settle(minMs: 300);
        Assert.False(Item(h, "Small").IsExpanded);
    }

    [AvaloniaFact]
    public void The_folder_of_the_open_page_is_open_however_big()
    {
        using var h = new WikiHarness(w => { Many(w); File.WriteAllText(w.Full("Photos/About.md"), "# About\n"); }, open: "Photos/About.md");
        Assert.True(Item(h, "Photos").IsExpanded);
    }

    [AvaloniaFact]
    public void An_entry_gets_its_menu_when_it_is_asked_for()
    {
        using var h = new WikiHarness(Many);
        var small = Item(h, "Small");
        var note = (TreeViewItem)small.Items[0]!;
        Assert.Null(note.ContextMenu);
        note.RaiseEvent(new ContextRequestedEventArgs());
        h.Settle();
        Assert.NotNull(note.ContextMenu);
        Assert.True(note.ContextMenu!.IsOpen);
        Assert.Contains(note.ContextMenu.Items.OfType<MenuItem>(), m => m.Header as string == "Rename or Move…");
        note.ContextMenu.Close();
    }
}
