using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.Watching;

namespace Avamux.Views;

/// <summary>Watches on the left, the selected watch's result, link and actions on the right.</summary>
public sealed class WatchingPage : UserControl, IPage
{
    private readonly ListBox _list = new() { Classes = { "plain" } };
    private readonly ScrollViewer _detail = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? _selectedId;
    private bool _building;
    private string? _editingLinkFor;

    private static WatchService Watches => App.Services.Watches;

    public WatchingPage()
    {
        _list.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            _selectedId = (_list.SelectedItem as Control)?.Tag as string;
            _editingLinkFor = null;
            BuildDetail();
        };
        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("Watching", "Pull requests, work items, pipelines, or anything a script or plugin can check.",
                    Ui.IconButton(Icons.Refresh, "Check every enabled watch now", () => _ = Ui.TryAsync(this, () => Watches.CheckAllAsync()), "Check all"),
                    Ui.IconButton(Icons.Plus, "Add a watch", () => _ = EditAsync(null), "Add watch")).Dock(Dock.Top),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("380,*"),
                    Children =
                    {
                        new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 1, 0), Child = _list },
                        new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = _detail }.At(0, 1),
                    },
                },
            },
        };
        Watches.Changed += Ui.Debounced(Rebuild);
        _clock.Tick += (_, _) => { if (IsVisible) Rebuild(); };
        _clock.Start();
        Rebuild();
    }

    public void OnShown() => Rebuild();

    /// <summary>Shows one watch (used by Search and by the Repositories page).</summary>
    public void Select(string id)
    {
        _selectedId = id;
        _editingLinkFor = null;
        Rebuild();
    }

    private void Rebuild()
    {
        var items = Watches.Items;
        if (_selectedId is null || items.All(i => i.Id != _selectedId)) _selectedId = items.FirstOrDefault()?.Id;
        _building = true;
        try
        {
            _list.Items.Clear();
            foreach (var w in items)
            {
                var interval = w.IntervalMinutes ?? App.Services.Config.Settings.Watching.DefaultIntervalMinutes;
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("18,*"), Opacity = w.Enabled ? 1 : 0.55,
                    Children =
                    {
                        Ui.Apply(Ui.Dot(w.Status), d => { d.VerticalAlignment = VerticalAlignment.Top; d.Margin = new Thickness(0, 5, 0, 0); d.HorizontalAlignment = HorizontalAlignment.Left; }),
                        new StackPanel
                        {
                            Spacing = 1,
                            Children =
                            {
                                new DockPanel
                                {
                                    Children =
                                    {
                                        new TextBlock { Text = w.Kind, Foreground = Ui.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }.Dock(Dock.Right),
                                        new TextBlock { Text = w.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                                    },
                                },
                                new TextBlock { Text = w.Summary, Foreground = Ui.Brush("#b8c0d0"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap },
                                new TextBlock { Text = w.Enabled ? $"checked {Ui.Ago(w.LastChecked)} · every {Core.Events.RecurrenceCalculator.FormatMinutes(interval)}" : "disabled", Foreground = Ui.Dim, FontSize = 11 },
                            },
                        }.At(0, 1),
                    },
                };
                _list.Items.Add(new ListBoxItem { Tag = w.Id, Content = row, IsSelected = w.Id == _selectedId });
            }
        }
        finally { _building = false; }
        if (_editingLinkFor is null) BuildDetail();
    }

    private void BuildDetail()
    {
        var w = _selectedId is null ? null : Watches.Find(_selectedId);
        if (w is null)
        {
            _detail.Content = new StackPanel
            {
                Margin = new Thickness(28), Spacing = 10, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    Ui.Text("Nothing is being watched yet", "h2"),
                    Ui.Text("Add a watch to keep an eye on GitHub pull requests, Azure DevOps pull requests, work items and pipelines — " +
                            "or anything else through a script or a .NET plugin.", "dim"),
                    Ui.Text("A script watch runs any command and reads JSON from its output:", "dim"),
                    new SelectableTextBlock
                    {
                        FontFamily = Ui.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Text = "{\"status\": \"ok|info|warn|error\", \"summary\": \"…\", \"link\": \"https://…\",\n \"items\": [{\"title\": \"…\", \"status\": \"…\", \"detail\": \"…\", \"link\": \"https://…\"}]}",
                    },
                    Ui.Text("Plain text works too: the first line becomes the summary and the exit code the status (0 ok, 1 warn, other error).", "dim"),
                    Ui.Apply(Ui.TextButton("Add watch", () => _ = EditAsync(null)), b => b.HorizontalAlignment = HorizontalAlignment.Left),
                },
            };
            return;
        }

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(22, 16, 22, 28) };
        body.Children.Add(new DockPanel
        {
            Children =
            {
                Ui.Row(4,
                    Ui.IconButton(Icons.Refresh, "Check now", () => _ = Ui.TryAsync(this, () => Watches.CheckAsync(w)), "Check now"),
                    Ui.IconButton(Icons.Pencil, "Edit this watch", () => _ = EditAsync(w), "Edit"),
                    Ui.TextButton(w.Enabled ? "Disable" : "Enable", () => { w.Enabled = !w.Enabled; Watches.Save(); }),
                    Ui.IconButton(Icons.Delete, "Delete this watch", () => _ = DeleteAsync(w))).Dock(Dock.Right),
                Ui.Row(10, Ui.Dot(w.Status, 12), Ui.Apply(Ui.Text(w.Name, "h1"), t => t.TextWrapping = TextWrapping.NoWrap)),
            },
        });
        body.Children.Add(new SelectableTextBlock { Text = w.Summary, TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = w.LastError is null ? Ui.Strong : Ui.Brush("#e06c75") });
        body.Children.Add(Ui.Text($"{w.Kind} · last checked {Ui.Ago(w.LastChecked)} · last changed {Ui.Ago(w.LastChanged)}", "dim", 12));

        // Link: clickable, and editable in place.
        var linkBox = new TextBox { Text = w.Link ?? "", PlaceholderText = w.LastResult?.Link is { Length: > 0 } auto ? $"From the last check: {auto}" : "https://…" };
        void SaveLink()
        {
            var text = (linkBox.Text ?? "").Trim();
            _editingLinkFor = null;
            if (text == (w.Link ?? "")) return;
            w.Link = text.Length == 0 ? null : text;
            Watches.Save();
        }
        linkBox.GotFocus += (_, _) => _editingLinkFor = w.Id;
        linkBox.LostFocus += (_, _) => SaveLink();
        linkBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SaveLink(); BuildDetail(); } };
        var linkPanel = new StackPanel { Spacing = 6, Children = { Ui.Text("Link", "h2") } };
        if (w.EffectiveLink is { Length: > 0 } link)
            linkPanel.Children.Add(Ui.Apply(Ui.Link(link, () => Shell.Open(link), "Open in the browser"), b => b.HorizontalAlignment = HorizontalAlignment.Left));
        linkPanel.Children.Add(new DockPanel
        {
            Children =
            {
                Ui.Apply(Ui.IconButton(Icons.OpenInNew, "Open the link in the browser", () => { if (w.EffectiveLink is { Length: > 0 } l) Shell.Open(l); }), b => b.IsEnabled = w.EffectiveLink is { Length: > 0 }).Dock(Dock.Right),
                linkBox,
            },
        });
        linkPanel.Children.Add(Ui.Text("Leave empty to use the link reported by the check. Press Enter to save.", "dim", 11.5));
        body.Children.Add(Ui.Card(linkPanel));

        if (w.LastResult?.Items is { Count: > 0 } items && w.LastError is null)
        {
            var list = new StackPanel { Spacing = 10, Children = { Ui.Text($"Items ({items.Count})", "h2") } };
            foreach (var item in items)
            {
                Control title = item.Link is { Length: > 0 } itemLink
                    ? Ui.Apply(Ui.Link(item.Title, () => Shell.Open(itemLink), itemLink), b => b.HorizontalAlignment = HorizontalAlignment.Left)
                    : new SelectableTextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap };
                var line = new StackPanel { Spacing = 1, Children = { title } };
                var sub = string.Join(" · ", new[] { item.Status, item.Detail }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (sub.Length > 0) line.Children.Add(new TextBlock { Text = sub, Foreground = Ui.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                list.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("18,*"),
                    Children =
                    {
                        Ui.Apply(Ui.Dot(ItemStatus(item.Status), 8), d => { d.VerticalAlignment = VerticalAlignment.Top; d.Margin = new Thickness(0, 6, 0, 0); d.HorizontalAlignment = HorizontalAlignment.Left; }),
                        line.At(0, 1),
                    },
                });
            }
            body.Children.Add(Ui.Card(list));
        }

        var config = new StackPanel { Spacing = 4, Children = { Ui.Text("Configuration", "h2") } };
        var interval = w.IntervalMinutes is { } m ? $"{m:0.##} min" : $"default ({App.Services.Config.Settings.Watching.DefaultIntervalMinutes:0.##} min)";
        foreach (var (k, v) in new[] { ("kind", w.Kind), ("interval", interval) }.Concat(w.Settings.Select(s => (s.Key, Mask(s.Key, s.Value)))))
            config.Children.Add(new SelectableTextBlock { Text = $"{k}: {v}", FontFamily = Ui.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("#b8c0d0") });
        body.Children.Add(Ui.Card(config));
        _detail.Content = body;
    }

    private static string Mask(string key, string value) =>
        key.Equals("token", StringComparison.OrdinalIgnoreCase) || key.Equals("pat", StringComparison.OrdinalIgnoreCase) ? new string('•', Math.Min(12, value.Length)) : value;

    /// <summary>Item statuses are free text ("open", "succeeded", "draft"); map the ones we recognise to a colour.</summary>
    private static string ItemStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" => "unknown",
        "ok" or "success" or "succeeded" or "passed" or "merged" or "completed" or "done" or "closed" or "approved" => "ok",
        "warn" or "warning" or "partiallysucceeded" or "abandoned" or "blocked" => "warn",
        "error" or "fail" or "failed" or "failure" or "canceled" or "cancelled" => "error",
        "draft" => "unknown",
        _ => "info",
    };

    private async Task DeleteAsync(WatchItem w)
    {
        if (await Ui.ConfirmAsync(Ui.Owner(this), "Delete watch", $"Delete the watch \"{w.Name}\"?"))
            Ui.Try(this, () => Watches.Remove(w.Id));
    }

    private async Task EditAsync(WatchItem? existing)
    {
        var name = new TextBox { Text = existing?.Name ?? "" };
        var kind = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var k in Watches.Kinds) kind.Items.Add(k);
        var description = new TextBlock { Foreground = Ui.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var interval = new TextBox { Text = existing?.IntervalMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", PlaceholderText = $"default ({App.Services.Config.Settings.Watching.DefaultIntervalMinutes:0.##})", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var link = new TextBox { Text = existing?.Link ?? "", PlaceholderText = "Optional. Otherwise the link reported by the check is used." };
        var settingsPanel = new StackPanel { Spacing = 10 };
        var other = new TextBox { AcceptsReturn = true, MinHeight = 54, Classes = { "mono" }, PlaceholderText = "key=value, one per line" };
        var values = new Dictionary<string, string>(existing?.Settings ?? new(), StringComparer.OrdinalIgnoreCase);
        var boxes = new Dictionary<string, TextBox>();

        void Collect()
        {
            foreach (var (k, b) in boxes)
                if (string.IsNullOrWhiteSpace(b.Text)) values.Remove(k); else values[k] = b.Text!.Trim();
        }

        void BuildSettings()
        {
            Collect();
            boxes.Clear();
            settingsPanel.Children.Clear();
            var k = kind.SelectedItem as string ?? "command";
            IReadOnlyDictionary<string, string> known = k == "plugin"
                ? new Dictionary<string, string> { ["assembly"] = "Path to a .NET DLL implementing Avamux.Plugins.IWatchProvider (relative paths are inside the plugins folder).", ["type"] = "Provider kind or class name, when the DLL has several." }
                : Watches.Providers.FirstOrDefault(p => p.Kind == k)?.Settings ?? new Dictionary<string, string>();
            description.Text = k == "plugin" ? "Load a provider from a .NET DLL. Any extra settings the plugin needs go under \"Other settings\"."
                : Watches.Providers.FirstOrDefault(p => p.Kind == k)?.Description ?? "";
            foreach (var (key, help) in known)
            {
                var box = new TextBox { Text = values.GetValueOrDefault(key, "") };
                if (key is "command" or "wiql") { box.AcceptsReturn = key == "wiql"; box.Classes.Add("mono"); box.TextWrapping = TextWrapping.Wrap; }
                boxes[key] = box;
                settingsPanel.Children.Add(Ui.Field(key, box, help));
            }
            other.Text = string.Join('\n', values.Where(v => !known.Keys.Contains(v.Key, StringComparer.OrdinalIgnoreCase)).Select(v => $"{v.Key}={v.Value}"));
        }

        kind.SelectedItem = Watches.Kinds.FirstOrDefault(k => string.Equals(k, existing?.Kind ?? "command", StringComparison.OrdinalIgnoreCase)) ?? "command";
        BuildSettings();
        kind.SelectionChanged += (_, _) =>
        {
            // Keep whatever was typed under "Other settings" when switching kinds.
            foreach (var line in (other.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (line.IndexOf('=') is > 0 and var eq) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            BuildSettings();
        };

        var body = Ui.Column(12,
            Ui.Field("Name", name),
            Ui.Field("Kind", kind),
            description,
            settingsPanel,
            Ui.Field("Other settings", other, "Passed to the provider as-is. Script watches receive them as AVAMUX_WATCH_<KEY> environment variables."),
            Ui.Field("Minutes between checks", interval, "Empty uses the default from Settings."),
            Ui.Field("Link", link));

        WatchItem? saved = null;
        var ok = await Ui.FormAsync(Ui.Owner(this), existing is null ? "Add watch" : "Edit watch", body, () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return Task.FromResult<string?>("Give the watch a name.");
            double? minutes = null;
            if (!string.IsNullOrWhiteSpace(interval.Text))
            {
                if (!double.TryParse(interval.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                    return Task.FromResult<string?>("Minutes between checks must be a positive number.");
                minutes = parsed;
            }
            Collect();
            var settings = new Dictionary<string, string>();
            foreach (var key in boxes.Keys)
                if (values.TryGetValue(key, out var v)) settings[key] = v;
            foreach (var line in (other.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) return Task.FromResult<string?>($"Other settings: expected key=value, got \"{line.Trim()}\".");
                if (line[(eq + 1)..].Trim().Length > 0) settings[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            var item = existing ?? new WatchItem();
            item.Name = name.Text!.Trim();
            item.Kind = kind.SelectedItem as string ?? "command";
            item.IntervalMinutes = minutes;
            item.Link = string.IsNullOrWhiteSpace(link.Text) ? null : link.Text.Trim();
            item.Settings = settings;
            if (existing is null) Watches.Add(item); else Watches.Save();
            saved = item;
            return Task.FromResult<string?>(null);
        }, existing is null ? "Add" : "Save", 580);
        if (!ok || saved is null) return;
        _selectedId = saved.Id;
        Rebuild();
        if (saved.Enabled) _ = Watches.CheckAsync(saved);
    }
}
