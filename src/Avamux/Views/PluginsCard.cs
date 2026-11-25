using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avamux.Core.Logging;
using Avamux.Core.Plugins;

namespace Avamux.Views;

/// <summary>
/// Settings → Plugins: every plugin (built in, in the plugins folder, page plugins), what it adds, and switching it
/// on and off, reloading, installing and uninstalling it. A plugin's own settings panel shows under it.
/// </summary>
public sealed class PluginsCard : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 4 };
    private static PluginService Plugins => App.Services.Plugins;

    private const string Words = "plugins plugin dll extension add-on addon install uninstall enable disable reload ai actions menu items pageless";

    public PluginsCard()
    {
        Content = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Text("Plugins", "h2"),
                Ui.Text("What .NET plugins add to avamux: right-click menu items, watch kinds, AI providers, pages. Switching one off takes what it added away at once. " +
                        $"Plugins live in folders of their own under {App.Services.Paths.PluginsDir}; `avamux plugin /?` manages them from the command line. " +
                        "A plugin runs with everything avamux can do, so only install ones you trust.", "dim", 12.5),
                _list,
                Ui.Row(8,
                    Ui.IconButton(Icons.Plus, "Copy a plugin (its DLL, or a folder holding it) into the plugins folder and switch it on", () => _ = InstallAsync(), "Install…"),
                    Ui.IconButton(Icons.FolderOpen, "Open the plugins folder", () => Avamux.Core.Shell.Open(App.Services.Paths.PluginsDir), "Plugins Folder")),
            },
        });
        var refresh = Ui.Debounced(Build);
        Plugins.Changed += refresh;
        DetachedFromVisualTree += (_, _) => Plugins.Changed -= refresh;
        Build();
    }

    public bool Matches(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => Words.Contains(w, StringComparison.OrdinalIgnoreCase))
        || Plugins.Entries.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void Build()
    {
        _list.Children.Clear();
        var entries = Plugins.Entries;
        if (entries.Count == 0) { _list.Children.Add(Ui.Text("No plugins yet.", "dim", 12.5)); return; }
        foreach (var e in entries.OrderBy(e => e.Source).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)) _list.Children.Add(Row(e));
    }

    private Control Row(PluginEntry e)
    {
        var on = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, IsChecked = e.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        ToolTip.SetTip(on, e.Enabled ? "Switch off: what it adds goes at once" : "Switch on");
        on.IsCheckedChanged += (_, _) => Ui.Try(this, () => Plugins.SetEnabled(e.Id, on.IsChecked == true), $"Could not switch {e.Name} {(on.IsChecked == true ? "on" : "off")}");
        var source = e.Source switch { PluginSource.BuiltIn => ("built in", "accent"), PluginSource.Page => ("page", "purple"), _ => ("plugins folder", "ok") };
        var title = Ui.Row(8, new TextBlock { Text = e.Name, FontWeight = FontWeight.SemiBold }, Ui.Chip(source.Item1, Ui.Tk(source.Item2)));
        if (e.Version.Length > 0) title.Children.Add(new TextBlock { Text = "v" + e.Version, Foreground = Ui.Dim, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Opacity = e.Enabled ? 1 : 0.6, Children = { title } };
        if (e.Description.Length > 0) text.Children.Add(new TextBlock { Text = e.Description, Foreground = Ui.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var adds = e.Adds.Count > 0 ? "Adds " + string.Join("; ", e.Adds) + "." : e.Enabled ? "" : "Switched off.";
        if (e.Location.Length > 0) adds += (adds.Length > 0 ? "  " : "") + e.Location;
        if (adds.Length > 0) text.Children.Add(new TextBlock { Text = adds, Foreground = Ui.Tk("faint"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        if (e.Error is { } error) text.Children.Add(new TextBlock { Text = "Problem: " + error, Foreground = Ui.Tk("error"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        if (e.RestartToFinish) text.Children.Add(new TextBlock { Text = "Its code stays loaded until avamux restarts (.NET cannot unload it).", Foreground = Ui.Tk("warn"), FontSize = 11, TextWrapping = TextWrapping.Wrap });

        var buttons = Ui.Row(0, on);
        if (e.CanReload) buttons.Children.Add(Ui.IconButton(Icons.Refresh, "Load it again from disk (after rebuilding it)", () => Ui.Try(this, () => Plugins.Reload(e.Id), $"Could not reload {e.Name}")));
        if (e.Location.Length > 0 && e.Source != PluginSource.Page)
            buttons.Children.Add(Ui.IconButton(Icons.FolderOpen, "Show it in the file manager", () => Avamux.Core.Shell.ShowInFolder(e.Location)));
        if (e.CanUninstall) buttons.Children.Add(Ui.IconButton(Icons.Delete, e.Source == PluginSource.Page ? "Remove the page" : "Uninstall: switch it off and delete it", () => _ = UninstallAsync(e)));

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("34,*,Auto"),
            Children =
            {
                new PathIcon { Data = e.Source == PluginSource.BuiltIn ? Icons.Sparkles : Icons.Puzzle, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Opacity = e.Enabled ? 1 : 0.55 },
                text.At(0, 1), buttons.At(0, 2),
            },
        });
        if (e.Enabled)
            foreach (var make in e.SettingsViews)
            {
                try
                {
                    if (make() is Control view) body.Children.Add(new Border { Margin = new Thickness(34, 2, 0, 4), Child = view });
                }
                catch (Exception ex) { Log.Warn("plugin", $"{e.Name}'s settings could not be shown: {ex.Message}"); body.Children.Add(Ui.Text($"Its settings could not be shown: {ex.Message}", "dim", 11.5)); }
            }
        return new Border { Background = Ui.Tk("bgDeep"), CornerRadius = Ui.R(6), Padding = new Thickness(10, 8), Child = body, Tag = e.Id };
    }

    private async Task UninstallAsync(PluginEntry e)
    {
        var what = e.Source == PluginSource.Page ? $"Remove the page \"{e.Name}\" from the navigation?" : $"Switch {e.Name} off and delete {e.Location}?";
        if (!await Ui.ConfirmAsync(Ui.Owner(this), e.Source == PluginSource.Page ? "Remove page" : "Uninstall plugin", what, e.Source == PluginSource.Page ? "Remove" : "Uninstall")) return;
        Ui.Try(this, () => Plugins.Uninstall(e.Id), $"Could not uninstall {e.Name}");
    }

    private async Task InstallAsync()
    {
        var files = await Ui.Owner(this).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install a plugin: choose its DLL", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Plugin DLL") { Patterns = ["*.dll"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var added = Plugins.Install(path);
            Ui.Toast(this, "Plugin installed", string.Join(", ", added.Select(a => a.Name)));
        }
        catch (Exception ex) { Ui.Toast(this, "Could not install the plugin", ex.GetBaseException().Message); }
    }
}
