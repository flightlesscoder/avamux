using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.Logging;

namespace Avamux.Views;

/// <summary>App logs, crash reports, saved terminal history and script output, with retention usage.</summary>
public sealed class LogsPage : UserControl, IPage
{
    private const string Live = "\0live";
    private static readonly (string? Key, string Label)[] Categories =
        [(null, "All"), ("app", "App"), ("crashes", "Crashes"), ("terminals", "Terminals"), ("scripts", "Scripts")];

    private readonly ComboBox _category = new() { MinWidth = 130 };
    private readonly ListBox _files = new() { Classes = { "plain" } };
    private readonly TextBox _viewer = new() { IsReadOnly = true, AcceptsReturn = true, Classes = { "mono", "pane" }, FontSize = 12, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), Background = Ui.Brush("#11131a"), TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter lines…", Width = 220 };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _usage = new() { Foreground = Ui.Dim, FontSize = 12 };
    private readonly ProgressBar _usageBar = new() { Height = 4, MinWidth = 0, Minimum = 0, Maximum = 100 };
    private readonly Button _delete;
    private string _selected = Live;
    private string _text = "";
    private bool _building;
    private readonly Action _liveRefresh;

    private static LogStore Logs => App.Services.Logs;

    public LogsPage()
    {
        foreach (var (_, label) in Categories) _category.Items.Add(label);
        _category.SelectedIndex = 0;
        _category.SelectionChanged += (_, _) => RebuildFiles();
        _files.SelectionChanged += (_, _) =>
        {
            if (_building || (_files.SelectedItem as Control)?.Tag is not string path) return;
            _selected = path;
            LoadSelected();
        };
        _filter.TextChanged += (_, _) => ShowText();
        _delete = Ui.IconButton(Icons.Delete, "Delete this file", () => _ = DeleteSelectedAsync());
        ScrollViewer.SetHorizontalScrollBarVisibility(_viewer, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var left = new DockPanel
        {
            Children =
            {
                new Border
                {
                    Padding = new Thickness(12, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            Ui.IconButton(Icons.Delete, "Delete every file in this category", () => _ = ClearAsync()).Dock(Dock.Right),
                            Ui.Row(8, Ui.Apply(Ui.Text("Show"), t => t.VerticalAlignment = VerticalAlignment.Center), _category),
                        },
                    },
                }.Dock(Dock.Top),
                new Border
                {
                    Padding = new Thickness(12, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0),
                    Child = Ui.Column(5, _usage, _usageBar),
                }.Dock(Dock.Bottom),
                _files,
            },
        };
        var right = new DockPanel
        {
            Children =
            {
                new Border
                {
                    Padding = new Thickness(12, 6), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            Ui.Row(4, _filter,
                                Ui.IconButton(Icons.Refresh, "Reload", LoadSelected),
                                Ui.IconButton(Icons.OpenInNew, "Open with the default application", () => { if (_selected != Live) Shell.Open(_selected); }),
                                _delete).Dock(Dock.Right),
                            _title,
                        },
                    },
                }.Dock(Dock.Top),
                _viewer,
            },
        };
        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("Logs", "App logs, crash reports, history of ended terminals and script output.",
                    Ui.IconButton(Icons.FolderOpen, "Open the logs folder", () => Shell.Open(App.Services.Paths.LogsDir), "Folder"),
                    Ui.IconButton(Icons.Delete, "Apply the retention limits now", () => { var n = Logs.ApplyRetention(); Ui.Toast(this, "Retention applied", $"Removed {n} file(s)."); RebuildFiles(); }, "Prune")).Dock(Dock.Top),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("360,*"),
                    Children =
                    {
                        new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 1, 0), Child = left },
                        new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = right }.At(0, 1),
                    },
                },
            },
        };

        Logs.Changed += Ui.Debounced(RebuildFiles, 200);
        App.Services.Terminals.Closed += _ => Dispatcher.UIThread.Post(async () => { await Task.Delay(800); RebuildFiles(); });
        _liveRefresh = Ui.Debounced(() => { if (IsVisible && _selected == Live) LoadSelected(); }, 400);
        Log.Written += _ => _liveRefresh();
        RebuildFiles();
        LoadSelected();
    }

    public void OnShown() { RebuildFiles(); LoadSelected(); }

    /// <summary>Shows a log file, optionally filtered to the lines containing some text (used by Search).</summary>
    public void OpenFile(string path, string? filter = null)
    {
        _category.SelectedIndex = 0;
        _selected = path;
        _filter.Text = filter ?? "";
        RebuildFiles();
        LoadSelected();
        (_files.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == path))?.BringIntoView();
    }

    private string? Category => Categories[Math.Max(0, _category.SelectedIndex)].Key;

    private void RebuildFiles()
    {
        var files = Logs.List(Category);
        if (_selected != Live && files.All(f => f.Path != _selected) && Logs.List().All(f => f.Path != _selected)) _selected = Live;
        _building = true;
        try
        {
            _files.Items.Clear();
            _files.Items.Add(new ListBoxItem
            {
                Tag = Live, IsSelected = _selected == Live,
                Content = Ui.Column(1, new TextBlock { Text = "Live app log", FontWeight = FontWeight.SemiBold }, Ui.Text("Most recent lines, updating as they are written", "dim", 11.5)),
            });
            foreach (var f in files)
            {
                _files.Items.Add(new ListBoxItem
                {
                    Tag = f.Path, IsSelected = f.Path == _selected,
                    Content = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("12,*"),
                        Children =
                        {
                            Ui.Apply(Ui.Dot(f.Category switch { "crashes" => "error", "terminals" => "ok", "scripts" => "warn", _ => "info" }, 7), d => d.HorizontalAlignment = HorizontalAlignment.Left),
                            Ui.Column(1,
                                new TextBlock { Text = f.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis },
                                Ui.Text($"{f.Category} · {Ui.Size(f.Size)} · {f.Modified:yyyy-MM-dd HH:mm}", "dim", 11)).At(0, 1),
                        },
                    },
                });
            }
        }
        finally { _building = false; }

        var s = App.Services.Config.Settings.Logs;
        var all = Logs.List();
        var total = all.Sum(f => f.Size);
        _usage.Text = $"{Ui.Size(total)} of {s.MaxTotalSizeMb} MB · {all.Count} of {(s.MaxFileCount > 0 ? s.MaxFileCount.ToString() : "∞")} files · kept {(s.MaxAgeDays > 0 ? s.MaxAgeDays + " days" : "forever")}";
        _usageBar.Value = s.MaxTotalSizeMb > 0 ? Math.Min(100, total * 100.0 / (s.MaxTotalSizeMb * 1048576.0)) : 0;
    }

    private void LoadSelected()
    {
        _delete.IsEnabled = _selected != Live;
        if (_selected == Live)
        {
            _title.Text = "Live app log";
            _text = string.Join('\n', Log.Recent().TakeLast(1000));
        }
        else
        {
            _title.Text = Path.GetFileName(_selected);
            try
            {
                // Very large files are shown from the end: the viewer is for reading, the file stays on disk.
                var text = Logs.Read(_selected);
                const int max = 1_500_000;
                _text = text.Length > max ? $"… showing the last {max / 1000} KB of {Ui.Size(new FileInfo(_selected).Length)} — open the file for the rest …\n" + text[^max..] : text;
            }
            catch (Exception ex) { _text = "Could not read the file: " + ex.Message; }
        }
        ShowText();
    }

    private void ShowText()
    {
        var filter = _filter.Text;
        _viewer.Text = string.IsNullOrEmpty(filter) ? _text
            : string.Join('\n', _text.Split('\n').Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        _viewer.CaretIndex = _viewer.Text?.Length ?? 0;
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selected == Live) return;
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Delete log", $"Delete {Path.GetFileName(_selected)}?")) return;
        Ui.Try(this, () => Logs.Delete(_selected));
        _selected = Live;
        RebuildFiles();
        LoadSelected();
    }

    private async Task ClearAsync()
    {
        var (key, label) = Categories[Math.Max(0, _category.SelectedIndex)];
        var count = Logs.List(key).Count;
        if (count == 0) return;
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Delete logs", key is null ? $"Delete all {count} log files?" : $"Delete all {count} files in \"{label}\"?")) return;
        Logs.Clear(key);
        _selected = Live;
        RebuildFiles();
        LoadSelected();
    }
}
