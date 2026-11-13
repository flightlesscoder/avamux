using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avamux.Core.Nav;

namespace Avamux.Views;

/// <summary>The part of Settings that lists the user's own navigation pages and lets them be added, changed and switched.</summary>
public sealed class CustomPagesCard : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 4 };
    private static NavService Nav => App.Services.Nav;

    /// <summary>Words that bring this card up in the settings search, besides the pages' own names.</summary>
    private const string Words = "custom pages navigation nav left sidebar item add terminal command web page url website browser plugin dll icon";

    public CustomPagesCard()
    {
        Content = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Text("Custom pages", "h2"),
                Ui.Text("Pages of your own in the navigation: a terminal that runs one command (btm, lazygit, lazydocker, an ssh line …), a web page, " +
                        "or a page built by a .NET plugin. Adding, changing and switching them takes effect at once. " +
                        $"They are kept in {App.Services.Paths.StateFile("nav")}, and `avamux page /?` manages them from the command line.", "dim", 12.5),
                _list,
                Ui.Apply(Ui.IconButton(Icons.Plus, "Add a page to the navigation", () => _ = EditAsync(null), "Add a page"), b => b.HorizontalAlignment = HorizontalAlignment.Left),
            },
        });
        var refresh = Ui.Debounced(Build);
        Nav.Changed += refresh;
        Build();
    }

    public bool Matches(string query) =>
        Words.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => Words.Contains(w, StringComparison.OrdinalIgnoreCase))
        || Nav.Pages.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Target.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string KindLabel(string kind) => kind switch { CustomPage.Web => "web", CustomPage.Plugin => "plugin", _ => "terminal" };

    private void Build()
    {
        _list.Children.Clear();
        var pages = Nav.Pages;
        if (pages.Count == 0) { _list.Children.Add(Ui.Text("No custom pages yet.", "dim", 12.5)); return; }
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var on = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, IsChecked = page.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            ToolTip.SetTip(on, "Show this page in the navigation. Switching it off ends what it runs.");
            on.IsCheckedChanged += (_, _) => Ui.Try(this, () => Nav.SetEnabled(page.Id, on.IsChecked == true));
            var chipBrush = Ui.Tk(page.Kind switch { CustomPage.Web => "accent", CustomPage.Plugin => "purple", _ => "ok" });
            var text = new StackPanel
            {
                Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Opacity = page.Enabled ? 1 : 0.55,
                Children =
                {
                    Ui.Row(8, new TextBlock { Text = page.Name, FontWeight = FontWeight.SemiBold }, Ui.Chip(KindLabel(page.Kind), chipBrush)),
                    Ui.Apply(new TextBlock { Text = page.Target, Foreground = Ui.Dim, FontSize = 11.5, FontFamily = Ui.Mono, TextTrimming = TextTrimming.CharacterEllipsis }, t => ToolTip.SetTip(t, page.Target)),
                },
            };
            var buttons = Ui.Row(0,
                on,
                Ui.Apply(Ui.IconButton(Icons.ArrowUp, "Move up", () => Ui.Try(this, () => Nav.Move(page.Id, -1))), b => { if (i == 0) { b.IsHitTestVisible = false; b.Opacity = 0.3; } }),
                Ui.Apply(Ui.IconButton(Icons.ArrowDown, "Move down", () => Ui.Try(this, () => Nav.Move(page.Id, 1))), b => { if (i == pages.Count - 1) { b.IsHitTestVisible = false; b.Opacity = 0.3; } }),
                Ui.IconButton(Icons.Refresh, page.Kind switch { CustomPage.Web => "Load the page again", CustomPage.Plugin => "Load the DLL again (after a rebuild, say)", _ => "Restart the command" },
                    () => Ui.Try(this, () => Nav.Reload(page.Id))),
                Ui.IconButton(Icons.OpenInNew, "Go to this page", () => { if (page.Enabled) App.Services.Host.Navigate(page.Key); else Ui.Toast(this, $"{page.Name} is switched off"); }),
                Ui.IconButton(Icons.Pencil, "Edit", () => _ = EditAsync(page)),
                Ui.IconButton(Icons.Delete, "Delete", () => _ = DeleteAsync(page)));
            _list.Children.Add(new Border
            {
                Background = Ui.Tk("bgDeep"), CornerRadius = Ui.R(6), Padding = new Thickness(10, 6),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("34,*,Auto"),
                    Children =
                    {
                        new PathIcon { Data = CustomPages.IconFor(page), Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, Opacity = page.Enabled ? 1 : 0.55 },
                        text.At(0, 1), buttons.At(0, 2),
                    },
                },
            });
        }
    }

    private async Task DeleteAsync(CustomPage page)
    {
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Delete page", $"Delete \"{page.Name}\" from the navigation? What it is running will be ended.")) return;
        Ui.Try(this, () => Nav.Remove(page.Id));
    }

    private async Task EditAsync(CustomPage? existing)
    {
        var page = existing?.Clone() ?? new CustomPage();
        var name = new TextBox { Text = page.Name, PlaceholderText = "Shown under the icon" };
        var kind = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Items = { "Terminal: one command in a terminal of its own", "Web page", "Plugin: a page built by a .NET DLL" } };
        kind.SelectedIndex = Array.IndexOf(CustomPage.Kinds, page.Kind) is var k and >= 0 ? k : 0;

        // Icon: a name from the list below, or path data of the user's own.
        var icon = new TextBox { Text = page.Icon, PlaceholderText = "Pick one below, or paste SVG path data (24x24). Empty: one for the kind." };
        var preview = new PathIcon { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var gallery = new WrapPanel();
        void ShowIcon() => preview.Data = Icons.Resolve(icon.Text) ?? CustomPages.DefaultIcon(CustomPage.Kinds[Math.Max(0, kind.SelectedIndex)]);
        foreach (var (iconName, geometry) in Icons.Named.OrderBy(n => n.Key))
        {
            var pick = new Button { Classes = { "flat" }, Padding = new Thickness(5), Content = new PathIcon { Data = geometry, Width = 17, Height = 17 } };
            ToolTip.SetTip(pick, iconName);
            pick.Click += (_, _) => icon.Text = iconName;
            gallery.Children.Add(pick);
        }
        icon.TextChanged += (_, _) => ShowIcon();

        var command = new TextBox { Text = page.Command, PlaceholderText = "btm     lazygit     ssh -p 2222 -L 8080:localhost:80 me@host", Classes = { "mono" } };
        var cwd = new TextBox { Text = page.Cwd, PlaceholderText = "Home folder" };
        var terminal = Ui.Column(12,
            Ui.Field("Command", command, "Run through your login shell, so it can be any command line. It gets the whole page; when it ends the page offers to start it again. Empty starts the shell itself."),
            Ui.Field("Start in", cwd));

        var url = new TextBox { Text = page.Url, PlaceholderText = "https://…" };
        var api = new CheckBox { Content = "Give the page the limited avamux object", IsChecked = page.InjectApi };
        var links = new CheckBox { Content = "Open links that ask for a new window in the default browser", IsChecked = page.LinksInBrowser };
        var script = new TextBox
        {
            Text = page.Script, AcceptsReturn = true, Height = 96, Classes = { "mono" }, TextWrapping = TextWrapping.NoWrap,
            PlaceholderText = "// e.g. show the unread count from the page title\nsetInterval(() => avamux.setBadge((document.title.match(/\\((\\d+)\\)/) || [])[1]), 3000);",
        };
        var web = Ui.Column(12,
            Ui.Field("Address", url),
            Ui.Column(4, api, Ui.Text("The page (and the script below) can call avamux.needsInput(message), avamux.clearNeedsInput(), avamux.notify(title, message) and " +
                                      "avamux.setBadge(text). That is all: it cannot run avamux commands or read anything from avamux.", "dim", 11.5)),
            links,
            Ui.Field("Script", script, "JavaScript run in the page each time it has loaded. The developer tools are on the page's toolbar (and its right-click menu)."));

        var assembly = new TextBox { Text = page.Assembly, PlaceholderText = "/path/to/Plugin.dll" };
        var browse = Ui.TextButton("Browse…", () => { });
        browse.Click += async (_, _) =>
        {
            var picked = await Ui.Owner(browse).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a plugin DLL", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(".NET assembly") { Patterns = ["*.dll"] }],
            });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) assembly.Text = path;
        };
        var type = new TextBox { Text = page.Type, PlaceholderText = "First page in the DLL" };
        var plugin = Ui.Column(12,
            Ui.Field("DLL", new DockPanel { Children = { Ui.Apply(browse, b => b.Margin = new Thickness(8, 0, 0, 0)).Dock(Dock.Right), assembly } },
                $"A class library with a class that implements Avamux.Plugins.IPagePlugin (see samples/page-plugin). A relative path is looked up in {App.Services.Paths.PluginsDir}."),
            Ui.Field("Class", type, "Only needed when the DLL has more than one page."),
            new Border
            {
                Background = Ui.Tk("bgDeep"), BorderBrush = Ui.Tk("warn"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 7),
                Child = Ui.Text("A plugin runs inside avamux and can do anything avamux can: run commands, read your terminals, change settings. Only add DLLs you trust.", null, 12),
            });

        void ShowKind()
        {
            terminal.IsVisible = kind.SelectedIndex == 0; web.IsVisible = kind.SelectedIndex == 1; plugin.IsVisible = kind.SelectedIndex == 2;
            ShowIcon();
        }
        kind.SelectionChanged += (_, _) => ShowKind();
        ShowKind();

        var body = Ui.Column(14,
            Ui.Field("Name", name),
            Ui.Field("Kind", kind),
            terminal, web, plugin,
            Ui.Field("Icon", Ui.Column(6, new DockPanel { Children = { preview.Dock(Dock.Left), icon } }, gallery)));

        await Ui.FormAsync(Ui.Owner(this), existing is null ? "Add a page" : $"Edit {existing.Name}", body, () =>
        {
            page.Name = name.Text ?? "";
            page.Kind = CustomPage.Kinds[Math.Max(0, kind.SelectedIndex)];
            page.Icon = icon.Text ?? "";
            page.Command = command.Text ?? ""; page.Cwd = cwd.Text ?? "";
            page.Url = url.Text ?? ""; page.InjectApi = api.IsChecked == true; page.LinksInBrowser = links.IsChecked == true; page.Script = script.Text ?? "";
            page.Assembly = assembly.Text ?? ""; page.Type = type.Text ?? "";
            if (page.Icon.Trim().Length > 0 && Icons.Resolve(page.Icon) is null) return Task.FromResult<string?>("The icon is neither one of the names below nor SVG path data.");
            if (existing is null) Nav.Add(page); else Nav.Update(page);
            return Task.FromResult<string?>(null);
        }, existing is null ? "Add" : "Save", width: 620);
    }
}
