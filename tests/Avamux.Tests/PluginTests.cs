using Avamux.Core;
using Avamux.Core.Plugins;
using Avamux.Plugins;

namespace Avamux.Tests;

public sealed class TestMenuPlugin : IAvamuxPlugin, IMenuContributor
{
    public static int Activations, Deactivations;
    public string Id => "test-menu";
    public string Name => "Test menu";
    public string Description => "Adds a test item.";
    public void Activate(IPluginContext context)
    {
        Activations++;
        context.AddMenuItems(this);
        context.AddWatchProvider(new TestWatch());
    }
    public void Deactivate() => Deactivations++;
    public IEnumerable<PluginMenuItem> Items(MenuContext context)
    {
        if (context.Menu != Menus.WikiEditor) yield break;
        yield return PluginMenuItem.Submenu("Test", [PluginMenuItem.Command("Do it " + context.WikiPath, () => { })]);
    }

    private sealed class TestWatch : IWatchProvider
    {
        public string Kind => "test-kind";
        public string Description => "test";
        public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>();
        public Task<WatchResult> CheckAsync(WatchContext context, CancellationToken cancellationToken) => Task.FromResult(new WatchResult());
    }
}

public sealed class BrokenPlugin : IAvamuxPlugin, IMenuContributor
{
    public string Id => "broken";
    public string Name => "Broken";
    public string Description => "";
    public void Activate(IPluginContext context) => context.AddMenuItems(this);
    public void Deactivate() { }
    public IEnumerable<PluginMenuItem> Items(MenuContext context) => throw new InvalidOperationException("boom");
}

public sealed class FailingPlugin : IAvamuxPlugin
{
    public string Id => "failing";
    public string Name => "Failing";
    public string Description => "";
    public void Activate(IPluginContext context) => throw new InvalidOperationException("cannot start");
    public void Deactivate() { }
}

[Collection("plugins")] // TestMenuPlugin counts calls in statics
public class PluginTests
{
    private static MenuContext Editor(string path = "Home.md") => new() { Menu = Menus.WikiEditor, WikiPath = path };

    [Fact]
    public async Task A_built_in_plugin_adds_menu_items_and_a_watch_kind_and_takes_them_back_when_switched_off()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Plugins.AddBuiltIn(() => new TestMenuPlugin());
        app.Plugins.Start();

        var entry = app.Plugins.Get("test-menu");
        Assert.True(entry.Enabled);
        Assert.Equal(PluginSource.BuiltIn, entry.Source);
        Assert.Contains("menu items", entry.Adds);
        var item = Assert.Single(app.Plugins.MenuItems(Editor("Notes.md")));
        Assert.Equal("Test", item.Header);
        Assert.Equal("Do it Notes.md", Assert.Single(item.Children).Header);
        Assert.Empty(app.Plugins.MenuItems(new MenuContext { Menu = Menus.Terminal }));
        Assert.Contains("test-kind", app.Watches.Kinds);

        app.Plugins.SetEnabled("test-menu", false);
        Assert.Empty(app.Plugins.MenuItems(Editor()));
        Assert.DoesNotContain("test-kind", app.Watches.Kinds);
        Assert.Throws<InvalidOperationException>(() => app.Plugins.Uninstall("test-menu")); // built in: only switched off

