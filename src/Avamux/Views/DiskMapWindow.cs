using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.SystemInfo;

namespace Avamux.Views;

/// <summary>
/// The map of a disk scan: a cushion treemap where every file is drawn to scale, beside an explorer list of the
/// folder being looked at. Click to select, double-click to go into a folder (or open a file), and the bar at the
/// top goes back up. While the scan runs both fill in as it goes, under an animated progress strip with its
/// controls. Closing the window leaves the scan running.
/// </summary>
public sealed class DiskMapWindow : AppWindow
{
    private static readonly Dictionary<string, DiskMapWindow> OpenWindows = new();

    // Eight categorical hues for the biggest file types (in a fixed order, never cycled), then one for the rest.
    private static readonly uint[] Palette = [0xff2a78d6, 0xffeb6834, 0xff1baf7a, 0xffeda100, 0xffe87ba4, 0xff008300, 0xff6250d6, 0xffe34948];
    private const uint OtherFiles = 0xff9a9a9a, Folder = 0xff6c6c6c, Unread = 0xfe4a4a4a;

    private readonly DiskScan _scan;
    private DiskNode? _root;
    private DiskNode? _view;
    private DiskNode? _selected;
    private readonly Stack<DiskNode> _back = new(), _forward = new();
    private readonly TreeMapControl _map = new();
    private readonly ListBox _list = new() { Classes = { "rows" }, SelectionMode = SelectionMode.Single };
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _progress;
    private readonly Platter _platter = new();
    private readonly ScanBar _bar = new() { Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _progressText = new() { FontWeight = FontWeight.SemiBold, FontSize = 12.5 };
    private readonly TextBlock _progressPath = new() { FontSize = 11.5, Classes = { "dim" }, TextTrimming = TextTrimming.PrefixCharacterEllipsis };
    private readonly WrapPanel _legend = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 4) };
    private readonly TextBlock _status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _loading = new() { Text = "Loading the saved scan…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "dim" } };
    private readonly Button _backButton, _forwardButton, _upButton, _pause, _stop, _rescan;
    private readonly PathIcon _pauseIcon = new() { Data = Icons.Pause };
    private readonly TextBlock _pauseText = new() { Text = "Pause", VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<short, int> _slots = new();
    private List<(short Ext, long Size)> _extTotals = [];
    private Dictionary<short, uint> _colours = new();
    private readonly List<Row> _rows = [];
    private (string Column, bool Descending) _sort = ("size", true);
    private DiskScanState _lastState;
    private DateTime _lastMapRefresh, _lastColours;
    private bool _loaded;
    /// <summary>Set while the list's selection is being moved to match the map, so it does not answer back.</summary>
    private bool _syncingList;
    private readonly Action<DiskScan> _onChanged;

    public static void Open(DiskScan scan, bool start)
    {
        if (OpenWindows.TryGetValue(scan.Id, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            if (start) open.StartFresh();
            return;
        }
        var w = new DiskMapWindow(scan);
        OpenWindows[scan.Id] = w;
        w.Show();
        if (start) w.StartFresh();
    }

    private DiskMapWindow(DiskScan scan)
    {
        _scan = scan;
        Title = "Disk map — " + scan.Title;
        Icon = AppIcon.Window;
        Width = 1280; Height = 820; MinWidth = 760; MinHeight = 460;

        _backButton = Ui.IconButton(Icons.ArrowLeft, "Back (Alt+Left)", GoBack);
        _forwardButton = Ui.IconButton(Icons.ArrowRight, "Forward (Alt+Right)", GoForward);
        _upButton = Ui.IconButton(Icons.ArrowUp, "Up to the folder above (Backspace)", GoUp);
        _pause = new Button { Classes = { "flat" }, Padding = new Thickness(10, 6), VerticalAlignment = VerticalAlignment.Center, Content = Ui.Row(6, _pauseIcon, _pauseText) };
        _pause.Click += (_, _) => { if (_scan.State == DiskScanState.Running) _scan.Pause(); else _scan.Resume(); Tick(); };
        _stop = Ui.IconButton(Icons.Stop, "Stop scanning: what has been found is kept, and it can be resumed (even after avamux restarts)", () => { _scan.Stop(); Tick(); }, "Stop");
        _rescan = Ui.IconButton(Icons.Refresh, "Scan everything again (F5)", StartFresh, "Rescan");
        var toolbar = new DockPanel
        {
            Margin = new Thickness(10, 8, 10, 6),
            Children =
            {
                Ui.Row(2, _pause, _stop, _rescan).Dock(Dock.Right),
                Ui.Row(0, _backButton, _forwardButton, _upButton).Dock(Dock.Left),
                new ScrollViewer { Content = _crumbs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(10, 0) },
            },
        };

        _progress = new Border
        {
            Background = Ui.Tk("panel"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 1), Padding = new Thickness(14, 8),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Apply(_platter, p => p.Margin = new Thickness(0, 0, 14, 0)).Dock(Dock.Left),
                    new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { _progressText, _bar, _progressPath } },
                },
            },
        };

        // The explorer: column headers that sort, and the folder's contents.
        var header = new Grid { ColumnDefinitions = RowColumns(), Margin = new Thickness(0, 0, 14, 0), Height = 26 };
        foreach (var (col, label, key, right) in new[] { (1, "Name", "name", false), (2, "Size", "size", true), (3, "%", "size", false), (4, "Files", "files", true), (5, "Modified", "modified", false) })
        {
            var b = new Button
            {
                Classes = { "flat" }, Padding = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Content = new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Tk("textSoft") },
            };
            ToolTip.SetTip(b, $"Sort by {label.ToLowerInvariant()}");
            b.Click += (_, _) => { _sort = (key, _sort.Column == key ? !_sort.Descending : key != "name"); ShowList(keepSelection: true); };
            header.Children.Add(b.At(0, col));
        }
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row?.Build() ?? new Panel(), false);
        _list.SelectionChanged += (_, _) =>
        {
            if (!_syncingList && _list.SelectedItem is Row r && !ReferenceEquals(r.Node, _selected)) Select(r.Node, fromList: true);
        };
        _list.DoubleTapped += (_, e) => { if (_list.SelectedItem is Row r) Activate(r.Node); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is Row r) { e.Handled = true; Activate(r.Node); }
        };
        _list.ContextRequested += (_, e) =>
        {
            if (_list.SelectedItem is Row r) { MenuFor(r.Node).Open(_list); e.Handled = true; }
        };
        var explorer = new DockPanel
        {
            Background = Ui.Tk("bgEditor"),
            Children = { new Border { Child = header, BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Tk("panel") }.Dock(Dock.Top), _list },
        };

        _map.Picked += (node, clicks) =>
        {
            if (clicks >= 2) { var child = ChildOfView(node); if (child is { IsFolder: true }) Go(child); else if (child is not null) Activate(child); }
            else Select(node, fromList: false);
        };
        _map.Hovered += node => ShowStatus(node ?? _selected);
        _map.MenuRequested += node => { Select(node, fromList: false); MenuFor(node).Open(_map); };
        _map.Colour = ColourOf;
        var mapArea = new DockPanel
        {
            Children =
            {
                _legend.Dock(Dock.Bottom),
                new Panel { Children = { _map, _loading } },
            },
        };

        var split = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("540,4,*"),
            Children =
            {
                explorer.At(0, 0),
                new GridSplitter { Background = Ui.Tk("lineSoft"), ResizeDirection = GridResizeDirection.Columns }.At(0, 1),
                mapArea.At(0, 2),
            },
        };
        var statusBar = new Border
        {
            Background = Ui.Tk("panel"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 5), Child = _status,
        };
        Body = new DockPanel
        {
            Children = { toolbar.Dock(Dock.Top), _progress.Dock(Dock.Top), statusBar.Dock(Dock.Bottom), split },
        };

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Back && e.Source is not TextBox) { e.Handled = true; GoUp(); }
            else if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt) { e.Handled = true; GoBack(); }
            else if (e.Key == Key.Right && e.KeyModifiers == KeyModifiers.Alt) { e.Handled = true; GoForward(); }
            else if (e.Key == Key.F5) { e.Handled = true; if (!_scan.Busy) StartFresh(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.XButton1) { e.Handled = true; GoBack(); }
            else if (e.InitialPressMouseButton == MouseButton.XButton2) { e.Handled = true; GoForward(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        _onChanged = s =>
        {
            if (!ReferenceEquals(s, _scan)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (App.Services.Disks.Find(_scan.Id) is null) { Close(); return; }
                Tick();
            });
        };
        App.Services.Disks.Changed += _onChanged;
        _timer.Tick += (_, _) => Tick();
        Opened += (_, _) =>
        {
            _timer.Start();
            Task.Run(() => App.Services.Disks.Load(_scan)).ContinueWith(_ => Dispatcher.UIThread.Post(() => { _loaded = true; Tick(); }));
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            OpenWindows.Remove(_scan.Id);
            App.Services.Disks.Changed -= _onChanged;
            if (_loaded) Task.Run(() => App.Services.Disks.Release(_scan));
        };
        Tick();
    }

    private static ColumnDefinitions RowColumns() => new("26,*,72,58,66,112");

    private void StartFresh()
    {
        if (_scan.Busy) return;
        _scan.Start();
        _slots.Clear();
        Tick();
    }

    // ---- keeping up with the scan ----

    private void Tick()
    {
        var state = _scan.State;
        var root = _scan.Root;
        _loading.IsVisible = root is null;
        _loading.Text = _loaded ? (state == DiskScanState.New ? "Not scanned yet: press Rescan to start." : "This scan could not be loaded. Press Rescan to scan again.") : "Loading the saved scan…";
        if (!ReferenceEquals(root, _root))
        {
            _root = root;
            _back.Clear(); _forward.Clear();
            _view = root;
            _selected = null;
            ShowView(recolour: true);
        }
        var running = state == DiskScanState.Running;
        _progress.IsVisible = state is not DiskScanState.Done and not DiskScanState.New || root is null && !_loaded;
        _platter.Running = running || !_loaded;
        _bar.Set(_scan.Fraction, running);
        var total = _scan.VolumeUsed is > 0 and var used ? $" of {Fmt.Bytes(used)}" : "";
        _progressText.Text = state switch
        {
            DiskScanState.Running => $"Scanning… {Fmt.Bytes(_scan.Size)}{total}{(_scan.Fraction is { } f ? $" ({Fmt.Percent(f)})" : "")} · {Fmt.Count(_scan.Files)} files in {Fmt.Count(_scan.Folders)} folders · {Fmt.Span(_scan.Elapsed)}",
            DiskScanState.Paused => $"Paused · {Fmt.Bytes(_scan.Size)}{total} · {Fmt.Count(_scan.Files)} files so far · {Fmt.Count(_scan.Outstanding)} folders still to read",
            DiskScanState.Stopped => $"Stopped part way · {Fmt.Bytes(_scan.Size)}{total} · {Fmt.Count(_scan.Files)} files · Resume carries on where it stopped",
            _ => _loaded ? "" : "Loading…",
        };
        _progressPath.Text = running ? _scan.CurrentPath ?? "" : state == DiskScanState.Paused ? "Resume to carry on." : "";
        _pause.IsVisible = _scan.Busy || state == DiskScanState.Stopped;
        _pauseIcon.Data = running ? Icons.Pause : Icons.Play;
        _pauseText.Text = running ? "Pause" : "Resume";
        _stop.IsVisible = _scan.Busy;
        _rescan.IsVisible = !_scan.Busy && _loaded;
        Title = $"Disk map — {_scan.Title}{(running ? $" (scanning {(_scan.Fraction is { } p ? Fmt.Percent(p) : "…")})" : state == DiskScanState.Paused ? " (paused)" : "")}";

        var changed = state != _lastState;
        _lastState = state;
        if (root is null) return;
        if (running && DateTime.UtcNow - _lastColours > TimeSpan.FromSeconds(6) || changed) { _lastColours = DateTime.UtcNow; Recolour(final: state == DiskScanState.Done); }
        if (changed || running && DateTime.UtcNow - _lastMapRefresh > TimeSpan.FromSeconds(2))
        {
            _lastMapRefresh = DateTime.UtcNow;
            _map.Refresh();
            if (changed) ShowList(keepSelection: true); else foreach (var r in _rows) r.Update();
            ShowStatus(_selected);
            ShowCrumbs();
        }
    }

    // ---- colours ----

    private uint ColourOf(DiskNode n)
    {
        if (n.IsFolder) return n.State == DiskNodeState.Pending ? Unread : Folder;
        return _colours.TryGetValue(n.Ext, out var c) ? c : OtherFiles;
    }

    /// <summary>
    /// Totals by file type over the whole scan, off the UI thread. The biggest types get a colour each; a type keeps
    /// its colour for as long as the window is open, except once, when the scan finishes, so the final map shows
    /// the types that really are the biggest.
    /// </summary>
    private void Recolour(bool final)
    {
        if (_root is not { } root) return;
        Task.Run(() =>
        {
            var totals = new Dictionary<short, long>();
            var stack = new Stack<DiskNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.IsFolder) { foreach (var c in n.Children) stack.Push(c); continue; }
                if (n.Kind == DiskNodeKind.File) totals[n.Ext] = totals.GetValueOrDefault(n.Ext) + n.Size;
            }
            return totals.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!t.IsCompletedSuccessfully || !ReferenceEquals(root, _root)) return;
            _extTotals = t.Result;
            var top = _extTotals.Take(Palette.Length).Select(e => e.Ext).ToList();
            if (final) _slots.Clear();
            foreach (var ext in top)
            {
                if (_slots.ContainsKey(ext)) continue;
                var free = Enumerable.Range(0, Palette.Length).FirstOrDefault(i => !_slots.ContainsValue(i), -1);
                if (free < 0) break;
                _slots[ext] = free;
            }
            _colours = _slots.ToDictionary(kv => kv.Key, kv => Palette[kv.Value]);
            ShowLegend();
            _map.Refresh();
        }));
    }

    private void ShowLegend()
    {
        _legend.Children.Clear();
        var total = Math.Max(1, _extTotals.Sum(e => e.Size));
        foreach (var (ext, size) in _extTotals.Where(e => _slots.ContainsKey(e.Ext)).Take(Palette.Length))
            _legend.Children.Add(Chip(Palette[_slots[ext]], _scan.ExtensionName(ext) is { Length: > 0 } name ? "." + name : "no extension", size, total));
        var other = _extTotals.Where(e => !_slots.ContainsKey(e.Ext)).Sum(e => e.Size);
        if (other > 0) _legend.Children.Add(Chip(OtherFiles, "other files", other, total));
        _legend.Children.Add(Chip(Folder, "folders too small to open up", null, total));
        if (_scan.State != DiskScanState.Done) _legend.Children.Add(Chip(Unread, "not read yet", null, total));
    }

    private static Control Chip(uint colour, string label, long? size, long total)
    {
        var text = size is { } s ? $"{label}  {Fmt.Bytes(s)} · {Fmt.Percent(s / (double)total)}" : label;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 14, 2),
            Children =
            {
                new Border { Width = 11, Height = 11, CornerRadius = Ui.R(2), Background = new SolidColorBrush(Color.FromUInt32(colour | 0xff000000)), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Tk("textSoft") },
            },
        };
    }

    // ---- navigation ----

    private void Go(DiskNode folder, bool record = true)
    {
        if (!folder.IsFolder || ReferenceEquals(folder, _view)) return;
        if (record && _view is not null) { _back.Push(_view); _forward.Clear(); }
        var from = _view;
        _view = folder;
        // Coming back up selects the folder just left, so it is easy to see where it was.
        _selected = from is not null && from.IsUnder(folder) && !ReferenceEquals(from, folder) ? ChildOf(folder, from) : null;
        ShowView(recolour: false);
    }

    private void GoBack() { if (_back.Count > 0 && _view is not null) { _forward.Push(_view); Go(_back.Pop(), record: false); } }
    private void GoForward() { if (_forward.Count > 0 && _view is not null) { _back.Push(_view); Go(_forward.Pop(), record: false); } }
    private void GoUp() { if (_view?.Parent is { } up) Go(up); }

    private static DiskNode? ChildOf(DiskNode folder, DiskNode inside)
    {
        for (var n = inside; n is not null; n = n.Parent) if (ReferenceEquals(n.Parent, folder)) return n;
        return null;
    }

    private DiskNode? ChildOfView(DiskNode node) => _view is null ? null : ChildOf(_view, node);

    private void ShowView(bool recolour)
    {
        _map.View = _view;
        _map.Selected = _selected;
        _map.Refresh();
        ShowList(keepSelection: true);
        ShowCrumbs();
        ShowStatus(_selected);
        _backButton.IsEnabled = _back.Count > 0;
        _forwardButton.IsEnabled = _forward.Count > 0;
        _upButton.IsEnabled = _view?.Parent is not null;
        if (recolour) Recolour(final: _scan.State == DiskScanState.Done);
    }

    private void ShowCrumbs()
    {
        _crumbs.Children.Clear();
        if (_view is null) return;
        var chain = _view.Ancestry();
        for (var i = 0; i < chain.Count; i++)
        {
            var n = chain[i];
            if (i > 0) _crumbs.Children.Add(new TextBlock { Text = "›", Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) });
            var label = i == 0 ? (n.Kind == DiskNodeKind.Group ? _scan.Title : n.Name) : n.Name;
            var last = i == chain.Count - 1;
            var b = new Button
            {
                Classes = { "flat" }, Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = label, FontWeight = last ? FontWeight.SemiBold : FontWeight.Normal, FontSize = 12.5 },
            };
            ToolTip.SetTip(b, $"{n.FullPath}\n{Fmt.Bytes(n.Size)} · {Fmt.Count(n.Files)} files");
            b.Click += (_, _) => Go(n);
            _crumbs.Children.Add(b);
        }
        _crumbs.Children.Add(new TextBlock { Text = $"  {Fmt.Bytes(_view.Size)} · {Fmt.Count(_view.Files)} files", Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
    }

    private void Select(DiskNode node, bool fromList)
    {
        _selected = node;
        _map.Selected = node;
        ShowStatus(node);
        if (fromList) return;
        // The list shows the folder being looked at: select the entry the node is in.
        var child = ChildOfView(node);
        var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, child));
        _syncingList = true;
        try { _list.SelectedItem = row; }
        finally { _syncingList = false; }
        if (row is not null) _list.ScrollIntoView(row);
    }

    /// <summary>Double-click / Enter: into a folder, or open a file with its usual app.</summary>
    private void Activate(DiskNode node)
    {
        if (node.IsFolder) Go(node);
        else if (node.Kind == DiskNodeKind.File) Shell.Open(node.FullPath);
    }

    private void ShowStatus(DiskNode? n)
    {
        if (n is null) { _status.Text = _view is null ? "" : "Click a block or a row to see what it is; double-click to go in."; return; }
        var share = _view is { Size: > 0 } v && n.IsUnder(v) ? $" · {Fmt.Percent(n.Size / (double)v.Size)} of {(v.Kind == DiskNodeKind.Group ? _scan.Title : v.Name)}" : "";
        var what = n.Kind switch
        {
            DiskNodeKind.Folder or DiskNodeKind.Group => $"{Fmt.Count(n.Files)} files, {Fmt.Count(n.Folders)} folders{(n.State == DiskNodeState.Pending ? " (not read yet)" : n.State == DiskNodeState.Denied ? " (could not be read)" : "")}",
            DiskNodeKind.Link => "link (not followed)",
            DiskNodeKind.Mount => "another file system is mounted here (not included)",
            _ => "file",
        };
        var modified = n.Modified > 0 ? $" · modified {new DateTime(n.Modified, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm}" : "";
        _status.Text = $"{n.FullPath}  —  {Fmt.Bytes(n.Size)}{share} · {what}{modified}";
    }

    // ---- explorer list ----

    private void ShowList(bool keepSelection)
    {
        _rows.Clear();
        if (_view is not null)
        {
            var kids = _view.Children;
            IEnumerable<DiskNode> ordered = _sort.Column switch
            {
                "name" => kids.OrderBy(k => !k.IsFolder).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase),
                "files" => kids.OrderBy(k => k.Files),
                "modified" => kids.OrderBy(k => k.Modified),
                _ => kids.OrderBy(k => k.Size),
            };
            if (_sort.Descending) ordered = ordered.Reverse();
            foreach (var k in ordered) _rows.Add(new Row(this, k));
        }
        _syncingList = true;
        try { _list.ItemsSource = _rows.ToList(); }
        finally { _syncingList = false; }
        if (keepSelection && ChildOfView(_selected ?? _view!) is { } child && _rows.FirstOrDefault(r => ReferenceEquals(r.Node, child)) is { } row)
        {
            _syncingList = true;
            try { _list.SelectedItem = row; }
            finally { _syncingList = false; }
            _list.ScrollIntoView(row);
        }
    }

    /// <summary>One entry of the explorer list; its figures are refreshed in place while the scan runs.</summary>
    private sealed class Row(DiskMapWindow owner, DiskNode node)
    {
        public readonly DiskNode Node = node;
        private TextBlock? _size, _files, _modified;
        private UsageBar? _share;
        private PathIcon? _icon;

        public Control Build()
        {
            _icon = new PathIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var name = new TextBlock { Text = Node.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Margin = new Thickness(4, 0) };
            _size = Cell(true);
            _files = Cell(true);
            _modified = Cell(false);
            _modified.Margin = new Thickness(10, 0, 4, 0);
            _share = new UsageBar { Height = 6, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, WarnAt = 2 };
            var grid = new Grid
            {
                ColumnDefinitions = RowColumns(), Height = 24, Background = Brushes.Transparent,
                Children = { _icon.At(0, 0), name.At(0, 1), _size.At(0, 2), _share.At(0, 3), _files.At(0, 4), _modified.At(0, 5) },
            };
            ToolTip.SetTip(name, Node.Name);
            Update();
            return grid;
        }

        private static TextBlock Cell(bool right) => new()
        {
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Foreground = Ui.Tk("textSoft"), Margin = new Thickness(4, 0),
        };

        public void Update()
        {
            if (_size is null) return;
            var parent = owner._view;
            _size.Text = Node.Kind == DiskNodeKind.Mount ? "mount" : Fmt.Bytes(Node.Size);
            _share!.Value = parent is { Size: > 0 } p ? Node.Size / (double)p.Size : 0;
            ToolTip.SetTip(_share, parent is { Size: > 0 } q ? Fmt.Percent(Node.Size / (double)q.Size) + " of this folder" : null);
            _files!.Text = Node.IsFolder ? Fmt.Count(Node.Files) : "";
            _modified!.Text = Node.Modified > 0 ? new DateTime(Node.Modified, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            _icon!.Data = Node.Kind switch
            {
                DiskNodeKind.Folder or DiskNodeKind.Group => Icons.Folder, DiskNodeKind.Mount => Icons.Harddisk, DiskNodeKind.Link => Icons.OpenInNew, _ => Icons.File,
            };
            _icon.Foreground = Node.IsFolder ? Ui.Tk("accent") : Node.Kind == DiskNodeKind.File
                ? new SolidColorBrush(Color.FromUInt32(owner.ColourOf(Node) | 0xff000000)) : Ui.Tk("faint");
            _icon.Opacity = Node.State == DiskNodeState.Pending ? 0.5 : 1;
        }
    }

    // ---- actions ----

    private ContextMenu MenuFor(DiskNode n)
    {
        var path = n.FullPath;
        var menu = new ContextMenu();
        if (n.IsFolder) menu.Items.Add(Ui.Item("Go Into This Folder", () => Go(n), icon: Icons.Treemap));
        if (n.Kind == DiskNodeKind.File) menu.Items.Add(Ui.Item("Open", () => Shell.Open(path), icon: Icons.OpenInNew));
        if (n.IsFolder && n.Kind != DiskNodeKind.Group) menu.Items.Add(Ui.Item("Open in File Manager", () => Shell.Open(path), icon: Icons.FolderOpen));
        if (n.Kind != DiskNodeKind.Group) menu.Items.Add(Ui.Item("Show in File Manager", () => Shell.ShowInFolder(path), icon: Icons.FolderOpen));
        if (n.Kind != DiskNodeKind.Group)
            menu.Items.Add(Ui.Item("Open a Terminal Here", () =>
            {
                var folder = n.IsFolder ? path : Path.GetDirectoryName(path) ?? path;
                var session = App.Services.Terminals.Create(new Core.Terminal.TerminalStartOptions(Cwd: folder));
                App.Services.Host.Navigate("terminals");
                App.Services.Host.SelectTerminal(session.Id);
                App.Services.Host.Activate();
            }, icon: Icons.Console));
        menu.Items.Add(Ui.Item("Copy Path", async () => { if (Clipboard is { } c) await c.SetTextAsync(path); }));
        if (n.Kind == DiskNodeKind.Folder)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item("Scan This Folder Again", () => { _scan.Rescan(n); Tick(); }, enabled: !_scan.Busy, icon: Icons.Refresh));
        }
        return menu;
    }

    // ---- the treemap ----

    /// <summary>The treemap itself: painted off the UI thread into a bitmap, with the selection drawn over it.</summary>
    private sealed class TreeMapControl : Control
    {
        private WriteableBitmap? _bitmap;
        private TreeMapTile? _tiles;
        private double _scale = 1;
        private CancellationTokenSource? _rendering;
        private DiskNode? _selected, _hover;
        private DispatcherTimer? _resize;

        public DiskNode? View { get; set; }
        public Func<DiskNode, uint> Colour { get; set; } = _ => 0xff808080;
        public event Action<DiskNode, int>? Picked;
        public event Action<DiskNode?>? Hovered;
        public event Action<DiskNode>? MenuRequested;

        public DiskNode? Selected
        {
            get => _selected;
            set { _selected = value; InvalidateVisual(); }
        }

        public TreeMapControl()
        {
            ClipToBounds = true;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            _resize ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => { _resize!.Stop(); Refresh(); });
            _resize.Stop();
            _resize.Start();
        }

        /// <summary>Paints the map again (in the background; the old picture stays up until the new one is ready).</summary>
        public void Refresh()
        {
            var view = View;
            _scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var w = (int)(Bounds.Width * _scale);
            var h = (int)(Bounds.Height * _scale);
            _rendering?.Cancel();
            if (view is null || w < 4 || h < 4) { _tiles = null; InvalidateVisual(); return; }
            var cts = _rendering = new CancellationTokenSource();
            var colour = Colour;
            var background = Ui.Color("bgDeep").ToUInt32();
            Task.Run(() =>
            {
                var pixels = new uint[w * h];
                var tiles = TreeMap.Render(view, w, h, pixels, background, colour, cts.Token);
                return (pixels, tiles);
            }, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    var (pixels, tiles) = t.Result;
                    var size = new PixelSize(w, h);
                    if (_bitmap is null || _bitmap.PixelSize != size)
                    {
                        _bitmap?.Dispose();
                        _bitmap = new WriteableBitmap(size, new Vector(96 * _scale, 96 * _scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    }
                    using (var fb = _bitmap.Lock())
                    {
                        var ints = new int[w];
                        for (var y = 0; y < h; y++)
                        {
                            Buffer.BlockCopy(pixels, y * w * 4, ints, 0, w * 4);
                            Marshal.Copy(ints, 0, fb.Address + y * fb.RowBytes, w);
                        }
                    }
                    _tiles = tiles;
                    InvalidateVisual();
                });
            }, TaskScheduler.Default);
        }

        public override void Render(DrawingContext ctx)
        {
            var bounds = new Rect(Bounds.Size);
            ctx.FillRectangle(Ui.Tk("bgDeep"), bounds);
            if (_bitmap is null || _tiles is null) return;
            ctx.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(0, 0, _bitmap.PixelSize.Width / _scale, _bitmap.PixelSize.Height / _scale));
            if (_hover is { } hover && _tiles.Find(hover) is { } ht && !ReferenceEquals(ht, _tiles))
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.White, 0.7), 1), Rect(ht).Deflate(0.5));
            if (_selected is { } sel && _tiles.Find(sel) is { } st)
            {
                var r = Rect(st).Deflate(1);
                ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), r);
                ctx.DrawRectangle(null, new Pen(Brushes.White, 1.4), r);
            }
        }

        private Rect Rect(TreeMapTile t) => new(t.X / _scale, t.Y / _scale, Math.Max(1, t.W / _scale), Math.Max(1, t.H / _scale));

        private DiskNode? At(Point p)
        {
            if (_tiles is null) return null;
            var path = _tiles.PathTo(p.X * _scale, p.Y * _scale);
            return path.Count > 1 ? path[^1].Node : null;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var n = At(e.GetPosition(this));
            if (ReferenceEquals(n, _hover)) return;
            _hover = n;
            Hovered?.Invoke(n);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hover = null;
            Hovered?.Invoke(null);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var props = e.GetCurrentPoint(this).Properties;
            if (At(e.GetPosition(this)) is not { } n) return;
            if (props.IsLeftButtonPressed) { e.Handled = true; Picked?.Invoke(n, e.ClickCount); }
            else if (props.IsRightButtonPressed) { e.Handled = true; MenuRequested?.Invoke(n); }
        }
    }
}
