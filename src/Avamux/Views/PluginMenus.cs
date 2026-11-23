using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avamux.Core.Logging;
using Avamux.Core.Plugins;
using Avamux.Plugins;

namespace Avamux.Views;

/// <summary>
/// Puts the items plugins add (<see cref="PluginService.MenuItems"/>) into avamux's right-click menus: native
/// Avalonia menus (the wiki editor and folder tree), and the HTML menus inside web views (the wiki preview and
/// terminals), which ask for them as they open and say which one was picked.
/// </summary>
public static class PluginMenus
{
    private const string Marker = "plugin-item";

    /// <summary>
    /// Makes <paramref name="menu"/> end with the plugin items for the context <paramref name="context"/> gives each
    /// time it opens (a separator first when there are any).
    /// </summary>
    public static void Attach(ContextMenu menu, Func<MenuContext?> context)
    {
        var fresh = false;
        void Fill()
        {
            fresh = true;
            foreach (var old in menu.Items.OfType<Control>().Where(c => c.Tag as string == Marker).ToList()) menu.Items.Remove(old);
            if (context() is not { } ctx) return;
            var items = App.Services.Plugins.MenuItems(ctx);
            if (items.Count == 0) return;
            if (menu.Items.Count > 0) menu.Items.Add(new Separator { Tag = Marker });
            foreach (var item in items) menu.Items.Add(Build(item, marked: true));
        }
        menu.Opening += (_, _) => Fill();
        // Opened straight from code (menu.Open), Opening may not be raised: fill it then.
        menu.Opened += (_, _) => { if (!fresh) Fill(); };
        menu.Closed += (_, _) => fresh = false;
    }

    public static Control Build(PluginMenuItem item, bool marked = false)
    {
        if (item.IsSeparator) return new Separator { Tag = marked ? Marker : null };
        var m = new MenuItem { Header = item.Header, IsEnabled = item.IsEnabled, Tag = marked ? Marker : null };
        if (item.Icon is { } icon && Icons.Resolve(icon) is { } geometry) m.Icon = new PathIcon { Data = geometry, Width = 14, Height = 14 };
        if (item.ToolTip is { } tip) ToolTip.SetTip(m, tip);
        foreach (var child in item.Children) m.Items.Add(Build(child));
        if (item.OnClick is { } click && item.Children.Count == 0) m.Click += (_, _) => Run(item.Header, click);
        return m;
    }

    /// <summary>Runs a plugin's menu command, telling the user (rather than crashing) when it fails.</summary>
    public static void Run(string header, Func<Task> click) => Dispatcher.UIThread.Post(async () =>
    {
        try { await click(); }
        catch (Exception ex)
        {
            Log.Error("plugin", $"Menu item '{header}' failed", ex);
            Ui.Toast(null, $"{header} failed", ex.GetBaseException().Message);
        }
    });

    /// <summary>The items of a web view's menu as JSON ({k, h, e, c} or {s:1}), remembering each by key for <see cref="Pick"/>.</summary>
    public sealed class WebMenu
    {
        private readonly Dictionary<string, PluginMenuItem> _byKey = [];
        private int _next;

        public JsonArray Items(MenuContext context)
        {
            _byKey.Clear();
            return Json(App.Services.Plugins.MenuItems(context));
        }

        private JsonArray Json(IEnumerable<PluginMenuItem> items)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                if (item.IsSeparator) { array.Add(new JsonObject { ["s"] = 1 }); continue; }
                var key = (++_next).ToString();
                _byKey[key] = item;
                var o = new JsonObject { ["k"] = key, ["h"] = item.Header, ["e"] = item.IsEnabled };
                if (item.ToolTip is { } tip) o["t"] = tip;
                if (item.Children.Count > 0) o["c"] = Json(item.Children);
                array.Add(o);
            }
            return array;
        }

        /// <summary>Runs the item the web view said was picked.</summary>
        public void Pick(string? key)
        {
            if (key is not null && _byKey.TryGetValue(key, out var item) && item.OnClick is { } click && item.IsEnabled) Run(item.Header, click);
        }
    }
}

/// <summary>What plugins get of the window: the theme, toasts, questions and windows of their own.</summary>
public sealed class AppPluginUi : IPluginUi
{
    public AppPluginUi() => Ui.ThemeChanged += () => ThemeChanged?.Invoke();

    public event Action? ThemeChanged;

    public string ThemeColor(string token)
    {
        try { return Ui.Theme[token]; } catch (KeyNotFoundException) { return Ui.Theme["text"]; }
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public void Toast(string title, string message) => Dispatcher.UIThread.Post(() => Ui.Toast(null, title, message));

    private static Window Owner => Ui.Main ?? throw new InvalidOperationException("The window is not open yet.");

    public Task<string?> PromptAsync(string title, string label, string initial) => Ui.PromptAsync(Owner, title, label, initial);
    public Task<bool> ConfirmAsync(string title, string message, string action) => Ui.ConfirmAsync(Owner, title, message, action);

    public IPluginWindow ShowWindow(string title, object content, double width, double height)
    {
        var control = content as Control ?? throw new ArgumentException($"A window's content has to be an Avalonia Control, not {content?.GetType().FullName ?? "null"}.");
        var window = new PluginWindow(title, control, width, height);
        if (Ui.Main is { } main) window.Show(main); else window.Show();
        return window;
    }

    private sealed class PluginWindow : AppWindow, IPluginWindow
    {
        public PluginWindow(string title, Control content, double width, double height)
        {
            Icon = AppIcon.Window;
            Title = title;
            Width = width; Height = height; MinWidth = 320; MinHeight = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Body = content;
            Closed += (_, _) => WindowClosed?.Invoke();
        }

        private event Action? WindowClosed;
        event Action? IPluginWindow.Closed { add => WindowClosed += value; remove => WindowClosed -= value; }
        void IPluginWindow.Close() => Dispatcher.UIThread.Post(Close);
    }
}
