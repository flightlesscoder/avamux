using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avamux.Core;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace Avamux.UiTests;

/// <summary>Links in the wiki editor: a menu of their own on right-click, Ctrl+click to follow; and the editor's side of scrolling with the preview.</summary>
public sealed class WikiEditorLinkTests
{
    private const string Page = "# Links\n\nRead [the docs](https://example.com/docs) first.\n\nThen [[Other]] and file:///tmp/nowhere-avamux.txt here.\n";

    private static void Fill(WikiHarness h)
    {
        File.WriteAllText(h.Full("Links.md"), Page);
        File.WriteAllText(h.Full("Other.md"), "# Other\n");
    }

    private static ContextMenu OpenMenu(WikiHarness h)
    {
        var menu = h.Editor.TextArea.ContextMenu!;
        h.Editor.TextArea.RaiseEvent(new ContextRequestedEventArgs());
        h.Settle();
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static List<string> Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(m => (string)m.Header!).ToList();
    private static void Click(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().First(m => m.Header as string == header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>Where a character of the page is drawn, in window coordinates.</summary>
    private static Point PointAt(WikiHarness h, int offset)
    {
        var view = h.Editor.TextArea.TextView;
        var loc = h.Editor.Document.GetLocation(offset);
        var p = view.GetVisualPosition(new TextViewPosition(loc), VisualYPosition.LineMiddle) - view.ScrollOffset + new Vector(3, 0);
        return view.TranslatePoint(p, h.Window)!.Value;
    }

    [AvaloniaFact]
    public void A_web_link_offers_open_and_copy()
    {
        using var h = new WikiHarness(Fill, open: "Links.md");
        h.Editor.CaretOffset = Page.IndexOf("the docs") + 2;
        var menu = OpenMenu(h);
        Assert.Equal(["Open in Browser", "Copy Link", "Copy Link Text"], Headers(menu).Take(3));
        Click(menu, "Copy Link");
        h.Settle();
        Assert.Equal("https://example.com/docs", ((NullAppHost)h.Services.Host).Clipboard);
        menu.Close();

        // Off the link, the menu is the editing one again.
        h.Editor.CaretOffset = 2;
        menu = OpenMenu(h);
        Assert.Equal("Undo", Headers(menu)[0]);
        Assert.DoesNotContain("Copy Link", Headers(menu));
    }

    [AvaloniaFact]
    public void Right_click_uses_the_link_under_the_pointer_and_a_missing_file_cannot_be_opened()
    {
        using var h = new WikiHarness(Fill, open: "Links.md");
        h.Editor.CaretOffset = 0; // the caret is elsewhere: the pointer decides
        h.Window.MouseDown(PointAt(h, Page.IndexOf("file:///") + 4), MouseButton.Right);
        h.Window.MouseUp(PointAt(h, Page.IndexOf("file:///") + 4), MouseButton.Right); // a right-click's release opens the menu
        h.Settle();
        var menu = h.Editor.TextArea.ContextMenu!;
        Assert.True(menu.IsOpen);
        Assert.Equal(["Open", "Show in Folder", "Copy Link"], Headers(menu).Take(3));
        Assert.False(menu.Items.OfType<MenuItem>().First().IsEnabled);
        Click(menu, "Copy Link");
        h.Settle();
        Assert.Equal("file:///tmp/nowhere-avamux.txt", ((NullAppHost)h.Services.Host).Clipboard);
    }

    [AvaloniaFact]
    public void Ctrl_click_on_a_wiki_link_opens_the_page()
    {
        using var h = new WikiHarness(Fill, open: "Links.md");
        var at = PointAt(h, Page.IndexOf("[[Other]]") + 3);
        h.Window.MouseDown(at, MouseButton.Left, RawInputModifiers.Control);
        h.Window.MouseUp(at, MouseButton.Left, RawInputModifiers.Control);
        h.Settle();
        Assert.Equal("Other.md", h.Page.CurrentPath);
    }

    [AvaloniaFact]
    public void The_editor_scrolls_to_a_line_and_says_which_line_is_at_its_top()
    {
        var text = "# Long\n\n" + string.Join("\n", Enumerable.Range(1, 400).Select(i => $"Line {i} of a long page."));
        using var h = new WikiHarness(x => File.WriteAllText(x.Full("Long.md"), text), open: "Long.md");
        h.Page.ScrollEditorToLine(200.5);
        h.Settle();
        Assert.InRange(h.Page.EditorTopLine(), 200.3, 200.7);
        h.Page.ScrollEditorToLine(1);
        h.Settle();
        Assert.InRange(h.Page.EditorTopLine(), 1, 1.05);
    }

    [AvaloniaFact]
    public void A_page_opens_where_it_was_left()
    {
        var text = "# Long\n\n" + string.Join("\n", Enumerable.Range(1, 400).Select(i => $"Line {i} of a long page."));
        using var h = new WikiHarness(x => { File.WriteAllText(x.Full("Long.md"), text); File.WriteAllText(x.Full("Short.md"), "# Short\n"); }, open: "Long.md");
        h.Page.ScrollEditorToLine(150);
        h.Settle();
        h.Page.Open("Short.md");
        h.Settle();
        h.Page.Open("Long.md");
        h.Settle();
        Assert.InRange(h.Page.EditorTopLine(), 149.5, 150.5);
    }
}
