using System.Globalization;
using Avamux.Plugins;

namespace SampleMenuPlugin;

/// <summary>
/// A plugin without a page. It adds a "Sample" submenu to the wiki editor's right-click menu (change the case of the
/// selection, insert the date, count words), a "Count words" item to terminals' menus, and a watch kind that reports
/// the time. Everything it adds goes away when it is switched off under Settings → Plugins.
/// </summary>
public sealed class SampleMenuPlugin : IAvamuxPlugin, IMenuContributor
{
    private IPluginContext? _context;

    public string Id => "sample-menu";
    public string Name => "Sample menu items";
    public string Description => "Adds a Sample submenu to the wiki editor's right-click menu and a word count to terminals' menus.";

    public void Activate(IPluginContext context)
    {
        _context = context;
        context.AddMenuItems(this);
        context.AddWatchProvider(new ClockWatch());
        context.Log("switched on");
    }

    public void Deactivate() => _context = null;

    public IEnumerable<PluginMenuItem> Items(MenuContext menu)
    {
        if (_context is not { } ctx) yield break;
        if (menu.Menu == Menus.WikiEditor && menu.Editor is { IsReadOnly: false } editor)
        {
            var selected = menu.SelectedText.Length > 0;
            yield return PluginMenuItem.Submenu("Sample", [
                new PluginMenuItem { Header = "UPPER CASE", IsEnabled = selected, OnClick = () => { editor.ReplaceSelection(editor.SelectedText.ToUpperInvariant()); return Task.CompletedTask; } },
                new PluginMenuItem { Header = "lower case", IsEnabled = selected, OnClick = () => { editor.ReplaceSelection(editor.SelectedText.ToLowerInvariant()); return Task.CompletedTask; } },
                PluginMenuItem.Separator(),
                PluginMenuItem.Command("Insert Today's Date", () => editor.ReplaceSelection(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
                PluginMenuItem.Command("Count Words", () => ctx.Toast("Words on this page", Words(editor.Text).ToString("N0"))),
            ], icon: "puzzle");
        }
        if (menu.Menu == Menus.Terminal && menu.SelectedText.Length > 0)
            yield return PluginMenuItem.Command("Count Words in Selection", () => ctx.Toast("Words selected", Words(menu.SelectedText).ToString("N0")));
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed class ClockWatch : IWatchProvider
    {
        public string Kind => "sample-clock";
        public string Description => "Reports the time (from the sample menu plugin)";
        public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>();
        public Task<WatchResult> CheckAsync(WatchContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new WatchResult { Status = "ok", Summary = "It is " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) });
    }
}
