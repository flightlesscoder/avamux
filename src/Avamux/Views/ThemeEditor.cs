using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using Avamux.Core;
using Avamux.Core.Config;
using Avamux.Core.Theming;

namespace Avamux.Views;

/// <summary>
/// Edits a theme while showing it: every change is applied to the whole app straight away (nothing is saved
/// until Save), and closing the editor puts the chosen theme back. Colours can be edited one by one, rebuilt from
/// three base colours, or written as the theme file itself (YAML or JSON).
/// </summary>
public sealed class ThemeEditorWindow : AppWindow
{
    private static ThemeEditorWindow? _open;

    private ThemeDef _original = Themes.Simple, _draft = Themes.Simple;
    private string _format = "yaml";
    private string _sourceOfOriginal = "";
    private bool _loading, _settingSource, _closeConfirmed, _sourceBroken;

    private readonly ComboBox _picker = new() { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _where = new() { Foreground = Ui.Dim, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _dirty = new() { Text = "● unsaved changes", Foreground = Ui.Tk("warn"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly TextBox _name = new(), _description = new(), _font = new() { PlaceholderText = "The default interface font" };
    private readonly ToggleSwitch _light = new() { OnContent = "Light", OffContent = "Dark", MinWidth = 0 };
    private readonly ToggleSwitch _bevel = new() { OnContent = "On", OffContent = "Off", MinWidth = 0 };
    private readonly Slider _radius = new() { Minimum = 0, Maximum = 16, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200 };
    private readonly TextBlock _radiusText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 40 };
    private readonly ComboBox _chrome = new() { ItemsSource = new[] { "system", "custom" }, MinWidth = 130 };
    private readonly ComboBox _sounds = new() { ItemsSource = SoundService.Sets, MinWidth = 130 };
    private readonly Dictionary<string, (Control Row, TextBox Hex)> _rows = new();
    private readonly List<(Border Swatch, Func<string> Get)> _swatches = [];
    private readonly List<(Run Run, int Ansi)> _ansiRuns = [];
    private readonly TextEditor _source;
    private readonly SyntaxColorizer _sourceColors = new() { Language = "yaml" };
    private readonly TextBlock _sourceError = new() { Foreground = Ui.Tk("error"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(12, 6), IsVisible = false };
    private readonly StackPanel _readability = new() { Spacing = 3 };
    private readonly ScrollViewer _colorsScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button _save, _delete, _use, _yaml, _json;
    private readonly Button[] _tabs;
    private readonly Control _colorsView, _sourceView;
    private readonly DispatcherTimer _previewSoon = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _parseSoon = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string _deriveBg = "#171a23", _deriveFg = "#d8dee9", _deriveAccent = "#61afef";

    private static ThemeStore Store => App.Services.ThemeFiles;

    /// <summary>Opens the editor (one per app) on a theme, or on the current theme.</summary>
    public static void Open(Control? near, string? theme = null)
    {
        var first = _open is null;
        var window = _open ??= new ThemeEditorWindow();
        if (first && near is not null && TopLevel.GetTopLevel(near) is Window owner)
        {
            window.Width = Math.Clamp(owner.Width * 0.92, 980, 1500);
            window.Height = Math.Clamp(owner.Height * 0.92, 640, 1000);
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        if (first) window.Load(Themes.Find(theme ?? App.Services.Config.Settings.Theme.Name));
        else if (theme is not null) _ = window.SwitchToAsync(theme);
    }

    private ThemeEditorWindow()
    {
        Icon = AppIcon.Window;
        Title = "Theme Editor — avamux";
        Width = 1280; Height = 820; MinWidth = 900; MinHeight = 560;

        _source = new TextEditor
        {
            FontFamily = Ui.Mono, FontSize = 13, BorderThickness = new Thickness(0), Padding = new Thickness(8, 8, 12, 8),
            Background = Ui.Tk("bgEditor"), Foreground = Ui.Tk("textCode"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, ShowLineNumbers = true,
        };
        _source.Options.EnableHyperlinks = false;
        _source.Options.EnableEmailHyperlinks = false;
        _source.Options.ConvertTabsToSpaces = true;
        _source.Options.IndentationSize = 2;
        _source.TextArea.TextView.LineTransformers.Add(_sourceColors);
        _source.TextChanged += (_, _) =>
        {
            _sourceColors.Invalidate();
            if (_settingSource) return;
            _parsePending = true;
            _parseSoon.Stop(); _parseSoon.Start();
        };
        _parseSoon.Tick += (_, _) => { _parseSoon.Stop(); ParseSource(); };
        _previewSoon.Tick += (_, _) => { _previewSoon.Stop(); ShowPreview(); };

        _save = Ui.IconButton(Icons.ContentSave, "Save the theme file (Ctrl+S)", () => _ = SaveAsync(), "Save");
        _delete = Ui.IconButton(Icons.Delete, "Delete this theme file", () => _ = DeleteAsync(), "Delete");
        _use = Ui.TextButton("Use This Theme", () => _ = UseAsync(), "Make this the theme avamux uses");
        _use.Classes.Add("accent");
        _yaml = FormatButton("YAML", "yaml");
        _json = FormatButton("JSON", "json");

        _picker.SelectionChanged += (_, _) =>
        {
            if (_loading || _picker.SelectedItem is not ComboBoxItem { Tag: string id } || id == _draft.Id) return;
            _ = SwitchToAsync(id);
        };
        Themes.ListChanged += OnThemeListChanged;

        var toolbar = new DockPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children =
            {
                Ui.Row(6, _use).Dock(Dock.Right),
                Ui.Row(4,
                    new TextBlock { Text = "Theme", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 4, 0) }, _picker,
                    Ui.IconButton(Icons.Plus, "Make a new theme file from what is shown", () => _ = NewCopyAsync(), "New Copy…"),
                    _save,
                    Ui.IconButton(Icons.Undo, "Throw away the changes since the last save", () => _ = RevertAsync(), "Revert"),
                    _delete,
                    Ui.IconButton(Icons.FolderOpen, "Import a theme file", () => _ = ImportAsync(), "Import…"),
                    Ui.IconButton(Icons.OpenInNew, "Save a copy of the theme file somewhere else", () => _ = ExportAsync(), "Export…"),
                    Ui.IconButton(Icons.Folder, "Open the themes folder", () => Shell.Open(Store.Dir))),
            },
        };
        var status = new DockPanel { Margin = new Thickness(18, 0, 18, 8), Children = { _dirty.Dock(Dock.Right), _where } };

        // Left: the colours, or the theme file itself.
        _colorsView = BuildColors();
        _sourceView = new DockPanel
        {
            IsVisible = false,
            Children =
            {
                new Border
                {
                    Background = Ui.Tk("panel"), BorderBrush = Ui.Tk("line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            Ui.Row(4, _yaml, _json).Dock(Dock.Right),
                            Ui.Apply(Ui.Text("Edits apply as you type. Colours must be in quotes in YAML.", "dim"), t => { t.FontSize = 12; t.VerticalAlignment = VerticalAlignment.Center; }),
                        },
                    },
                }.Dock(Dock.Bottom),
                _sourceError.Dock(Dock.Bottom),
                _source,
            },
        };
        _tabs = [Tab("Colours", 0), Tab("Theme File", 1)];
        var left = new DockPanel
        {
            Children =
            {
                new Border { BorderBrush = Ui.Tk("line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Row(0, _tabs) }.Dock(Dock.Top),
                new Panel { Children = { _colorsView, _sourceView } },
            },
        };

        var right = new ScrollViewer { Content = BuildPreview(), Background = Ui.Tk("bgDeep") };
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("470,4,*") };
        split.Children.Add(new Border { BorderBrush = Ui.Tk("line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = left }.At(0, 0));
        split.Children.Add(new GridSplitter { Background = Ui.Tk("line"), ResizeDirection = GridResizeDirection.Columns }.At(0, 1));
        split.Children.Add(new Border { BorderBrush = Ui.Tk("line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = right }.At(0, 2));

        Body = new DockPanel { Children = { toolbar.Dock(Dock.Top), status.Dock(Dock.Top), split } };

        // Tunnelling, so the text editor does not get to swallow it first.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; _parseSoon.Stop(); ParseSourceIfPending(); _ = SaveAsync(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += async (_, e) =>
        {
            if (_closeConfirmed || !IsDirty || e.IsProgrammatic && e.CloseReason == WindowCloseReason.ApplicationShutdown) return;
            e.Cancel = true;
            if (!await Ui.ConfirmAsync(this, "Unsaved theme", $"Discard the changes to {_draft.Name}?", "Discard")) return;
            _closeConfirmed = true;
            Close();
        };
        Closed += (_, _) =>
        {
            Themes.ListChanged -= OnThemeListChanged;
            if (ReferenceEquals(_open, this)) _open = null;
            if (Application.Current is { } app) ThemeManager.Preview(app, null);
        };
        ShowTab(0);
    }

    // ------------------------------------------------------------------ building

    private Button Tab(string text, int index)
    {
        var b = new Button { Content = text, Classes = { "tab" } };
        b.Click += (_, _) => ShowTab(index);
        return b;
    }

    private void ShowTab(int index)
    {
        _colorsView.IsVisible = index == 0;
        _sourceView.IsVisible = index == 1;
        for (var i = 0; i < _tabs.Length; i++) _tabs[i].Classes.Set("on", i == index);
    }

    private Button FormatButton(string text, string format)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3), FontSize = 12 };
        ToolTip.SetTip(b, $"Show and save the theme as {text}");
        b.Click += (_, _) =>
        {
            if (_format == format) return;
            if (_sourceBroken) { Ui.Toast(this, "Fix the theme file first", "It has problems, so it cannot be converted."); return; }
            _format = format;
            WriteSource(ThemeFile.Write(_draft, _format));
            UpdateFormatButtons();
            UpdateDirty();
        };
        return b;
    }

    private void UpdateFormatButtons()
    {
        _yaml.Classes.Set("accent", _format == "yaml");
        _json.Classes.Set("accent", _format == "json");
        _sourceColors.Language = _format;
        _source.TextArea.TextView.Redraw();
    }

    private Control BuildColors()
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14, 12, 18, 24) };

        _name.LostFocus += (_, _) => Edit(t => t with { Name = string.IsNullOrWhiteSpace(_name.Text) ? t.Name : _name.Text.Trim() });
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Edit(t => t with { Name = string.IsNullOrWhiteSpace(_name.Text) ? t.Name : _name.Text.Trim() }); };
        _description.LostFocus += (_, _) => Edit(t => t with { Description = _description.Text?.Trim() ?? "" });
        _font.LostFocus += (_, _) => Edit(t => t with { Font = string.IsNullOrWhiteSpace(_font.Text) ? null : _font.Text.Trim() });
        _font.KeyDown += (_, e) => { if (e.Key == Key.Enter) Edit(t => t with { Font = string.IsNullOrWhiteSpace(_font.Text) ? null : _font.Text.Trim() }); };
        _light.IsCheckedChanged += (_, _) => Edit(t => t with { Light = _light.IsChecked == true });
        _bevel.IsCheckedChanged += (_, _) => Edit(t => t with { Bevel = _bevel.IsChecked == true });
        _radius.ValueChanged += (_, e) => { _radiusText.Text = $"{e.NewValue:0}px"; Edit(t => t with { Radius = Math.Round(e.NewValue) }); };
        _chrome.SelectionChanged += (_, _) => { if (_chrome.SelectedItem is string c) Edit(t => t with { Chrome = c }); };
        _sounds.SelectionChanged += (_, _) => { if (_sounds.SelectedItem is string s) Edit(t => t with { SoundSet = s }); };

        var general = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"), RowSpacing = 8 };
        void G(int row, string label, Control editor, string? tip = null)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            if (tip is not null) ToolTip.SetTip(l, tip);
            general.Children.Add(l.At(row, 0));
            general.Children.Add(editor.At(row, 1));
        }
        G(0, "Name", _name);
        G(1, "Description", _description);
        G(2, "Controls", _light, "Light or dark buttons, fields and scroll bars");
        G(3, "Corners", Ui.Row(8, _radius, _radiusText), "Corner radius; 0 gives square corners");
        G(4, "Title bars", _chrome, "system uses the desktop's title bar; custom draws one in the theme's colours");
        G(5, "Sounds", _sounds);
        G(6, "Bevels", _bevel, "Raised buttons and sunken fields, like a 1990s desktop");
        G(7, "Font", _font, "Comma-separated font families for the interface; the first one installed is used");
        body.Children.Add(Ui.Card(Ui.Column(10, Ui.Text("General", "h2"), general)));

        // Quick start: rebuild everything from three colours.
        var derive = Ui.Column(10,
            Ui.Text("Start from three colours", "h2"),
            Ui.Apply(Ui.Text("Pick a background, a text colour and an accent, then rebuild: every surface, line and text shade is mixed from them, " +
                             "and status colours come from the terminal colours.", "dim"), t => t.FontSize = 12),
            Ui.Row(14,
                Ui.Row(6, ColorButton(() => _deriveBg, v => _deriveBg = v, 26), Ui.Text("Background")),
                Ui.Row(6, ColorButton(() => _deriveFg, v => _deriveFg = v, 26), Ui.Text("Text")),
                Ui.Row(6, ColorButton(() => _deriveAccent, v => _deriveAccent = v, 26), Ui.Text("Accent"))),
            Ui.Row(8,
                Ui.TextButton("Rebuild All Colours", () => Edit(t => Themes.Derive(t.Id, t.Name, t.Description, _deriveBg, _deriveFg, _deriveAccent, null, t.Ansi)
                    with { File = t.File, Radius = t.Radius, Chrome = t.Chrome, SoundSet = t.SoundSet, Bevel = t.Bevel, Font = t.Font }), "Replace every colour with ones mixed from these three"),
                Ui.TextButton("Recolour From Accent", () => Edit(t => t.WithAccent(t["accent"])), "Rebuild links, the deep accent and text-on-accent from the accent colour")));
        body.Children.Add(Ui.Card(derive));

        foreach (var group in ThemeFile.Tokens.GroupBy(t => t.Group))
        {
            var rows = new StackPanel { Spacing = 2 };
            rows.Children.Add(Ui.Apply(Ui.Text(group.Key, "h2"), t => t.Margin = new Thickness(0, 0, 0, 6)));
            foreach (var token in group) rows.Children.Add(TokenRow(token));
            body.Children.Add(Ui.Card(rows));
        }

        var ansi = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", 8))), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        for (var i = 0; i < 16; i++)
        {
            var index = i;
            var b = ColorButton(() => _draft.Ansi[index], v => Edit(t =>
            {
                var a = (string[])t.Ansi.Clone();
                a[index] = v;
                return t with { Ansi = a };
            }), 32);
            ToolTip.SetTip(b, ThemeFile.AnsiNames[i]);
            ansi.Children.Add(b.At(i / 8, i % 8));
        }
        body.Children.Add(Ui.Card(Ui.Column(8, Ui.Text("Terminal colours", "h2"),
            Ui.Apply(Ui.Text("Black, red, green, yellow, blue, magenta, cyan and white; the bright ones underneath.", "dim"), t => t.FontSize = 12), ansi)));

        _colorsScroll.Content = body;
        return _colorsScroll;
    }

    private Control TokenRow(ThemeToken token)
    {
        var hex = new TextBox { Width = 92, FontFamily = Ui.Mono, FontSize = 12, Padding = new Thickness(6, 3), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        void Commit()
        {
            if (Rgb.TryParse(hex.Text, out var c)) SetColor(token.Name, c.Hex);
            else hex.Text = _draft[token.Name];
        }
        hex.LostFocus += (_, _) => Commit();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; hex.Text = _draft[token.Name]; }
        };
        var swatch = ColorButton(() => _draft[token.Name], v => SetColor(token.Name, v), 24);
        var reset = Ui.IconButton(Icons.Undo, "Back to the saved colour", () => SetColor(token.Name, _original[token.Name]));
        reset.Padding = new Thickness(4);
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,100,Auto,*,Auto"), ColumnSpacing = 8, Background = Brushes.Transparent,
            Children =
            {
                swatch.At(0, 0),
                new TextBlock { Text = token.Name, FontFamily = Ui.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.At(0, 1),
                hex.At(0, 2),
                new TextBlock { Text = token.Description, Foreground = Ui.Dim, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.At(0, 3),
                reset.At(0, 4),
            },
        };
        ToolTip.SetTip(row, token.Description);
        _rows[token.Name] = (row, hex);
        return row;
    }

    /// <summary>A swatch that opens a colour picker. <paramref name="get"/> is read whenever the swatches are refreshed.</summary>
    private Button ColorButton(Func<string> get, Action<string> set, double size)
    {
        var swatch = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1),
            Background = Ui.Raw(get()),
        };
        _swatches.Add((swatch, get));
        var view = new ColorView
        {
            IsAlphaEnabled = false, IsAlphaVisible = false, IsColorPaletteVisible = true, IsAccentColorsVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Box, Width = 320, Height = 380,
        };
        var opening = false;
        view.ColorChanged += (_, e) =>
        {
            if (opening) return;
            var hex = new Rgb(e.NewColor.R, e.NewColor.G, e.NewColor.B).Hex;
            if (!string.Equals(hex, get(), StringComparison.OrdinalIgnoreCase)) set(hex);
            swatch.Background = Ui.Raw(get());
        };
        var flyout = new Flyout { Content = view, Placement = PlacementMode.RightEdgeAlignedTop };
        flyout.Opening += (_, _) =>
        {
            opening = true;
            try { view.Color = Color.Parse(get()); }
            finally { opening = false; }
        };
        var button = new Button { Content = swatch, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Flyout = flyout, Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 0 };
        return button;
    }

