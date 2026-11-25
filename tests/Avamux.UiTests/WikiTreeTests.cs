using Avalonia;
using Avalonia.Headless;
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

    private static TreeViewItem Find(WikiHarness h, string path) =>
        Tree(h).GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as WikiNode)?.Path == path);

    private static void RightClick(WikiHarness h, TreeViewItem item)
    {
        var header = item.GetVisualDescendants().OfType<TextBlock>().First();
        var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), h.Window)!.Value;
        h.Window.MouseMove(at);
        h.Window.MouseDown(at, MouseButton.Right);
        h.Window.MouseUp(at, MouseButton.Right);
        h.Settle();
    }

    private static void Pages(WikiHarness h)
    {
        Directory.CreateDirectory(h.Full("Notes"));
        File.WriteAllText(h.Full("Notes/a.md"), "# a\n");
        File.WriteAllText(h.Full("Notes/b.md"), "# b\n");
    }

    [AvaloniaFact]
    public void Right_clicking_a_page_that_is_not_selected_opens_its_menu()
    {
        using var h = new WikiHarness(Pages, open: "Notes/a.md");
        RightClick(h, Find(h, "Notes/b.md"));
        var item = Find(h, "Notes/b.md");
        Assert.True(item.ContextMenu?.IsOpen);
        Assert.Equal("Notes/a.md", h.Page.CurrentPath); // a right-click does not open the page
        item.ContextMenu!.Close();
    }

    [AvaloniaFact]
    public void The_menu_stays_open_while_an_edit_is_saved()
    {
        using var h = new WikiHarness(Pages, open: "Notes/a.md");
        h.Editor.CaretOffset = h.Editor.Text.Length;
        h.Type("more");
        RightClick(h, Find(h, "Notes/b.md"));
        var item = Find(h, "Notes/b.md");
        var menu = item.ContextMenu!;
        Assert.True(menu.IsOpen);
        h.Settle(minMs: 2600); // auto-save writes the page, and the wiki changes
        h.Wiki.NotifyChanged();
        h.Settle(minMs: 400);
        Assert.Contains("more", File.ReadAllText(h.Full("Notes/a.md")));
        Assert.True(menu.IsOpen);
        Assert.Same(item, Find(h, "Notes/b.md")); // the entry it is for was not built again
        menu.Close();
        h.Settle(minMs: 100);
        File.WriteAllText(h.Full("Notes/c.md"), "# c\n");
        h.Wiki.NotifyChanged();
        h.Settle(minMs: 400);
        Find(h, "Notes/c.md"); // the tree is built again once the menu is closed
    }

    [AvaloniaFact]
    public void New_folder_from_a_page_makes_a_sibling_and_from_a_folder_a_child()
    {
        using var h = new WikiHarness(Pages, open: "Notes/a.md");
        void NewFolder(string path, string header, string name, string label)
        {
            var item = Find(h, path);
            item.RaiseEvent(new ContextRequestedEventArgs());
            h.Settle();
            var entry = item.ContextMenu!.Items.OfType<MenuItem>().First(m => m.Header as string == header);
            entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            item.ContextMenu.Close();
            h.Settle();
            var dialog = h.Window.OwnedWindows.Single();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label);
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = name;
            dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "OK").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            h.Settle(minMs: 300);
        }
        NewFolder("Notes/b.md", "New Folder…", "Beside", "Folder name (in Notes/)");
        Assert.True(Directory.Exists(h.Full("Notes/Beside")));
        NewFolder("Notes", "New Folder Here…", "Inside", "Folder name (in Notes/)");
        Assert.True(Directory.Exists(h.Full("Notes/Inside")));
    }

    [AvaloniaFact]
    public void A_new_folder_from_a_menu_leaves_the_selected_folder_as_it_was()
    {
        using var h = new WikiHarness(x => { Pages(x); Directory.CreateDirectory(x.Full("Other")); File.WriteAllText(x.Full("Other/c.md"), "# c\n"); }, open: "Notes/a.md");
        Window Prompt(string label)
        {
            var dialog = h.Window.OwnedWindows.Single();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label);
            return dialog;
        }
        // From the menu of a page in another folder, a new folder is begun there, and then cancelled.
        var item = Find(h, "Other/c.md");
        item.RaiseEvent(new ContextRequestedEventArgs());
        h.Settle();
        item.ContextMenu!.Items.OfType<MenuItem>().First(m => m.Header as string == "New Folder…").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        item.ContextMenu.Close();
        h.Settle();
        Prompt("Folder name (in Other/)").Close();
        h.Settle(minMs: 200);
        // The toolbar still makes pages and folders in the folder of the page that is open.
        foreach (var (tip, label) in new[] { ("New page in the selected folder", "Page name (in Notes/). Use / to create folders."), ("New folder", "Folder name (in Notes/)") })
        {
            h.Page.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == tip).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            h.Settle();
            Prompt(label).Close();
            h.Settle(minMs: 200);
        }
    }
}
