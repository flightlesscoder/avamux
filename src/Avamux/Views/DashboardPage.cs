using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avamux.Core;
using Avamux.Core.Dashboard;
using Avamux.Core.Logging;

namespace Avamux.Views;

/// <summary>The user's own HTML page from the dashboard folder, with a panel to toggle extra CSS/JS sources.</summary>
public sealed class DashboardPage : UserControl, IPage
{
    private readonly NativeWebView _web = new();
    private readonly StackPanel _sources = new() { Spacing = 8 };
    private readonly Border _panel;
    private readonly TextBlock _folder = new() { Foreground = Ui.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };

    private static DashboardService Dashboard => App.Services.Dashboard;

    public DashboardPage()
    {
        var url = new TextBox { PlaceholderText = "https://…/file.css, /absolute/file.js, or a path in the dashboard folder" };
        var type = new ComboBox { Items = { "auto", "css", "js" }, SelectedIndex = 0, MinWidth = 86 };
        void Add()
        {
            if (string.IsNullOrWhiteSpace(url.Text)) return;
            Ui.Try(this, () =>
            {
                Dashboard.AddSource(url.Text!.Trim(), type.SelectedIndex == 0 ? null : (string)type.SelectedItem!);
                url.Text = "";
            }, "Could not add the source");
        }
        url.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Add(); } };
        var control = new ToggleSwitch { OnContent = "Page scripts may control avamux", OffContent = "Page scripts may not control avamux", IsChecked = App.Services.Config.Settings.Dashboard.AllowAppControl };
        control.IsCheckedChanged += (_, _) =>
        {
            if (control.IsChecked == App.Services.Config.Settings.Dashboard.AllowAppControl) return;
            Ui.Try(this, () => App.Services.Config.Set("dashboard.allowAppControl", control.IsChecked == true));
        };
        App.Services.Config.Changed += Ui.Debounced(() => control.IsChecked = App.Services.Config.Settings.Dashboard.AllowAppControl);

        _panel = new Border
        {
            Width = 380, IsVisible = false, BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, 0, 0), Background = Ui.Brush("#171a23"),
            Child = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(16), Spacing = 12,
                    Children =
                    {
                        Ui.Text("Extra CSS and JavaScript", "h2"),
                        Ui.Text("Sources that are switched on are added to the page's <head> after its own files. Toggle them without editing the page.", "dim", 12),
                        _sources,
                        Ui.Field("Add a source", url),
                        new DockPanel { Children = { Ui.TextButton("Add", Add).Dock(Dock.Right), Ui.Row(8, Ui.Apply(Ui.Text("Type"), t => t.VerticalAlignment = VerticalAlignment.Center), type) } },
                        new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 6) },
                        Ui.Text("JavaScript API", "h2"),
                        Ui.Text("The page gets window.avamux: avamux.command(name, args) runs any command the CLI can, and avamux.commands() lists them.", "dim", 12),
                        control,
                        new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 6) },
                        Ui.Text("Folder", "h2"),
                        _folder,
                    },
                },
            },
        };

        var bar = new Border
        {
            BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 5),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Row(2,
                        Ui.IconButton(Icons.Refresh, "Reload the page", Reload, "Reload"),
                        Ui.IconButton(Icons.Bug, "Open the web developer tools", OpenDevTools, "Developer tools"),
                        Ui.IconButton(Icons.OpenInNew, "Open the dashboard in the default browser", () => Shell.Open(App.Services.Web.DashboardUrl), "Browser"),
                        Ui.IconButton(Icons.Pencil, "Open the dashboard folder in the editor (repositories.editor) in a terminal tab", () => Ui.Try(this, () => Ui.OpenInEditor(Dashboard.Root), "Could not start the editor"), "Edit"),
                        Ui.IconButton(Icons.FolderOpen, "Open the dashboard folder", () => Shell.Open(Dashboard.Root), "Folder"),
                        Ui.IconButton(Icons.Cog, "Extra CSS/JS sources and the JavaScript API", () => _panel!.IsVisible = !_panel.IsVisible, "Sources")).Dock(Dock.Right),
                    Ui.Apply(Ui.Text("Dashboard", "h2"), t => t.VerticalAlignment = VerticalAlignment.Center),
                },
            },
        };
        Content = new DockPanel { Children = { bar.Dock(Dock.Top), _panel.Dock(Dock.Right), _web } };

        WebViews.FocusOnClick(_web);

        _web.EnvironmentRequested += (_, e) => WebViews.Configure(e);
        _web.NewWindowRequested += (_, e) =>
        {
            // target=_blank links and window.open go to the real browser.
            e.Handled = true;
            if (e.Request is { } uri && uri.Scheme is "http" or "https") Shell.Open(uri.ToString());
        };
        _web.Source = new Uri(App.Services.Web.DashboardUrl);
        Dashboard.Changed += Ui.Debounced(() => { BuildSources(); Reload(); });
        BuildSources();
    }

    public void OnShown() { }

    public void Reload()
    {
        try { _web.Navigate(new Uri(App.Services.Web.DashboardUrl)); }
        catch (Exception ex) { Log.Warn("dashboard", "Reload failed: " + ex.Message); }
    }

    public void OpenDevTools()
    {
        if (!App.Services.Config.Settings.Dashboard.DeveloperTools) { Ui.Toast(this, "Developer tools are switched off", "Turn on dashboard.developerTools in Settings and restart avamux."); return; }
        if (!WebViews.OpenDevTools(_web)) Ui.Toast(this, "Developer tools", "Right-click the page and choose \"Inspect Element\".");
    }

    private void BuildSources()
    {
        _folder.Text = Dashboard.Root;
        _sources.Children.Clear();
        var list = Dashboard.Sources;
        if (list.Count == 0) _sources.Children.Add(Ui.Text("No extra sources yet.", "dim", 12.5));
        foreach (var s in list)
        {
            var on = new CheckBox { IsChecked = s.Enabled, MinHeight = 0, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
            on.IsCheckedChanged += (_, _) => Ui.Try(this, () => Dashboard.SetEnabled(s.Id, on.IsChecked == true));
            ToolTip.SetTip(on, "Pull this source into the page");
            _sources.Children.Add(new DockPanel
            {
                Children =
                {
                    on.Dock(Dock.Left),
                    Ui.IconButton(Icons.Delete, "Remove this source", () => Ui.Try(this, () => Dashboard.RemoveSource(s.Id))).Dock(Dock.Right),
                    new Border
                    {
                        Background = Ui.Brush(s.Type == "js" ? "#e5c07b" : "#61afef"), CornerRadius = Ui.R(3), Padding = new Thickness(5, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = s.Type, FontSize = 10.5, Foreground = Ui.OnAccent },
                    }.Dock(Dock.Left),
                    Ui.Apply(new TextBlock { Text = s.Url, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = s.Enabled ? 1 : 0.55 }, t => ToolTip.SetTip(t, s.Url)),
                },
            });
        }
    }
}
