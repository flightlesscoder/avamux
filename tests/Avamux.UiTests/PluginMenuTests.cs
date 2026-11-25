using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avamux.Plugins;
using Avamux.Views;

namespace Avamux.UiTests;

public sealed class UiTestPlugin : IAvamuxPlugin, IMenuContributor
{
    public string Id => "ui-test";
    public string Name => "UI test";
    public string Description => "";
    public void Activate(IPluginContext context) => context.AddMenuItems(this);
    public void Deactivate() { }
    public IEnumerable<PluginMenuItem> Items(MenuContext context)
    {
        if (context.Editor is not { } editor) yield break;
        yield return PluginMenuItem.Submenu("Test Actions", [
            PluginMenuItem.Command("Shout", () => editor.ReplaceSelection(editor.SelectedText.ToUpperInvariant())),
            PluginMenuItem.Command("Replace All", () => editor.ReplaceText("replaced\n")),
            PluginMenuItem.Command("Add Below", () => editor.InsertAfterSelection("added line")),
            new PluginMenuItem { Header = "Off", IsEnabled = false },
        ]);
        if (context.Menu == Menus.WikiTree) yield return PluginMenuItem.Command("Tree Item " + context.WikiPath, () => { });
    }
}

/// <summary>Plugins' items in the wiki's menus, and the editor they act on.</summary>
public class PluginMenuTests
{
    private static void Fill(WikiHarness h)
    {
        h.Wiki.Write("Home.md", "first line\nsecond line\n");
        h.Services.Plugins.AddBuiltIn(() => new UiTestPlugin());
        h.Services.Plugins.Start();
    }

    private static ContextMenu OpenEditorMenu(WikiHarness h)
    {
        var menu = h.Editor.TextArea.ContextMenu!;
        // As a right-click does: the control asks for its menu, which raises Opening.
        h.Editor.TextArea.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
        h.Settle();
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem Find(ItemsControl menu, string header) =>
        menu.Items.OfType<MenuItem>().First(m => m.Header as string == header);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public void The_editor_menu_ends_with_plugin_items_that_edit_the_page_as_one_undo_step()
    {
        using var h = new WikiHarness(Fill, open: "Home.md");
        h.Editor.Select(0, 5); // "first"
        var menu = OpenEditorMenu(h);
        var actions = Find(menu, "Test Actions");
        Assert.False(Find(actions, "Off").IsEnabled);
        Click(Find(actions, "Shout"));
        h.Settle();
        menu.Close();
        Assert.Equal("FIRST line\nsecond line\n", h.Text);
        h.Editor.Undo();
        Assert.Equal("first line\nsecond line\n", h.Text);

        // Opening the menu again does not pile the items up.
        menu = OpenEditorMenu(h);
        Assert.Single(menu.Items.OfType<MenuItem>(), m => m.Header as string == "Test Actions");
        Click(Find(Find(menu, "Test Actions"), "Replace All"));
        h.Settle();
        menu.Close();
        Assert.Equal("replaced\n", h.Text);
        h.Editor.Undo();
        Assert.Equal("first line\nsecond line\n", h.Text);

        h.Editor.Select(0, 3);
        menu = OpenEditorMenu(h);
        Click(Find(Find(menu, "Test Actions"), "Add Below"));
        h.Settle();
        menu.Close();
        Assert.Equal("first line\nadded line\nsecond line\n", h.Text);
    }

    [AvaloniaFact]
    public void Switching_the_plugin_off_takes_its_items_out_of_the_menu()
    {
        using var h = new WikiHarness(Fill, open: "Home.md");
        h.Services.Plugins.SetEnabled("ui-test", false);
        var menu = OpenEditorMenu(h);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header as string == "Test Actions");
        Assert.Contains(menu.Items.OfType<MenuItem>(), m => m.Header as string == "Copy");
        menu.Close();
    }

    [AvaloniaFact]
    public void The_web_menus_get_items_as_json_and_run_the_one_picked()
    {
        using var h = new WikiHarness(Fill, open: "Home.md");
        var ran = "";
        var web = new PluginMenus.WebMenu();
        var items = web.Items(new MenuContext { Menu = Menus.WikiPreview, WikiPath = "Home.md", Editor = new Recorder(t => ran = t) });
        var sub = items[0]!.AsObject();
        Assert.Equal("Test Actions", sub["h"]!.GetValue<string>());
        var shout = sub["c"]!.AsArray().First(i => i!["h"]?.GetValue<string>() == "Shout")!;
        Assert.False(sub["c"]!.AsArray().First(i => i!["h"]?.GetValue<string>() == "Off")!["e"]!.GetValue<bool>());
        web.Pick(shout["k"]!.GetValue<string>());
        h.Settle();
        Assert.Equal("SEL", ran);
    }

    private sealed class Recorder(Action<string> done) : IWikiEditor
    {
        public string Path => "Home.md";
        public string Text => "";
        public string SelectedText => "sel";
        public bool IsReadOnly => false;
        public void ReplaceSelection(string text) => done(text);
        public void ReplaceText(string text) => done(text);
        public void InsertAfterSelection(string text) => done(text);
        public void Save() { }
    }
}
