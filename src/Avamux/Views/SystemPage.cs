using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avamux.Core.SystemInfo;

namespace Avamux.Views;

/// <summary>
/// System: what the machine is doing (CPU, memory, GPUs, network, disks, batteries) and how full each volume is,
/// with a map of what takes the space on any of them. Readings are taken only while the page is on screen; disk
/// scans carry on regardless (they belong to the app, not the page).
/// </summary>
public sealed class SystemPage : UserControl, IPage
{
    public const int HistoryLength = 60;

    private readonly SystemMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _subtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "dim" } };
    private readonly WrapPanel _metrics = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _volumes = new() { Spacing = 8 };
    private readonly StackPanel _folders = new() { Spacing = 8 };
    private readonly TextBlock _foldersTitle = new() { Text = "Folder scans", Classes = { "h2" }, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Dictionary<string, List<double>> _history = new();
    private readonly Dictionary<string, Metric> _cards = new();
    private readonly Dictionary<string, ScanRow> _rows = new();
    private string _cardSignature = "";
    private string _volumeSignature = "";
    private List<Volume> _volumeList = [];
    private bool _sampling, _listing;
    private DateTime _volumesAt = DateTime.MinValue;
    private SystemSnapshot? _last;

    private static DiskScanService Disks => App.Services.Disks;

    public SystemPage()
    {
        var scanFolder = Ui.IconButton(Icons.FolderOpen, "Map the space taken in any folder", () => _ = ScanFolderAsync(), "Scan a Folder…");
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 14, 16, 10),
            Children = { new StackPanel { Children = { Ui.Text("System", "h1"), _subtitle } }, Ui.Row(4, scanFolder).At(0, 1) },
        };
        var body = new StackPanel
        {
            Spacing = 10, Margin = new Thickness(20, 0, 20, 24),
            Children =
            {
                _metrics,
                Ui.Apply(Ui.Text("Volumes", "h2"), t => t.Margin = new Thickness(0, 6, 0, 0)),
                _volumes,
                _foldersTitle,
                _folders,
            },
        };
        Content = new DockPanel { Children = { header.Dock(Dock.Top), new ScrollViewer { Content = body } } };

        _timer.Tick += (_, _) => Tick();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Disks.Changed += _ => Dispatcher.UIThread.Post(SyncScans);
        SyncScans();
    }

    public void OnShown()
    {
        _volumesAt = DateTime.MinValue;
        Tick();
    }

    /// <summary>On screen: the page and its window can be seen, and the boss screen is not up.</summary>
    private bool Showing => IsEffectivelyVisible && TopLevel.GetTopLevel(this) is MainWindow { WindowState: not WindowState.Minimized, BossShowing: false };

    private void Tick()
    {
        if (!Showing) return;
        foreach (var row in _rows.Values) row.Update();
        if (!_sampling)
        {
            _sampling = true;
            Task.Run(_monitor.Sample).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                _sampling = false;
                if (t.IsCompletedSuccessfully) Show(t.Result);
            }));
        }
        if (!_listing && DateTime.UtcNow - _volumesAt > TimeSpan.FromSeconds(10))
        {
            _listing = true;
            Task.Run(Volumes.List).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                _listing = false;
                _volumesAt = DateTime.UtcNow;
                if (t.IsCompletedSuccessfully) ShowVolumes(t.Result);
            }));
        }
    }

    // ---- live figures ----

    private sealed class Metric
    {
        public readonly Border Card;
        public readonly TextBlock Big = new() { FontSize = 22, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        public readonly TextBlock Name = new() { FontSize = 11.5, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis };
        public readonly TextBlock Detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        public readonly UsageBar Bar = new() { IsVisible = false, Margin = new Thickness(0, 2) };
        public readonly Sparkline Spark = new();
        public readonly StackPanel Extra = new() { Spacing = 2 };

        public Metric(string title, Geometry icon)
        {
            Card = new Border
            {
                Classes = { "card" }, Width = 292, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 12),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        Ui.Row(7, new PathIcon { Data = icon, Width = 14, Height = 14, Foreground = Ui.Tk("accent") },
                            Ui.Apply(Ui.Text(title, size: 12), t => { t.FontWeight = FontWeight.SemiBold; t.VerticalAlignment = VerticalAlignment.Center; })),
                        Big, Name, Bar, Detail, Spark, Extra,
                    },
                },
            };
            ToolTip.SetTip(Name, null);
        }
    }

    private double[] Push(string key, double value)
    {
        if (!_history.TryGetValue(key, out var list)) _history[key] = list = [];
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
        return list.ToArray();
    }

    private Metric Card(string key, string title, Geometry icon)
    {
        if (_cards.TryGetValue(key, out var card)) return card;
        return _cards[key] = new Metric(title, icon);
    }

    private void Show(SystemSnapshot s)
    {
        _last = s;
        var up = s.Uptime is { } u ? $" · up {Fmt.Span(u)}" : "";
        _subtitle.Text = $"{s.Host} · {s.Os}{up}";

        var keys = new List<string>();
        var accent = Ui.Tk("accent");
        if (s.Cpu is { } cpu)
        {
            keys.Add("cpu");
            var m = Card("cpu", "CPU", Icons.Chip);
            m.Big.Text = Fmt.Percent(cpu.Usage);
            m.Name.Text = cpu.Model;
            ToolTip.SetTip(m.Name, cpu.Model);
            var bits = new List<string> { $"{cpu.Cores} threads" };
            if (cpu.MHz is { } mhz) bits.Add(mhz >= 1000 ? $"{mhz / 1000:0.0} GHz" : $"{mhz:0} MHz");
            if (cpu.TempC is { } c) bits.Add($"{c:0} °C");
            if (s.Load is { Length: 3 } l) bits.Add($"load {l[0]:0.00} {l[1]:0.00} {l[2]:0.00}");
            m.Detail.Text = string.Join(" · ", bits);
            m.Spark.Set((Push("cpu", cpu.Usage), accent));
            if (cpu.PerCore.Length > 1)
            {
                if (m.Extra.Children.Count == 0) m.Extra.Children.Add(new CoreBars());
                ((CoreBars)m.Extra.Children[0]).Values = cpu.PerCore;
            }
        }
        if (s.Memory is { Total: > 0 } mem)
        {
            keys.Add("mem");
            var m = Card("mem", "Memory", Icons.ViewDashboard);
            m.Big.Text = $"{Fmt.Bytes(mem.Used)} of {Fmt.Bytes(mem.Total)}";
            m.Name.Text = $"{Fmt.Percent(mem.Used / (double)mem.Total)} in use · {Fmt.Bytes(mem.Available)} available";
            m.Bar.IsVisible = true;
            m.Bar.Value = mem.Used / (double)mem.Total;
            m.Detail.Text = mem.SwapTotal > 0 ? $"Swap {Fmt.Bytes(mem.SwapUsed)} of {Fmt.Bytes(mem.SwapTotal)}" : "No swap";
            m.Spark.Set((Push("mem", mem.Used / (double)mem.Total), accent));
        }
        for (var i = 0; i < s.Gpus.Count; i++)
        {
            var gpu = s.Gpus[i];
            var key = $"gpu{i}";
            keys.Add(key);
            var m = Card(key, s.Gpus.Count > 1 ? $"GPU {i + 1}" : "GPU", Icons.Server);
            m.Big.Text = gpu.Usage is { } busy ? Fmt.Percent(busy) : "—";
            m.Name.Text = gpu.Name;
            ToolTip.SetTip(m.Name, gpu.Name);
            if (gpu.VramTotal is > 0 and var total && gpu.VramUsed is { } used)
            {
                m.Bar.IsVisible = true;
                m.Bar.Value = used / (double)total;
            }
            var bits = new List<string>();
            if (gpu.VramTotal is > 0 and var t && gpu.VramUsed is { } u2) bits.Add($"VRAM {Fmt.Bytes(u2)} of {Fmt.Bytes(t)}");
            if (gpu.TempC is { } c) bits.Add($"{c:0} °C");
            if (gpu.PowerW is { } w) bits.Add($"{w:0} W");
            m.Detail.Text = bits.Count > 0 ? string.Join(" · ", bits) : "No usage figures for this GPU";
            if (gpu.Usage is { } usage) m.Spark.Set((Push(key, usage), accent));
            m.Spark.IsVisible = gpu.Usage is not null;
        }
        if (s.Network is { } net)
        {
            keys.Add("net");
            var m = Card("net", "Network", Icons.ArrowRightCircle);
            m.Big.Text = $"↓ {Fmt.Rate(net.InPerSec)}";
            m.Name.Text = $"↑ {Fmt.Rate(net.OutPerSec)} sent";
            m.Detail.Text = "Received (filled) and sent, last two minutes";
            m.Spark.Max = null;
            m.Spark.Set((Push("netin", net.InPerSec), accent), (Push("netout", net.OutPerSec), Ui.Tk("textSoft")));
        }
        if (s.DiskIo is { } io)
        {
            keys.Add("io");
            var m = Card("io", "Disk activity", Icons.Harddisk);
            m.Big.Text = $"{Fmt.Rate(io.InPerSec + io.OutPerSec)}";
            m.Name.Text = $"Reading {Fmt.Rate(io.InPerSec)} · writing {Fmt.Rate(io.OutPerSec)}";
            m.Detail.Text = "Reads (filled) and writes, all disks";
            m.Spark.Max = null;
            m.Spark.Set((Push("ioread", io.InPerSec), accent), (Push("iowrite", io.OutPerSec), Ui.Tk("textSoft")));
        }
        if (s.Batteries.Count > 0)
        {
            // A wireless device that is switched off reports 0% and an unknown status.
            keys.Add("bat");
            var m = Card("bat", s.Batteries.Any(b => !b.Device) ? "Battery" : "Device batteries", Icons.CheckCircle);
            var main = s.Batteries.FirstOrDefault(b => !b.Device) ?? s.Batteries[0];
            m.Big.Text = $"{main.Percent}%";
            m.Name.Text = $"{main.Name}{(main.Status.Length > 0 ? " · " + main.Status : "")}";
            m.Bar.IsVisible = true;
            m.Bar.WarnAt = 2;
            m.Bar.Value = main.Percent / 100.0;
            m.Detail.Text = string.Join("\n", s.Batteries.Where(b => b != main).Select(b => $"{b.Name}: {b.Percent}%{(b.Status is { Length: > 0 } st && st != "Unknown" ? " · " + st : "")}"));
            m.Detail.IsVisible = m.Detail.Text.Length > 0;
            m.Spark.IsVisible = false;
        }
        var signature = string.Join(",", keys);
        if (signature != _cardSignature)
        {
            _cardSignature = signature;
            _metrics.Children.Clear();
            foreach (var k in keys) _metrics.Children.Add(_cards[k].Card);
            if (keys.Count == 0) _metrics.Children.Add(Ui.Text("No system figures could be read on this computer.", "dim"));
        }
    }

    /// <summary>One thin bar per CPU thread.</summary>
    private sealed class CoreBars : Control
    {
        private double[] _values = [];
        public CoreBars() { Height = 22; Margin = new Thickness(0, 4, 0, 0); }
        public double[] Values { get => _values; set { _values = value; InvalidateVisual(); } }

        public override void Render(DrawingContext ctx)
        {
            if (_values.Length == 0) return;
            var w = Bounds.Width / _values.Length;
            var h = Bounds.Height;
            for (var i = 0; i < _values.Length; i++)
            {
                var r = new Rect(i * w + 0.5, 0, Math.Max(1, w - 1.5), h);
                ctx.FillRectangle(Ui.Tk("bgDeep"), r);
                var v = Math.Clamp(_values[i], 0, 1);
                ctx.FillRectangle(Ui.Tk("accent"), new Rect(r.X, h - v * h, r.Width, v * h));
            }
        }
    }

    // ---- volumes and scans ----

    private void ShowVolumes(List<Volume> list)
    {
        _volumeList = list;
        var signature = string.Join("|", list.Select(v => v.Key));
        if (signature != _volumeSignature)
        {
            _volumeSignature = signature;
            _volumes.Children.Clear();
            foreach (var key in _rows.Keys.Where(k => k.StartsWith("vol:", StringComparison.Ordinal)).ToList()) _rows.Remove(key);
            foreach (var v in list)
            {
                var row = new ScanRow(this, v, null);
                _rows["vol:" + v.Key] = row;
                _volumes.Children.Add(row.Card);
            }
            if (list.Count == 0) _volumes.Children.Add(Ui.Text("No volumes could be read.", "dim"));
        }
        foreach (var v in list) if (_rows.TryGetValue("vol:" + v.Key, out var row)) row.SetVolume(v);
    }

    /// <summary>Brings the folder scans list (and every row's scan status) up to date.</summary>
    private void SyncScans()
    {
        var folders = Disks.Scans.Where(s => s.VolumeKey is null).OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = folders.Select(s => "scan:" + s.Id).ToHashSet();
        foreach (var key in _rows.Keys.Where(k => k.StartsWith("scan:", StringComparison.Ordinal) && !wanted.Contains(k)).ToList()) _rows.Remove(key);
        if (_folders.Children.Count != folders.Count || folders.Any(s => !_rows.ContainsKey("scan:" + s.Id)))
        {
            _folders.Children.Clear();
            foreach (var s in folders)
            {
                if (!_rows.TryGetValue("scan:" + s.Id, out var row)) _rows["scan:" + s.Id] = row = new ScanRow(this, null, s);
                _folders.Children.Add(row.Card);
            }
        }
        _foldersTitle.IsVisible = folders.Count > 0;
        foreach (var row in _rows.Values) row.Update();
    }

    private async Task ScanFolderAsync()
    {
        var picked = await Ui.Owner(this).StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Map the space taken in a folder", AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        var scan = Disks.ForFolder(path);
        DiskMapWindow.Open(scan, start: scan.State == DiskScanState.New);
        SyncScans();
    }

    /// <summary>What a scan is up to, in a few words.</summary>
    public static string Describe(DiskScan s)
    {
        var pct = s.Fraction is { } f && s.State != DiskScanState.Done ? $"{Fmt.Percent(f)} · " : "";
        var counts = $"{Fmt.Count(s.Files)} files · {Fmt.Bytes(s.Size)}";
        return s.State switch
        {
            DiskScanState.New => "Not scanned yet",
            DiskScanState.Running => $"Scanning · {pct}{counts} · {Fmt.Span(s.Elapsed)}",
            DiskScanState.Paused => $"Paused · {pct}{counts}",
            DiskScanState.Stopped => $"Stopped part way · {pct}{counts} · resume to carry on",
            _ => $"Scanned {Ui.Ago(s.Finished)} · {counts} · took {Fmt.Span(s.Elapsed)}",
        };
    }

    /// <summary>A volume (with its scan, if it has one) or a folder scan: usage, scan status and buttons.</summary>
    private sealed class ScanRow
    {
        public readonly Border Card;
        private readonly SystemPage _page;
        private Volume? _volume;
        private DiskScan? _scan;
        private readonly TextBlock _name = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _where = new() { FontSize = 11.5, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly UsageBar _usage = new();
        private readonly TextBlock _space = new() { FontSize = 12 };
        private readonly TextBlock _status = new() { FontSize = 11.5, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ScanBar _bar = new() { Height = 6, Width = 160, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _map, _pause, _stop, _forget;
        private readonly PathIcon _pauseIcon = new() { Data = Icons.Pause };

        public ScanRow(SystemPage page, Volume? volume, DiskScan? scan)
        {
            _page = page;
            _volume = volume;
            _scan = scan;
            _map = Ui.IconButton(Icons.Treemap, "Show what takes the space, as a map you can explore", OpenMap, "Map");
            _map.Classes.Add("accent");
            _pause = new Button { Content = _pauseIcon, Classes = { "flat" }, VerticalAlignment = VerticalAlignment.Center };
            _pause.Click += (_, _) => { if (Scan is { } s) { if (s.State == DiskScanState.Running) s.Pause(); else s.Resume(); Update(); } };
            _stop = Ui.IconButton(Icons.Stop, "Stop scanning (what has been found is kept, and it can be resumed later)", () => { Scan?.Stop(); Update(); });
            _forget = Ui.IconButton(Icons.Delete, "Forget this scan", () => { if (Scan is { } s) Disks.Delete(s); });
            var info = new StackPanel { Spacing = 3, Children = { _name, _where } };
            Control usage = volume is null ? new Panel() : new StackPanel { Spacing = 3, Children = { _usage, _space } };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,2*,2*,Auto"),
                Children =
                {
                    new PathIcon { Data = volume is null ? Icons.Folder : Icons.Harddisk, Width = 26, Height = 26, Foreground = Ui.Tk("textSoft"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center },
                    Ui.Apply(new StackPanel { Spacing = 6, Children = { info, usage } }, p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 1),
                    Ui.Apply(new StackPanel { Spacing = 5, Margin = new Thickness(18, 0), Children = { _status, _bar } }, p => p.VerticalAlignment = VerticalAlignment.Center).At(0, 2),
                    Ui.Row(4, _pause, _stop, _forget, _map).At(0, 3),
                },
            };
            Card = new Border { Classes = { "card" }, Padding = new Thickness(14, 10), Child = grid };
            Card.DoubleTapped += (_, _) => OpenMap();
            if (volume is not null) SetVolume(volume);
            Update();
        }

        private DiskScan? Scan => _scan ?? (_volume is { } v ? Disks.Scans.FirstOrDefault(s => s.VolumeKey == v.Key && s.Roots.SequenceEqual(v.ScanRoots)) : null);

        public void SetVolume(Volume v)
        {
            _volume = v;
            _name.Text = v.Name;
            var kind = v.Network ? "network" : v.Removable ? "removable" : "";
            _where.Text = $"{string.Join(", ", v.MountPoints)} · {v.FsType}{(kind.Length > 0 ? " · " + kind : "")}";
            ToolTip.SetTip(_where, string.Join("\n", v.MountPoints));
            _usage.Value = v.UsedFraction;
            _space.Text = $"{Fmt.Bytes(v.Used)} used of {Fmt.Bytes(v.Total)} · {Fmt.Bytes(v.Available)} free ({Fmt.Percent(v.Available / (double)Math.Max(1, v.Total))})";
        }

        public void Update()
        {
            var s = Scan;
            if (_scan is not null) { _name.Text = Path.GetFileName(_scan.Title.TrimEnd('/', '\\')) is { Length: > 0 } leaf ? leaf : _scan.Title; _where.Text = _scan.Title; }
            _status.Text = s is null ? "Not scanned yet" : Describe(s);
            ToolTip.SetTip(_status, s?.CurrentPath is { } cur ? $"{_status.Text}\n{cur}" : _status.Text);
            var busy = s?.Busy == true;
            _bar.IsVisible = busy || s?.State == DiskScanState.Stopped;
            _bar.Set(s?.Fraction, s?.State == DiskScanState.Running);
            _pause.IsVisible = busy || s?.State == DiskScanState.Stopped;
            _pauseIcon.Data = s?.State == DiskScanState.Running ? Icons.Pause : Icons.Play;
            ToolTip.SetTip(_pause, s?.State == DiskScanState.Running ? "Pause the scan" : "Carry on scanning");
            _stop.IsVisible = busy;
            _forget.IsVisible = _scan is not null && !busy;
        }

        private void OpenMap()
        {
            DiskScan scan;
            if (_scan is not null) scan = _scan;
            else if (_volume is { } v) scan = Disks.ForVolume(v);
            else return;
            DiskMapWindow.Open(scan, start: scan.State == DiskScanState.New);
            Update();
        }
    }
}
