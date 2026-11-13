using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Plugins;

namespace SamplePagePlugin;

/// <summary>
/// A sample page plugin: ordinary Avalonia controls on the left, and on the right the same kinds of control
/// wired to avamux through <see cref="IAvamuxHost"/>: notifications, commands, settings, navigation, the theme.
/// </summary>
public sealed class SamplePage : IPagePlugin, IPageVisibility, IDisposable
{
    private IAvamuxHost _host = null!;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _clock = new() { FontSize = 26, FontWeight = FontWeight.Light };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 59, Height = 6 };
    private readonly ListBox _terminals = new() { Height = 120 };
    private readonly ToggleSwitch _crt = new() { OnContent = "CRT effect on", OffContent = "CRT effect off" };
    private readonly TextBlock _shown = new() { FontSize = 12 };
    private readonly WrapPanel _swatches = new();
    private readonly List<(Border Card, string Token)> _themed = [];
    private Border _root = null!;
    private int _timesShown;
    private bool _loadingSetting;

    public object CreateView(IAvamuxHost host)
    {
        _host = host;
        host.Log("Sample page created");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(20, 0, 20, 20) };
        grid.Children.Add(Column(0, PlainControls()));
        grid.Children.Add(Column(1, ApiControls()));

        _root = new Border
        {
            Child = new DockPanel
            {
                Children =
                {
                    Docked(Dock.Top, new StackPanel
                    {
                        Margin = new Thickness(20, 16, 20, 12), Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = "Sample plugin page", FontSize = 20, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = $"Built by SamplePagePlugin.dll, shown as \"{host.PageName}\" in avamux {host.Version}. Data folder: {host.DataDirectory}", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
                        },
                    }),
                    new ScrollViewer { Content = grid },
                },
            },
        };

        _clockTimer.Tick += (_, _) => Tick();
        _clockTimer.Start();
        Tick();

        host.SettingsChanged += ReadSettings;
        host.ThemeChanged += ApplyTheme;
        ReadSettings();
        ApplyTheme();
        _ = RefreshTerminalsAsync();
        return _root;
    }

    // ---- left: controls that need nothing from avamux -------------------------------------------------------

    private Control[] PlainControls()
    {
        var name = new TextBox { PlaceholderText = "Type your name" };
        var greeting = new TextBlock { Text = "Hello!", FontSize = 15 };
        name.TextChanged += (_, _) => greeting.Text = string.IsNullOrWhiteSpace(name.Text) ? "Hello!" : $"Hello, {name.Text!.Trim()}!";

        var count = 0;
        var counter = new TextBlock { Text = "Clicked 0 times", VerticalAlignment = VerticalAlignment.Center };
        var click = new Button { Content = "Click me" };
        click.Click += (_, _) => counter.Text = $"Clicked {++count} time{(count == 1 ? "" : "s")}";

        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 40 };
        var sliderBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 40, Height = 6 };
        var sliderText = new TextBlock { Text = "40", Width = 30, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { sliderBar.Value = e.NewValue; sliderText.Text = Math.Round(e.NewValue).ToString(); };

        var choice = new ComboBox { Items = { "Espresso", "Flat white", "Tea", "Water" }, SelectedIndex = 0, MinWidth = 160 };
        var extras = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { new CheckBox { Content = "Milk" }, new CheckBox { Content = "Sugar" }, new CheckBox { Content = "To go", IsChecked = true } } };
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { new RadioButton { Content = "Small", GroupName = "size" }, new RadioButton { Content = "Medium", GroupName = "size", IsChecked = true }, new RadioButton { Content = "Large", GroupName = "size" } } };
        var order = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        void Describe()
        {
            var with = extras.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => c.Content?.ToString()?.ToLowerInvariant()).ToList();
            var chosen = size.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Content?.ToString()?.ToLowerInvariant();
            order.Text = $"One {chosen} {choice.SelectedItem?.ToString()?.ToLowerInvariant()}{(with.Count > 0 ? ", " + string.Join(", ", with) : "")}.";
        }
        choice.SelectionChanged += (_, _) => Describe();
        foreach (var c in extras.Children.OfType<CheckBox>()) c.IsCheckedChanged += (_, _) => Describe();
        foreach (var r in size.Children.OfType<RadioButton>()) r.IsCheckedChanged += (_, _) => Describe();
        Describe();

        var date = new CalendarDatePicker { SelectedDate = DateTime.Today };
        var number = new NumericUpDown { Minimum = 0, Maximum = 10, Value = 3, Increment = 1, Width = 130 };

        return
        [
            Card("Clock", "A timer in the plugin updates this every second while the page is in view.", _clock, _progress, _shown),
            Card("Text and buttons", null, name, greeting, Row(click, counter)),
            Card("Slider and progress", null, Row(slider, sliderText), sliderBar),
            Card("Choices", null, Row(choice, number, date), extras, size, order),
        ];
    }

    // ---- right: controls that call avamux ------------------------------------------------------------------

    private Control[] ApiControls()
    {
        // Notifications.
        var message = new TextBox { Text = "Hello from the sample plugin", PlaceholderText = "Message" };
        var notify = Button("Notify", () => _host.Notify("Sample plugin", message.Text ?? ""));

        // Attention and badge: go to another page within five seconds to see the mark appear on this one.
        var attention = Button("Ask for attention in 5 s", async () =>
        {
            await Task.Delay(5000);
            _host.RequestAttention("The sample plugin would like a word");
        });
        var badge = new NumericUpDown { Minimum = 0, Maximum = 99, Value = 0, Increment = 1, Width = 130, FormatString = "0" };
        badge.ValueChanged += (_, e) => _host.SetBadge(e.NewValue is > 0 and var n ? ((int)n).ToString() : null);

        // Commands: anything the avamux command line can do.
        var program = new TextBox { Text = "top", Width = 180 };
        var open = Button("Open in a new terminal tab", async () =>
        {
            // "command" is the program and its arguments, one word each; "background" leaves the current tab selected.
            var words = (program.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => (JsonNode)w).ToArray();
            var created = await _host.CommandAsync("terminal new", new JsonObject { ["command"] = new JsonArray(words), ["name"] = "from the sample plugin", ["background"] = true });
            _host.Notify("Terminal started", $"Tab {created?["id"]} is running {program.Text}.");
            await RefreshTerminalsAsync();
        });
        var refresh = Button("Refresh", RefreshTerminalsAsync);
        var show = Button("Go to Terminals", () => _host.Navigate("terminals"));

        var line = new TextBox { Text = "app status", FontFamily = new FontFamily("monospace") };
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 150, FontFamily = new FontFamily("monospace"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        var run = Button("Run", async () =>
        {
            // RunAsync takes the words of a command line; quoting is up to the caller.
            try { output.Text = Pretty(await _host.RunAsync((line.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))); }
            catch (Exception ex) { output.Text = "Error: " + ex.Message; }
        });
        line.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; run.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); } };

        // Settings: read, write, and follow changes made elsewhere.
        _crt.IsCheckedChanged += (_, _) =>
        {
            if (_loadingSetting) return;
            try { _host.SetSetting("terminal.crt", _crt.IsChecked == true ? "true" : "false"); }
            catch (Exception ex) { _host.Notify("Could not change the setting", ex.Message); }
        };

        // Navigation.
        var pages = new ComboBox { Items = { "terminals", "search", "settings", "watching", "events", "dashboard", "logs", "wiki", "repositories" }, SelectedIndex = 7, MinWidth = 160 };
        var go = Button("Go", () => _host.Navigate(pages.SelectedItem?.ToString() ?? "terminals"));

        return
        [
            Card("Notifications", "host.Notify, host.RequestAttention and host.SetBadge.", Row(message, notify), Row(attention, new TextBlock { Text = "Badge", VerticalAlignment = VerticalAlignment.Center }, badge)),
            Card("Terminals", "host.CommandAsync(\"terminal list\") and (\"terminal new\", …).", _terminals, Row(program, open), Row(refresh, show)),
            Card("Any command", "host.RunAsync runs what the avamux command line runs: try \"watch list\", \"settings get theme.name\", \"page list\".", Row(line, run), output),
            Card("Settings", "host.GetSetting / SetSetting, kept in step through host.SettingsChanged.", _crt),
            Card("Navigation", "host.Navigate.", Row(pages, go)),
            Card("Theme", "host.ThemeColor(token), refreshed on host.ThemeChanged. This page's cards use the same colours.", _swatches),
        ];
    }

    private async Task RefreshTerminalsAsync()
    {
        try
        {
            var list = await _host.CommandAsync("terminal list") as JsonArray ?? [];
            _terminals.ItemsSource = list.Select(t => $"{t?["id"]}   {t?["title"]}   ({t?["state"]}, pid {t?["pid"]})").ToList();
        }
        catch (Exception ex) { _terminals.ItemsSource = new[] { "Error: " + ex.Message }; }
    }

    private void ReadSettings()
    {
        _loadingSetting = true;
        try { _crt.IsChecked = _host.GetSetting("terminal.crt") == "true"; }
        finally { _loadingSetting = false; }
    }

    private void ApplyTheme()
    {
        _root.Background = Brush("bg");
        foreach (var (card, token) in _themed) { card.Background = Brush(token); card.BorderBrush = Brush("line"); }
        _swatches.Children.Clear();
        foreach (var token in new[] { "accent", "ok", "warn", "error", "purple", "text", "dim", "panel", "bg" })
        {
            var hex = _host.ThemeColor(token);
            _swatches.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 12, 6), Spacing = 3,
                Children =
                {
                    new Border { Width = 54, Height = 26, CornerRadius = new CornerRadius(4), Background = Brush(token), BorderBrush = Brush("line"), BorderThickness = new Thickness(1) },
                    new TextBlock { Text = token, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = hex, FontSize = 10, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("HH:mm:ss");
        _progress.Value = now.Second;
    }

    // IPageVisibility: stop the clock while nobody is looking.
    public void OnShown()
    {
        _shown.Text = $"Shown {++_timesShown} time{(_timesShown == 1 ? "" : "s")} since it was loaded.";
        Tick();
        _clockTimer.Start();
        _ = RefreshTerminalsAsync();
    }

    public void OnHidden() => _clockTimer.Stop();

    // Called when the page is switched off, removed or reloaded.
    public void Dispose()
    {
        _clockTimer.Stop();
        _host.SetBadge(null);
        _host.Log("Sample page closed");
    }

    // ---- small helpers --------------------------------------------------------------------------------------

    private IBrush Brush(string token) => Color.TryParse(_host.ThemeColor(token), out var c) ? new SolidColorBrush(c) : Brushes.Gray;

    private static string Pretty(JsonNode? node) =>
        node switch { null => "(no result)", JsonValue v when v.TryGetValue<string>(out var s) => s, _ => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) };

    private Border Card(string title, string? note, params Control[] content)
    {
        var column = new StackPanel { Spacing = 10 };
        column.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        if (note is not null) column.Children.Add(new TextBlock { Text = note, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        foreach (var c in content) column.Children.Add(c);
        var card = new Border { Child = column, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        _themed.Add((card, "panel"));
        return card;
    }

    private static StackPanel Column(int column, Control[] cards)
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(column == 0 ? 0 : 7, 0, column == 0 ? 7 : 0, 0) };
        foreach (var c in cards) panel.Children.Add(c);
        Grid.SetColumn(panel, column);
        return panel;
    }

    /// <summary>A row whose first control takes the room that is left when it is a text box or a slider.</summary>
    private static Control Row(params Control[] controls)
    {
        if (controls[0] is TextBox { Width: double.NaN } or Slider)
        {
            var dock = new DockPanel();
            foreach (var c in controls.Skip(1).Reverse()) { c.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(c, Dock.Right); dock.Children.Add(c); }
            dock.Children.Add(controls[0]);
            return dock;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in controls) row.Children.Add(c);
        return row;
    }

    private static T Docked<T>(Dock dock, T control) where T : Control { DockPanel.SetDock(control, dock); return control; }

    private Button Button(string text, Action onClick) => Button(text, () => { onClick(); return Task.CompletedTask; });

    /// <summary>A button whose failures become a notification instead of an unhandled exception.</summary>
    private Button Button(string text, Func<Task> onClick)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            try { await onClick(); }
            catch (Exception ex) { _host.Notify($"\"{text}\" failed", ex.Message); }
        };
        return button;
    }
}