    /// <summary>A small, made-up screen that uses every colour token, built from the live theme brushes.</summary>
    private Control BuildPreview()
    {
        Border Spot(Border b, params string[] tokens)
        {
            ToolTip.SetTip(b, string.Join(", ", tokens) + " (click to edit)");
            b.Cursor = new Cursor(StandardCursorType.Hand);
            b.PointerPressed += (_, e) => { e.Handled = true; Reveal(tokens[0]); };
            return b;
        }
        TextBlock T(string text, string token, double size = 13, FontWeight? weight = null) =>
            new() { Text = text, Foreground = Ui.Tk(token), FontSize = size, FontWeight = weight ?? FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };

        // Window title bar and navigation.
        var title = Spot(new Border
        {
            Background = Ui.Tk("titleBg"), Height = 30, Padding = new Thickness(10, 0),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Row(6, Enumerable.Range(0, 3).Select(_ => (Control)new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Ui.Tk("titleFg"), Opacity = 0.6 }).ToArray()).Dock(Dock.Right),
                    T("avamux — preview", "titleFg", 12.5, FontWeight.SemiBold),
                },
            },
        }, "titleBg", "titleFg");

        var nav = new ListBox { Classes = { "nav" }, Background = Brushes.Transparent, SelectedIndex = 1 };
        foreach (var (icon, text) in new[] { (Icons.Console, "Terminals"), (Icons.Magnify, "Search"), (Icons.BookOpenVariant, "Wiki"), (Icons.SourceBranch, "Repositories") })
            nav.Items.Add(new ListBoxItem { Content = Ui.Row(8, new PathIcon { Data = icon }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }), Padding = new Thickness(10, 7) });
        var navPane = Spot(new Border { Background = Ui.Tk("bgDeep"), Width = 160, BorderBrush = Ui.Tk("line"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 8), Child = nav }, "bgDeep", "line");

        // Text shades.
        var shades = new WrapPanel();
        foreach (var token in new[] { "textStrong", "text", "textSoft", "textMute", "textCode", "dim", "faint", "ghost" })
        {
            var b = Spot(new Border { Padding = new Thickness(0, 0, 14, 4), Background = Brushes.Transparent, Child = T(token, token, 12.5) }, token);
            shades.Children.Add(b);
        }

        var chips = Ui.Row(6, new[] { ("ok", "passing"), ("warn", "warning"), ("error", "failed"), ("accent", "running"), ("purple", "agent"), ("cyan", "watch"), ("orange", "timer") }
            .Select(p => (Control)Spot(Ui.Chip(p.Item2, Ui.Tk(p.Item1)), p.Item1, "onAccent")).ToArray());

        var tabs = Ui.Row(0, new Button { Content = "Changes", Classes = { "tab", "on" } }, new Button { Content = "History", Classes = { "tab" } }, new Button { Content = "Branches", Classes = { "tab" } });

        var controls = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Row(8, new Button { Content = "Primary", Classes = { "accent" } }, new Button { Content = "Button" }, Ui.IconButton(Icons.Refresh, "A flat button", () => { }, "Flat"),
                    Ui.Link("A link", () => { })),
                Ui.Row(10, new TextBox { Text = "Some text", Width = 150 }, new TextBox { PlaceholderText = "Placeholder", Width = 150 }),
                Ui.Row(14, new CheckBox { Content = "Check box", IsChecked = true }, new RadioButton { Content = "Option", IsChecked = true }, new ToggleSwitch { IsChecked = true, OnContent = "On", OffContent = "Off" }),
                Ui.Row(12, new Slider { Width = 160, Value = 60, Maximum = 100 }, new ProgressBar { Width = 140, Value = 40, Maximum = 100, VerticalAlignment = VerticalAlignment.Center }),
                new ComboBox { ItemsSource = new[] { "Combo box", "Second" }, SelectedIndex = 0 },
            },
        };

        var list = new StackPanel();
        foreach (var (text, detail, token) in new[] { ("build.sh", "2 minutes ago", "cell"), ("Selected row", "just now", "cellSel"), ("notes.md", "yesterday", "cell") })
            list.Children.Add(Spot(new Border
            {
                Background = Ui.Tk(token), Padding = new Thickness(10, 6), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new DockPanel { Children = { T(detail, "faint", 11.5).Dock(Dock.Right), T(text, "text") } },
            }, token, "lineSoft"));

        var diff = new StackPanel();
        foreach (var (text, token, fg) in new[] { ("@@ -12,4 +12,5 @@ Settings", "hunkBg", "accent"), ("   var theme = Themes.Find(name);", "bgEditor", "textCode"), ("-  Apply(theme);", "delBg", "error"), ("+  Apply(theme.WithAccent(accent));", "addBg", "ok") })
            diff.Children.Add(Spot(new Border { Background = Ui.Tk(token), Padding = new Thickness(8, 2), Child = new TextBlock { Text = text, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tk(fg) } }, token));

        var terminal = new StackPanel { Spacing = 1 };
        void Line(params (string Text, int Ansi)[] parts)
        {
            var tb = new TextBlock { FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tk("termFg") };
            foreach (var (text, ansi) in parts)
            {
                var run = new Run(text);
                if (ansi >= 0) _ansiRuns.Add((run, ansi));
                tb.Inlines!.Add(run);
            }
            terminal.Children.Add(tb);
        }
        Line(("user", 2), ("@", -1), ("avamux", 4), (" ~/projects ", -1), ("main", 5), (" $ ls --color", -1));
        Line(("src/  ", 12), ("docs/  ", 12), ("build.sh  ", 10), ("README.md  ", -1), ("logo.png", 13));
        Line(("$ ", -1), ("dotnet test", 3));
        Line(("  Passed!", 2), ("  Failed: 0, Skipped: ", -1), ("1", 3), (", warnings: ", 8), ("none", 6));
        Line(("  error", 1), (" CS0103: ", 9), ("bright: ", -1), ("■", 8), ("■", 9), ("■", 10), ("■", 11), ("■", 12), ("■", 13), ("■", 14), ("■", 15));
        var selection = new Border { Background = Ui.Tk("termSel"), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = "selected text", FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tk("termFg") } };
        terminal.Children.Add(Ui.Row(0, new TextBlock { Text = "$ echo ", FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tk("termFg") }, selection,
            new Border { Width = 8, Height = 15, Background = Ui.Tk("accent"), Margin = new Thickness(2, 0, 0, 0) }));
        var termPane = Spot(new Border { Background = Ui.Tk("termBg"), Padding = new Thickness(10, 8), CornerRadius = Ui.R(6), Child = terminal }, "termBg", "termFg", "termSel");

        Border Section(string heading, Control child) => new()
        {
            Classes = { "card" },
            Child = Ui.Column(8, T(heading, "textStrong", 13, FontWeight.SemiBold), child),
        };

        var main = Spot(new Border
        {
            Background = Ui.Tk("bg"), Padding = new Thickness(16, 12),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Ui.Column(2, T("Search", "textStrong", 20, FontWeight.SemiBold), T("A heading, its subtitle and the page background.", "dim", 12)),
                    Section("Controls", controls),
                    Section("Text", Ui.Column(8, shades, chips)),
                    Spot(new Border
                    {
                        Classes = { "card" }, Padding = new Thickness(0),
                        Child = new StackPanel { Children = { new Border { Padding = new Thickness(8, 2, 8, 0), Child = tabs }, new Border { Background = Ui.Tk("bgEditor"), Child = list } } },
                    }, "panel", "bgEditor"),
                    Section("Diff", new Border { ClipToBounds = true, CornerRadius = Ui.R(4), Child = diff }),
                    Section("Terminal", termPane),
                    Spot(new Border
                    {
                        Background = Ui.Tk("panelHi"), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8), CornerRadius = Ui.R(6),
                        Child = Ui.Row(8, new PathIcon { Data = Icons.AlertCircle, Foreground = Ui.Tk("warn") }, T("A raised panel, like a tooltip or toast.", "textSoft", 12.5),
                            T("Something went wrong.", "errorText", 12.5)),
                    }, "panelHi", "lineStrong", "errorText"),
                },
            },
        }, "bg");

        var window = new Border
        {
            Margin = new Thickness(18), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1), ClipToBounds = true, CornerRadius = Ui.R(8),
            Child = new DockPanel { Children = { title.Dock(Dock.Top), navPane.Dock(Dock.Left), main } },
        };

        return new StackPanel
        {
            Children =
            {
                new Border
                {
                    Margin = new Thickness(18, 14, 18, 0), Padding = new Thickness(12, 8), Background = Ui.Tk("panel"), BorderBrush = Ui.Tk("line"), BorderThickness = new Thickness(1), CornerRadius = Ui.R(6),
                    Child = Ui.Column(4,
                        Ui.Apply(Ui.Text("Preview — the whole app shows the theme while the editor is open. Click anything here to edit its colour.", "dim"), t => t.FontSize = 12),
                        _readability),
                },
                window,
            },
        };
    }

    // ------------------------------------------------------------------ state

    private bool IsDirty => Signature(_draft) != Signature(_original);

    private static string Signature(ThemeDef t) => t.Fingerprint() + "|" + t.Name + "|" + t.Description;

    private void Load(ThemeDef theme, string? text = null)
    {
        _original = theme;
        _draft = theme;
        _format = theme.File is { } file ? ThemeFile.FormatOf(file) : _format;
        _sourceOfOriginal = text ?? (theme.File is { } f && File.Exists(f) ? SafeRead(f) ?? ThemeFile.Write(theme, _format) : ThemeFile.Write(theme, _format));
        _deriveBg = theme["bg"]; _deriveFg = theme["text"]; _deriveAccent = theme["accent"];
        WriteSource(_sourceOfOriginal);
        _sourceBroken = false;
        _sourceError.IsVisible = false;
        UpdateFormatButtons();
        RebuildPicker();
        RefreshFields();
        UpdateDirty();
        ShowPreview();
    }

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    private async Task<bool> SwitchToAsync(string id)
    {
        var theme = Themes.TryFind(id);
        if (theme is null) return false;
        if (IsDirty && !await Ui.ConfirmAsync(this, "Unsaved theme", $"Discard the changes to {_draft.Name} and open {theme.Name}?", "Discard"))
        {
            RebuildPicker();
            return false;
        }
        Load(theme);
        return true;
    }

    private void OnThemeListChanged() => Dispatcher.UIThread.Post(() =>
    {
        RebuildPicker();
        // The file was changed outside the editor (by hand, or by another avamux command): follow it when there is nothing to lose.
        if (!IsDirty && Themes.TryFind(_draft.Id) is { } fresh && Signature(fresh) != Signature(_original) && !_saving) Load(fresh);
        UpdateWhere();
    });

    private void RebuildPicker()
    {
        _loading = true;
        try
        {
            var items = Themes.All.Select(t => new ComboBoxItem { Content = t.File is null ? t.Name : $"{t.Name}   ·  file", Tag = t.Id }).ToList();
            if (items.All(i => (string)i.Tag! != _draft.Id)) items.Add(new ComboBoxItem { Content = _draft.Name + " (not saved)", Tag = _draft.Id });
            _picker.ItemsSource = items;
            _picker.SelectedItem = items.First(i => (string)i.Tag! == _draft.Id);
        }
        finally { _loading = false; }
    }

    private void Edit(Func<ThemeDef, ThemeDef> change)
    {
        if (_loading) return;
        var next = change(_draft);
        if (Signature(next) == Signature(_draft)) return;
        _draft = next;
        WriteSource(ThemeFile.Write(_draft, _format));
        _sourceBroken = false;
        _sourceError.IsVisible = false;
        RefreshFields();
        UpdateDirty();
        _previewSoon.Stop(); _previewSoon.Start();
    }

    private void SetColor(string token, string hex) => Edit(t => t with { Colors = new Dictionary<string, string>(t.Colors) { [token] = hex } });

    private void WriteSource(string text)
    {
        if (_source.Text == text) return;
        _settingSource = true;
        try
        {
            var caret = _source.CaretOffset;
            var scroll = _source.TextArea.TextView.ScrollOffset;
            _source.Text = text;
            _source.CaretOffset = Math.Min(caret, text.Length);
            _source.ScrollEditorTo(scroll.Y);
        }
        finally { _settingSource = false; }
    }

    private bool _parsePending;

    /// <summary>Reads the theme file text now if typing left it unread (saving straight after typing).</summary>
    private void ParseSourceIfPending()
    {
        if (_parsePending) ParseSource();
    }

    private void ParseSource()
    {
        _parsePending = false;
        try
        {
            var text = _source.Text;
            var format = ThemeFile.DetectFormat(text);
            var theme = ThemeFile.Parse(text, _draft.Id, format, other => string.Equals(other, _draft.Id, StringComparison.OrdinalIgnoreCase) ? null : Themes.TryFind(other)) with { File = _draft.File };
            _sourceBroken = false;
            _sourceError.IsVisible = false;
            if (format != _format) { _format = format; UpdateFormatButtons(); }
            _draft = theme;
            RefreshFields();
            UpdateDirty();
            ShowPreview();
        }
        catch (ThemeFileException ex)
        {
            _sourceBroken = true;
            _sourceError.Text = string.Join("\n", ex.Problems.Take(6)) + (ex.Problems.Count > 6 ? $"\n… and {ex.Problems.Count - 6} more" : "");
            _sourceError.IsVisible = true;
            UpdateDirty();
        }
    }

    private void RefreshFields()
    {
        _loading = true;
        try
        {
            if (!_name.IsFocused) _name.Text = _draft.Name;
            if (!_description.IsFocused) _description.Text = _draft.Description;
            if (!_font.IsFocused) _font.Text = _draft.Font ?? "";
            _light.IsChecked = _draft.Light;
            _bevel.IsChecked = _draft.Bevel;
            _radius.Value = Math.Clamp(_draft.Radius, 0, 16);
            _radiusText.Text = $"{_draft.Radius:0}px";
            _chrome.SelectedItem = _draft.Chrome;
            _sounds.SelectedItem = _draft.SoundSet;
            foreach (var (token, (_, hex)) in _rows)
                if (!hex.IsFocused) hex.Text = _draft[token];
            foreach (var (swatch, get) in _swatches) swatch.Background = Ui.Raw(get());
            foreach (var (run, ansi) in _ansiRuns) run.Foreground = Ui.Raw(_draft.Ansi[ansi]);
            UpdateReadability();
        }
        finally { _loading = false; }
    }

    private void ShowPreview()
    {
        if (Application.Current is { } app) ThemeManager.Preview(app, _draft);
    }

    private void UpdateDirty()
    {
        _dirty.IsVisible = IsDirty || _sourceBroken;
        _dirty.Text = _sourceBroken ? "● the theme file has problems" : "● unsaved changes";
        _dirty.Foreground = Ui.Tk(_sourceBroken ? "error" : "warn");
        Title = (IsDirty ? "• " : "") + $"{_draft.Name} — Theme Editor — avamux";
        UpdateWhere();
    }

    private void UpdateWhere()
    {
        var builtIn = Themes.IsBuiltinId(_draft.Id);
        _where.Text = _draft.File is { } file
            ? (builtIn ? $"Replaces the built-in theme · {file}" : file)
            : $"Built in. Saving makes a copy in {Store.Dir}";
        _delete.IsVisible = _draft.File is not null;
        ToolTip.SetTip(_delete, builtIn ? "Delete this file and go back to the built-in theme" : "Delete this theme file");
        var current = Themes.Find(App.Services.Config.Settings.Theme.Name).Id;
        _use.IsEnabled = !string.Equals(current, _draft.Id, StringComparison.OrdinalIgnoreCase) || IsDirty;
        _use.Content = string.Equals(current, _draft.Id, StringComparison.OrdinalIgnoreCase) && !IsDirty ? "In Use" : "Use This Theme";
    }

    /// <summary>Flags text that would be hard to read: WCAG contrast below 4.5:1 for body text, 3:1 for the rest.</summary>
    private void UpdateReadability()
    {
        _readability.Children.Clear();
        var weak = new List<string>();
        foreach (var (fg, bg, min) in new[]
                 {
                     ("text", "bg", 4.5), ("text", "panel", 4.5), ("text", "bgEditor", 4.5), ("textCode", "bgEditor", 4.5), ("text", "cellSel", 4.5),
                     ("dim", "bg", 3.0), ("dim", "panel", 3.0), ("link", "bg", 3.0), ("onAccent", "accent", 3.0), ("termFg", "termBg", 4.5),
                     ("titleFg", "titleBg", 4.5), ("errorText", "panel", 3.0),
                 })
        {
            var ratio = Contrast(_draft[fg], _draft[bg]);
            if (ratio < min) weak.Add($"{fg} on {bg}: {ratio:0.0}:1 (aim for {min:0.#}:1)");
        }
        if (weak.Count == 0)
        {
            _readability.Children.Add(new TextBlock { Text = "✓ Text is readable on every surface.", Foreground = Ui.Tk("ok"), FontSize = 12 });
            return;
        }
        _readability.Children.Add(new TextBlock { Text = "Hard to read:", Foreground = Ui.Tk("warn"), FontSize = 12, FontWeight = FontWeight.SemiBold });
        foreach (var w in weak)
        {
            var token = w[..w.IndexOf(' ')];
            var link = Ui.Link(w, () => Reveal(token), "Edit " + token);
            link.FontSize = 12;
            _readability.Children.Add(link);
        }
    }

    public static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            var c = Rgb.Parse(hex);
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Shows a token's row in the Colours tab and draws attention to it.</summary>
    private void Reveal(string token)
    {
        if (!_rows.TryGetValue(token, out var row)) return;
        ShowTab(0);
        Dispatcher.UIThread.Post(() =>
        {
            row.Row.BringIntoView();
            if (row.Row is Panel panel)
            {
                panel.Background = Ui.Tk("cellSel");
                DispatcherTimer.RunOnce(() => panel.Background = Brushes.Transparent, TimeSpan.FromSeconds(1.4));
            }
            row.Hex.Focus();
            row.Hex.SelectAll();
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ actions

    private bool _saving;

    private async Task SaveAsync()
    {
        if (_sourceBroken) { ShowTab(1); Ui.Toast(this, "The theme file has problems", "Fix them (they are listed under the file) before saving."); return; }
        if (_draft.File is null) { await NewCopyAsync(); return; }
        await Ui.TryAsync(this, () =>
        {
            _saving = true;
            try
            {
                var text = Signature(ThemeFile.Parse(_source.Text, _draft.Id, _format, other => string.Equals(other, _draft.Id, StringComparison.OrdinalIgnoreCase) ? null : Themes.TryFind(other))) == Signature(_draft)
                    ? _source.Text : ThemeFile.Write(_draft, _format);
                var saved = Store.SaveText(_draft.Id, text, _format);
                Load(saved, text);
            }
            finally { _saving = false; }
            return Task.CompletedTask;
        }, "Could not save the theme");
    }

    /// <summary>Saves what is shown as a new theme file under a name the user picks, and carries on editing that.</summary>
    private async Task NewCopyAsync()
    {
        var suggested = Themes.IsBuiltinId(_draft.Id) && _draft.File is null && IsDirty ? $"My {_draft.Name}" : $"{_draft.Name} copy";
        var name = await Ui.PromptAsync(this, "New theme", "Name of the new theme:", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        await Ui.TryAsync(this, () =>
        {
            _saving = true;
            try
            {
                var id = Store.UniqueId(name);
                var copy = _draft with { Id = id, Name = name.Trim(), File = null };
                var text = ThemeFile.Write(copy, _format);
                var saved = Store.SaveText(id, text, _format);
                Load(saved, text);
            }
            finally { _saving = false; }
            return Task.CompletedTask;
        }, "Could not save the theme");
    }

    private async Task RevertAsync()
    {
        if (!IsDirty && !_sourceBroken) return;
        if (!await Ui.ConfirmAsync(this, "Revert", $"Throw away the changes to {_draft.Name}?", "Revert")) return;
        Load(_original, _sourceOfOriginal);
    }

    private async Task DeleteAsync()
    {
        if (_draft.File is null) return;
        var builtIn = Themes.IsBuiltinId(_draft.Id);
        var message = builtIn
            ? $"Delete {Path.GetFileName(_draft.File)}? The built-in {_draft.Name} theme comes back."
            : $"Delete the theme {_draft.Name} ({Path.GetFileName(_draft.File)})? Anything using it switches to Simple.";
        if (!await Ui.ConfirmAsync(this, "Delete theme", message)) return;
        Ui.Try(this, () =>
        {
            var id = _draft.Id;
            _original = _draft; // nothing to protect any more
            Store.Delete(id);
            Load(Themes.TryFind(id) ?? Themes.Find(App.Services.Config.Settings.Theme.Name));
        }, "Could not delete the theme");
    }

    private async Task UseAsync()
    {
        if (IsDirty || _draft.File is null && !Themes.IsBuiltinId(_draft.Id)) await SaveAsync();
        if (IsDirty) return; // saving was cancelled or failed
        Ui.Try(this, () =>
        {
            App.Services.Config.Set("theme.name", JsonValue.Create(_draft.Id), ConfigScope.Home);
            var accent = App.Services.Config.Settings.Theme.Accent;
            if (!string.IsNullOrWhiteSpace(accent))
                Ui.Toast(this, $"Using {_draft.Name}", $"Your accent colour override ({accent}) still applies on top of it. Clear it in Settings → Themes to see the theme's own accent.");
            UpdateWhere();
        }, "Could not switch theme");
    }

    private async Task ImportAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a theme", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Theme files") { Patterns = ["*.yaml", "*.yml", "*.json"] }],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (IsDirty && !await Ui.ConfirmAsync(this, "Unsaved theme", $"Discard the changes to {_draft.Name}?", "Discard")) return;
        await Ui.TryAsync(this, () =>
        {
            var id = ThemeStore.IdOf(path);
            if (Themes.All.Any(t => t.File is not null && t.Id == id)) id = Store.UniqueId(id);
            var theme = Store.Import(path, id);
            Load(theme);
            return Task.CompletedTask;
        }, "Could not import the theme");
    }

    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the theme", SuggestedFileName = ThemeStore.Slug(_draft.Name) + (_format == "json" ? ".json" : ".yaml"),
            FileTypeChoices = [new FilePickerFileType("YAML") { Patterns = ["*.yaml"] }, new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Ui.Try(this, () =>
        {
            var format = ThemeFile.FormatOf(path);
            File.WriteAllText(path, format == _format && !_sourceBroken ? _source.Text : ThemeFile.Write(_draft, format));
            Ui.Toast(this, "Theme exported", path);
        }, "Could not export the theme");
    }
}