        // Remembered: a new start leaves it off.
        await using var again = new AppServices(box.Paths);
        again.Plugins.AddBuiltIn(() => new TestMenuPlugin());
        again.Plugins.Start();
        Assert.False(again.Plugins.Get("test-menu").Enabled);
        Assert.Empty(again.Plugins.MenuItems(Editor()));
        again.Plugins.SetEnabled("Test menu", true); // by name too
        Assert.Single(again.Plugins.MenuItems(Editor()));
    }

    [Fact]
    public async Task A_plugin_that_fails_is_listed_with_its_problem_and_does_not_stop_the_others()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Plugins.AddBuiltIn(() => new FailingPlugin());
        app.Plugins.AddBuiltIn(() => new BrokenPlugin());
        app.Plugins.AddBuiltIn(() => new TestMenuPlugin());
        app.Plugins.Start();
        Assert.Equal("cannot start", app.Plugins.Get("failing").Error);
        // Broken throws while making its items: left out, the rest of the menu still comes.
        Assert.Equal("Test", Assert.Single(app.Plugins.MenuItems(Editor())).Header);
    }

    private static string SampleDll()
    {
        var bin = Path.Combine(AppRun.RepoRoot, "samples", "menu-plugin", "bin");
        var dll = Directory.Exists(bin) ? Directory.GetFiles(bin, "SampleMenuPlugin.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        Assert.True(dll is not null, "Build the samples first (dotnet build avamux.slnx).");
        return dll!;
    }

    [Fact]
    public async Task A_plugin_is_installed_from_its_dll_switched_off_and_on_reloaded_and_uninstalled()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Plugins.Start();
        var added = Assert.Single(app.Plugins.Install(SampleDll()));
        Assert.Equal("sample-menu", added.Id);
        Assert.Equal(PluginSource.Folder, added.Source);
        var folder = Path.Combine(box.Paths.PluginsDir, "sample-menu");
        Assert.True(File.Exists(Path.Combine(folder, "SampleMenuPlugin.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "Avamux.Plugin.Abstractions.dll"))); // avamux has it
        Assert.True(File.Exists(Path.Combine(folder, "plugin.json")));
        Assert.Contains("sample-clock", app.Watches.Kinds);

        var editor = new FakeEditor("hello world");
        var menu = new MenuContext { Menu = Menus.WikiEditor, WikiPath = "a.md", SelectedText = "hello", Editor = editor };
        var sample = Assert.Single(app.Plugins.MenuItems(menu));
        await sample.Children.Single(c => c.Header == "UPPER CASE").OnClick!();
        Assert.Equal("HELLO", editor.Replaced);

        Assert.Throws<InvalidOperationException>(() => app.Plugins.Install(SampleDll())); // already there

        app.Plugins.SetEnabled("sample-menu", false);
        Assert.Empty(app.Plugins.MenuItems(menu));
        Assert.True(app.Plugins.Get("sample-menu").RestartToFinish);
        app.Plugins.SetEnabled("sample-menu", true);
        Assert.Single(app.Plugins.MenuItems(menu));
        app.Plugins.Reload("sample-menu");
        Assert.Single(app.Plugins.MenuItems(menu));

        // Found again on the next start, from the plugins folder.
        await using (var next = new AppServices(box.Paths))
        {
            next.Plugins.Start();
            Assert.Equal(PluginSource.Folder, next.Plugins.Get("sample-menu").Source);
            Assert.Single(next.Plugins.MenuItems(menu));
        }

        app.Plugins.Uninstall("sample-menu");
        Assert.False(Directory.Exists(folder));
        Assert.Null(app.Plugins.Find("sample-menu"));
        Assert.Empty(app.Plugins.MenuItems(menu));
        Assert.DoesNotContain("sample-clock", app.Watches.Kinds);
    }

    [Fact]
    public async Task A_dll_without_plugins_is_refused()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Plugins.Start();
        var abstractions = typeof(IAvamuxPlugin).Assembly.Location;
        var ex = Assert.Throws<InvalidOperationException>(() => app.Plugins.Install(abstractions));
        Assert.Contains("no class that implements", ex.Message);
        Assert.Empty(Directory.GetDirectories(box.Paths.PluginsDir));
    }

    [Fact]
    public async Task Page_plugins_are_listed_too_and_switch_with_their_page()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Plugins.Start();
        var page = app.Nav.Add(new Avamux.Core.Nav.CustomPage { Name = "Sample", Kind = "plugin", Assembly = "/nowhere/Sample.dll" });
        var entry = app.Plugins.Get("page:" + page.Id);
        Assert.Equal(PluginSource.Page, entry.Source);
        Assert.Equal("Sample", entry.Name);
        app.Plugins.SetEnabled(entry.Id, false);
        Assert.False(app.Nav.Get(page.Id).Enabled);
        Assert.False(app.Plugins.Get(entry.Id).Enabled);
        app.Plugins.Uninstall(entry.Id);
        Assert.Null(app.Nav.Find(page.Id));
        Assert.Null(app.Plugins.Find(entry.Id));
    }

    [Fact]
    public async Task Cli_lists_and_switches_plugins()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        app.Plugins.AddBuiltIn(() => new TestMenuPlugin());
        app.Plugins.Start();
        var list = await app.Commands.ExecuteAsync("plugin list", new());
        Assert.Contains("test-menu", list.ToJsonString());
        var off = await app.Commands.ExecuteAsync("plugin disable", new() { ["plugin"] = "test-menu" });
        Assert.True(off["ok"]!.GetValue<bool>(), off.ToJsonString());
        Assert.False(app.Plugins.Get("test-menu").Enabled);
    }

    private sealed class FakeEditor(string text) : IWikiEditor
    {
        public string? Replaced;
        public string Path => "a.md";
        public string Text => text;
        public string SelectedText => "hello";
        public bool IsReadOnly => false;
        public void ReplaceSelection(string t) => Replaced = t;
        public void ReplaceText(string t) => Replaced = t;
        public void InsertAfterSelection(string t) => Replaced = t;
        public void Save() { }
    }
}
