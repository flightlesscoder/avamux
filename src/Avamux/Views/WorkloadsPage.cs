using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core.Workloads;
using Avamux.Plugins;

namespace Avamux.Views;

/// <summary>
/// AI Workloads: what runs models and AI work here and on the machines plugins reach, what can be started (profiles),
/// avamux's own AI tasks and queue jobs, and the models on disk. Readings are taken only while the page is on screen.
/// </summary>
public sealed class WorkloadsPage : UserControl, IPage, ISubPage
{
    private enum Tab { Running, Profiles, Tasks, Models }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly TextBlock _subtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "dim" } };
    private readonly StackPanel _problems = new() { Spacing = 6 };
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly StackPanel _models = new() { Spacing = 6 };
    private readonly TextBox _modelFilter = new() { PlaceholderText = "Filter models (name, architecture, quant, folder)…", MinWidth = 320 };
    private readonly TextBlock _modelSummary = new() { Classes = { "dim" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<Tab, ToggleButton> _tabs = [];
    private readonly Dictionary<string, Row> _rows = [];
    private readonly HashSet<string> _expanded = [];
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _modelsPanel;
    private Tab _tab = Tab.Running;
    private bool _listing, _scanning, _listAgain;
    private WorkloadOverview? _last;
    private List<ModelFile>? _modelList;

    private static WorkloadService Workloads => App.Services.Workloads;

    public WorkloadsPage()
    {
        var newProfile = Ui.IconButton(Icons.Plus, "A way to start a model server, a command, a remote RPC server or an API", () => NewProfileMenu(), "New Profile…");
        var refresh = Ui.IconButton(Icons.Refresh, "Look again now", () => Refresh(force: true));
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 14, 16, 6),
            Children = { new StackPanel { Children = { Ui.Text("AI Workloads", "h1"), _subtitle } }, Ui.Row(4, newProfile, refresh).At(0, 1) },
        };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(18, 0, 16, 8) };
        foreach (var (tab, label) in new[] { (Tab.Running, "Running"), (Tab.Profiles, "Profiles"), (Tab.Tasks, "Tasks and jobs"), (Tab.Models, "Models on disk") })
        {
            var b = new ToggleButton { Content = label, Padding = new Thickness(12, 5), FontSize = 12.5 };
            b.Click += (_, _) => Show(tab);
            _tabs[tab] = b;
            tabs.Children.Add(b);
        }
        _modelFilter.TextChanged += (_, _) => (_filterSoon ??= Ui.Debounced(ShowModels, 200))();
        _modelsPanel = new StackPanel
        {
            Spacing = 10, IsVisible = false,
            Children =
            {
                Ui.Row(8, _modelFilter, Ui.IconButton(Icons.Refresh, "Scan the model folders again", () => ScanModels(), "Rescan"),
                    Ui.IconButton(Icons.FolderOpen, "Which folders are searched", () => _ = EditFoldersAsync(), "Folders…"), _modelSummary),
                _models,
            },
        };
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20, 0, 20, 24), Children = { _problems, _list, _modelsPanel } };
        _scroll = new ScrollViewer { Content = body };
        Content = new DockPanel { Children = { header.Dock(Dock.Top), tabs.Dock(Dock.Top), _scroll } };
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Workloads.Changed += () => Dispatcher.UIThread.Post(() => Refresh(force: true));
        Show(Tab.Running);
    }

    private Action? _filterSoon;

    public void OnShown() => Refresh(force: true);

    /// <summary>"running", "profiles", "tasks", "models", optionally followed by ":&lt;workload id&gt;" to open that workload's details.</summary>
    public void OpenPart(string part)
    {
        var bits = part.Split(':', 2);
        if (bits[0].Length > 0 && Enum.GetNames<Tab>().FirstOrDefault(n => n.StartsWith(bits[0], StringComparison.OrdinalIgnoreCase)) is { } name) Show(Enum.Parse<Tab>(name));
        if (bits.Length > 1 && bits[1].Length > 0)
        {
            _expanded.Add(bits[1]);
            if (_rows.TryGetValue(bits[1], out var row)) row.Expand();
        }
    }

    private bool Showing => IsEffectivelyVisible && TopLevel.GetTopLevel(this) is MainWindow { WindowState: not WindowState.Minimized, BossShowing: false };

    private void Show(Tab tab)
    {
        _tab = tab;
        foreach (var (t, b) in _tabs) b.IsChecked = t == tab;
        _list.IsVisible = tab != Tab.Models;
        _modelsPanel.IsVisible = tab == Tab.Models;
        if (tab == Tab.Models && _modelList is null) ScanModels();
        if (_last is not null) Render(_last);
        Refresh(force: true);
    }

    // ---- reading ----

    private void Refresh(bool force = false)
    {
        if (!force && !Showing) return;
        // A forced refresh (something changed) that arrives during a listing runs after it, rather than being lost.
        if (_listing) { _listAgain |= force; return; }
        _listing = true;
        Task.Run(() => Workloads.ListAsync()).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _listing = false;
            if (t.IsCompletedSuccessfully) { _last = t.Result; Render(t.Result); }
            if (_listAgain) { _listAgain = false; Refresh(force: true); }
        }));
    }

    private static bool IsTask(WorkloadEntry e) => e.Info.Kind is WorkloadKind.Task or WorkloadKind.Job;
    private static bool Ended(WorkloadInfo i) => i.State is WorkloadState.Stopped or WorkloadState.Done or WorkloadState.Cancelled;

    private void Render(WorkloadOverview o)
    {
        _subtitle.Text = Summary(o);
        _problems.Children.Clear();
        foreach (var (source, problem) in o.Problems)
        {
            var name = Workloads.Sources.FirstOrDefault(s => s.Id == source)?.Name ?? source;
            _problems.Children.Add(Ui.Card(Ui.Row(8, new PathIcon { Data = Icons.AlertCircle, Width = 14, Height = 14, Foreground = Ui.Tk("warn") },
                Ui.Apply(Ui.Text($"{name}: {problem}", size: 12.5), t => t.MaxWidth = 900))));
        }
        Count(Tab.Running, "Running", o.Items.Count(e => !IsTask(e) && !Ended(e.Info)));
        Count(Tab.Profiles, "Profiles", o.Items.Count(e => !IsTask(e) && e.Info.ProfileId is not null));
        Count(Tab.Tasks, "Tasks and jobs", o.Items.Count(e => IsTask(e) && !Ended(e.Info) && e.Info.State != WorkloadState.Failed));
        if (_tab == Tab.Models) { _list.Children.Clear(); return; }

        IEnumerable<WorkloadEntry> items = _tab switch
        {
            Tab.Running => o.Items.Where(e => !IsTask(e) && (!Ended(e.Info) || e.Info.State == WorkloadState.Failed && e.Info.ProfileId is null)),
            Tab.Profiles => o.Items.Where(e => !IsTask(e) && (e.Info.ProfileId is not null || e.Info.Kind == WorkloadKind.Container)),
            _ => o.Items.Where(IsTask),
        };
        var list = items.ToList();
        // Running: what holds the most first. Profiles: by kind, then name. Tasks: newest first, running ones on top.
        // Grouped by machine (this computer first). Running: what holds the most first. Profiles: by kind, then name.
        // Tasks: running ones on top, then newest first.
        static int Here(WorkloadEntry e) => e.Machine == "This computer" ? 0 : 1;
        list = _tab switch
        {
            Tab.Running => list.OrderBy(Here).ThenBy(e => e.Machine).ThenBy(e => e.Info.Kind == WorkloadKind.Agent).ThenBy(e => e.Info.Kind == WorkloadKind.Container)
                .ThenByDescending(e => e.Info.VramMiB ?? 0).ThenBy(e => e.Info.Name).ToList(),
            Tab.Profiles => list.OrderBy(Here).ThenBy(e => e.Machine).ThenBy(e => e.Info.Kind == WorkloadKind.Container).ThenBy(e => Ended(e.Info)).ThenBy(e => e.Info.Kind)
                .ThenBy(e => e.Info.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => list.OrderBy(Here).ThenBy(e => e.Machine).ThenBy(e => e.Info.Finished is not null || Ended(e.Info)).ThenByDescending(e => e.Info.Started ?? DateTime.MinValue).ToList(),
        };
        _list.Children.Clear();
        if (list.Count == 0)
        {
            _list.Children.Add(Ui.Card(Ui.Text(_tab switch
            {
                Tab.Running => "Nothing AI is running. Start a profile from Profiles, or a model server some other way: it shows up here either way.",
                Tab.Profiles => "No profiles yet. Add one (New Profile…), or name folders of start-*.sh / stop-*.sh scripts under Settings → AI Workloads.",
                _ => "No AI tasks or queue jobs right now. Chat replies, AI Actions and jobs from queue plugins show up here.",
            }, "dim", 12.5)));
            return;
        }
        var seen = new HashSet<string>();
        string? lastMachine = null;
        foreach (var e in list)
        {
            if (e.Machine != lastMachine && (lastMachine is not null || e.Machine != "This computer"))
                _list.Children.Add(Ui.Apply(Ui.Text(e.Machine, "h2"), t => t.Margin = new Thickness(0, 8, 0, 0)));
            lastMachine = e.Machine;
            seen.Add(e.Id);
            if (!_rows.TryGetValue(e.Id, out var row)) _rows[e.Id] = row = new Row(this, e.Id);
            row.Update(e);
            _list.Children.Add(row.View);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList()) { _rows[gone].Close(); _rows.Remove(gone); }
    }

    private void Count(Tab tab, string label, int n) => _tabs[tab].Content = n > 0 ? $"{label} ({n})" : label;

    private static string Summary(WorkloadOverview o)
    {
        var parts = new List<string>();
        foreach (var g in o.Gpu.Gpus) parts.Add($"{g.Name.Replace("NVIDIA GeForce ", "")}: {g.UsedMiB / 1024.0:0.0} of {g.TotalMiB / 1024.0:0.0} GB used");
        var servers = o.Items.Count(e => e.Info.Kind is WorkloadKind.Server or WorkloadKind.RpcServer && !Ended(e.Info) && e.Info.State != WorkloadState.Failed);
        var tasks = o.Items.Count(e => IsTask(e) && !Ended(e.Info) && e.Info.State != WorkloadState.Failed);
        parts.Add(servers == 1 ? "1 server running" : $"{servers} servers running");
        if (tasks > 0) parts.Add(tasks == 1 ? "1 task" : $"{tasks} tasks");
        return string.Join(" · ", parts);
    }

    // ---- one workload ----

    private sealed class Row
    {
        private readonly WorkloadsPage _page;
        private readonly string _id;
        private readonly Border _dot = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new() { FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Border _type;
        private readonly TextBlock _typeText = new() { FontSize = 10.5 };
        private readonly TextBlock _line = new() { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new() { Height = 4, Minimum = 0, Maximum = 1, IsVisible = false };
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _more = new() { Spacing = 8, IsVisible = false, Margin = new Thickness(20, 6, 0, 0) };
        private readonly Grid _facts = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        private readonly TextBox _log = new() { IsReadOnly = true, Classes = { "mono" }, FontSize = 11.5, MaxHeight = 340, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
        private readonly Button _expand;
        private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private string _actionsKey = "";
        private bool _busy, _reading;
        /// <summary>What the row is doing at the user's request ("Stopping…"), shown until it has finished.</summary>
        private string? _doing;
        private WorkloadEntry? _entry;

        public Border View { get; }

        public Row(WorkloadsPage page, string id)
        {
            _page = page;
            _id = id;
            _error.Foreground = Ui.Tk("error");
            _type = new Border { Child = _typeText, Padding = new Thickness(6, 1), CornerRadius = Ui.R(4), Background = Ui.Tk("bg"), VerticalAlignment = VerticalAlignment.Center };
            var expand = _expand = Ui.IconButton(Icons.ChevronRight, "Details and log", Toggle);
            var top = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
                Children =
                {
                    expand.At(0, 0),
                    Ui.Apply(_dot, d => d.Margin = new Thickness(2, 0, 8, 0)).At(0, 1),
                    Ui.Row(8, _name, _type).At(0, 2),
                    _buttons.At(0, 3),
                },
            };
            var main = new StackPanel { Spacing = 3, Children = { top, Ui.Apply(new StackPanel { Spacing = 3, Margin = new Thickness(40, 0, 0, 0), Children = { _line, _detail, _error, _progress } }, _ => { }), _more } };
            _log.Height = double.NaN;
            _more.Children.Add(_facts);
            _more.Children.Add(Ui.Row(6, Ui.Text("Log and output", "dim", 11.5), Ui.IconButton(Icons.ContentCopy, "Copy the log", () => _ = App.Services.Host.SetClipboardAsync(_log.Text ?? ""))));
            _more.Children.Add(_log);
            View = new Border { Classes = { "card" }, Padding = new Thickness(8, 8, 12, 8), Child = main };
            _logTimer.Tick += (_, _) => ReadLog();
            if (_page._expanded.Contains(id)) Toggle();
        }

        public void Close() => _logTimer.Stop();

        public void Expand() { if (!_more.IsVisible) Toggle(); }

        private void Toggle()
        {
            _more.IsVisible = !_more.IsVisible;
            if (_expand.Content is StackPanel { Children: [PathIcon icon, ..] }) icon.RenderTransform = _more.IsVisible ? new RotateTransform(90) : null;
            if (_more.IsVisible) { _page._expanded.Add(_id); ReadLog(); _logTimer.Start(); }
            else { _page._expanded.Remove(_id); _logTimer.Stop(); }
        }

        private void ReadLog()
        {
            if (_reading || !_more.IsVisible || !_page.IsEffectivelyVisible) return;
            _reading = true;
            Task.Run(() => Workloads.LogAsync(_id, 300)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                _reading = false;
                var text = t.IsCompletedSuccessfully ? t.Result : "Could not read the log: " + t.Exception?.GetBaseException().Message;
                if (text.Length == 0) text = "(This one keeps no log avamux can read.)";
                if (_log.Text == text) return;
                var atEnd = _log.CaretIndex >= (_log.Text?.Length ?? 0) - 1;
                _log.Text = text;
                if (atEnd) _log.CaretIndex = text.Length;
            }));
        }

        public void Update(WorkloadEntry e)
        {
            _entry = e;
            var i = e.Info;
            _name.Text = i.Name;
            _typeText.Text = i.Type ?? i.Kind.ToString();
            _type.IsVisible = _typeText.Text.Length > 0;
            _dot.Background = Ui.StatusBrush(i.State switch
            {
                WorkloadState.Running or WorkloadState.Done => "ok",
                WorkloadState.Busy or WorkloadState.Starting or WorkloadState.Loading or WorkloadState.Stopping or WorkloadState.Queued or WorkloadState.Waiting => "info",
                WorkloadState.Failed => "error",
                _ => "",
            });
            _line.Text = Line(e);
            _detail.Text = _doing ?? i.Detail ?? "";
            _detail.IsVisible = _detail.Text.Length > 0;
            _error.Text = i.Error ?? "";
            _error.IsVisible = !string.IsNullOrEmpty(i.Error);
            _progress.IsVisible = i.Progress is not null;
            if (i.Progress is { } p) _progress.Value = p;

            var key = string.Join("|", i.Actions.Select(a => a.Id)) + "|" + _busy;
            if (key != _actionsKey)
            {
                _actionsKey = key;
                _buttons.Children.Clear();
                foreach (var a in i.Actions)
                {
                    var b = new Button { Content = a.Label, Padding = new Thickness(10, 4), FontSize = 12, IsEnabled = !_busy };
                    if (a.Danger) b.Foreground = Ui.Tk("error");
                    b.Click += (_, _) => _ = RunAsync(a);
                    _buttons.Children.Add(b);
                }
                var menu = Ui.IconButton(Icons.DotsVertical, "More", () => { });
                menu.Click += (_, _) => Menu(menu);
                _buttons.Children.Add(menu);
            }

            _facts.Children.Clear();
            _facts.RowDefinitions.Clear();
            var facts = new List<(string, string)>();
            if (i.Endpoint is { } url) facts.Add(("Address", url));
            if (i.Folder is { } f) facts.Add(("Folder", f));
            if (i.StartedBy is { } by) facts.Add(("Started by", by));
            if (i.Started is { } st) facts.Add(("Started", st.ToString("g")));
            if (i.Finished is { } fin) facts.Add(("Finished", fin.ToString("g")));
            facts.Add(("Source", e.SourceName + (e.Machine != e.SourceName ? $" ({e.Machine})" : "")));
            facts.Add(("Id", e.Id));
            facts.AddRange(i.Facts.Select(kv => (kv.Key, kv.Value)));
            for (var r = 0; r < facts.Count; r++)
            {
                _facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                _facts.Children.Add(Ui.Apply(Ui.Text(facts[r].Item1, "dim", 12), t => t.Margin = new Thickness(0, 1, 14, 1)).At(r, 0));
                var value = new SelectableTextBlock { Text = facts[r].Item2, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) };
                _facts.Children.Add(value.At(r, 1));
            }
        }

        private static string Line(WorkloadEntry e)
        {
            var i = e.Info;
            var parts = new List<string> { StateText(i) };
            if (i.Model is { Length: > 0 } m && m != i.Name) parts.Add(m);
            if (i.Port is { } port && i.Kind is not WorkloadKind.Agent) parts.Add($":{port}");
            if (i.Context is { } ctx) parts.Add($"{ctx:N0} context");
            if (i.VramMiB is { } v) parts.Add($"{v / 1024.0:0.0} GB GPU");
            if (i.RamMiB is { } ram && ram >= 64) parts.Add($"{ram / 1024.0:0.0} GB RAM");
            if (i.Pid is { } pid) parts.Add($"pid {pid}");
            if (i.Started is { } s && !Ended(i)) parts.Add("for " + For(DateTime.Now - s));
            if (i.Finished is { } f && Ended(i)) parts.Add(Ui.Ago(f));
            if (i.ByAvamux) parts.Add("started by avamux");
            return string.Join(" · ", parts);
        }

        private static string StateText(WorkloadInfo i) => i.State switch
        {
            WorkloadState.Busy => i.Kind is WorkloadKind.Server ? "answering a request" : "working",
            WorkloadState.Loading => "loading",
            _ => i.State.ToString().ToLowerInvariant(),
        };

        private static string For(TimeSpan d) => d.TotalMinutes < 1 ? $"{d.Seconds} s" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{(int)d.TotalDays} days";

        private void Menu(Control anchor)
        {
            if (_entry is not { } e) return;
            var items = new List<MenuItem>
            {
                Ui.Item(_more.IsVisible ? "Hide details" : "Details and log", Toggle),
                Ui.Item("Copy id", () => _ = App.Services.Host.SetClipboardAsync(e.Id)),
            };
            if (e.Info.Endpoint is { } url && url.StartsWith("http", StringComparison.Ordinal)) items.Add(Ui.Item("Open in browser", () => Avamux.Core.Shell.Open(url)));
            if (e.Info.Folder is { } f && Directory.Exists(f)) items.Add(Ui.Item("Show folder", () => Avamux.Core.Shell.ShowInFolder(f)));
            if (e.Info.ProfileId is { } pid && Workloads.Profile(pid) is { } profile)
            {
                items.Add(Ui.Item("Edit profile…", () => _ = _page.EditProfileAsync(profile)));
                if (profile.Kind == WorkloadProfile.LlamaCpp || profile.ContextArg)
                    items.Add(Ui.Item("Start with a context size…", () => _ = _page.StartAsync(profile, askContext: true)));
                if (profile.StartScript.Length > 0) items.Add(Ui.Item("Open start script", () => Ui.OpenInEditor(profile.StartScript)));
                if (!profile.Discovered) items.Add(Ui.Item("Duplicate profile", () => _ = _page.EditProfileAsync(Dup(profile))));
                items.Add(Ui.Item(profile.Discovered ? "Hide this script" : "Remove profile…", async () =>
                {
                    if (!profile.Discovered && !await Ui.ConfirmAsync(Ui.Owner(_page), "Remove profile", $"Remove the profile {profile.Name}? Nothing it started is stopped.", "Remove")) return;
                    Ui.Try(_page, () => Workloads.RemoveProfile(profile.Id));
                }));
            }
            var menu = new ContextMenu();
            foreach (var item in items) menu.Items.Add(item);
            menu.Open(anchor);
        }

        private static WorkloadProfile Dup(WorkloadProfile p)
        {
            var c = p.Clone();
            c.Id = "";
            c.Name = p.Name + " (copy)";
            return c;
        }

        private async Task RunAsync(WorkloadAction a)
        {
            if (_entry is not { } e) return;
            var owner = Ui.Owner(_page);
            if (a.Id == WorkloadActions.Start && e.SourceId == WorkloadService.LocalSourceId && e.Info.ProfileId is { } pid && Workloads.Profile(pid) is { } profile)
            {
                await _page.StartAsync(profile, askContext: false);
                return;
            }
            if (a.Confirm is { } question && !await Ui.ConfirmAsync(owner, a.Label, question, a.Label)) return;
            _busy = true;
            _actionsKey = "";
            // Said straight away: a stop script can take half a minute (it waits for the server to let go of the GPU).
            _doing = a.Id == WorkloadActions.Stop && e.Info.ProfileId is { } sp && Workloads.Profile(sp) is { StopScript.Length: > 0 } scripted
                ? $"Stopping… (running {Path.GetFileName(scripted.StopScript)})" : a.Label + "…";
            Update(e);
            try
            {
                var result = await Task.Run(() => Workloads.ActAsync(e.Id, a.Id));
                if (result.Length > 0) Ui.Toast(_page, e.Info.Name, result);
            }
            catch (Exception ex) { Ui.Toast(_page, $"Could not {a.Label.ToLowerInvariant()} {e.Info.Name}", ex.GetBaseException().Message); }
            finally
            {
                _busy = false;
                _doing = null;
                _actionsKey = "";
                if (_entry is { } now) Update(now);
                _page.Refresh(force: true);
            }
        }
    }

    // ---- starting (swapping) ----

    /// <summary>
    /// Starts a profile: says first what it would stop (and whether that is answering a request right now) and what
    /// else holds the GPU, then starts it in the background. The row shows the progress; a toast says how it ended.
    /// </summary>
    internal async Task StartAsync(WorkloadProfile profile, bool askContext)
    {
        await StartProfileAsync(this, profile, askContext, () => Show(Tab.Running));
        Refresh(force: true);
    }

    /// <summary>
    /// Starts a profile from anywhere in the window: says first what it would stop (and whether that is answering a
    /// request right now) and what else holds the GPU, then starts it. <paramref name="begun"/> runs once it is under way.
    /// Returns true when it is running.
    /// </summary>
    internal static async Task<bool> StartProfileAsync(Control from, WorkloadProfile profile, bool askContext, Action? begun = null)
    {
        var owner = Ui.Owner(from);
        long? context = null;
        if (askContext)
        {
            var text = await Ui.PromptAsync(owner, "Start " + profile.Name, "Context size (tokens):", profile.Context > 0 ? profile.Context.ToString() : "32768");
            if (text is null) return false;
            if (!long.TryParse(text.Replace(",", "").Trim(), out var c) || c < 256) { Ui.Toast(from, "Not a context size", text); return false; }
            context = c;
        }
        StartCheck check;
        try { check = await Task.Run(() => Workloads.CheckStartAsync(profile.Id)); }
        catch (Exception ex) { Ui.Toast(from, "Could not check " + profile.Name, ex.GetBaseException().Message); return false; }
        if (check.Notes.FirstOrDefault(n => n.Contains(" is not there", StringComparison.Ordinal)) is { } missing) { Ui.Toast(from, "Cannot start " + profile.Name, missing); return false; }
        var force = false;
        if (check.Conflicts.Count > 0 || check.GpuHolders.Count > 0)
        {
            var lines = new StackPanel { Spacing = 4 };
            foreach (var c in check.Conflicts)
                lines.Children.Add(Ui.Text($"• Stop {c.Info.Name}" + (c.Info.State == WorkloadState.Busy ? " — it is answering a request right now" : "") + (c.Info.ByAvamux ? "" : " (avamux did not start it)"), size: 12.5));
            foreach (var h in check.GpuHolders)
                lines.Children.Add(Ui.Text($"• Also on the GPU: {h.Name} (pid {h.Pid}) holds {h.MiB / 1024.0:0.0} GB, which {profile.Name} may need.", "dim", 12.5));
            if (check.FreeVramMiB is { } free) lines.Children.Add(Ui.Text($"About {free / 1024.0:0.0} GB of GPU memory will be free for it.", "dim", 12));
            var message = check.Conflicts.Count > 0 ? $"Starting {profile.Name} swaps out what runs in its place:" : "Something else is using the GPU:";
            var choice = await Ui.ChoiceAsync(owner, "Start " + profile.Name, message, lines, ("Cancel", false), (check.Conflicts.Count > 0 ? "Swap" : "Start anyway", true));
            if (choice != 1) return false;
            force = check.Busy.Count > 0;
        }
        Ui.Toast(from, "Starting " + profile.Name, "This can take a few minutes; AI Workloads shows how far it got.");
        begun?.Invoke();
        try
        {
            var result = await Task.Run(() => Workloads.StartAsync(profile.Id, new StartOptions { Context = context, Force = force }));
            Ui.Toast(from, profile.Name, result);
            return true;
        }
        catch (OperationCanceledException) { Ui.Toast(from, profile.Name, "The start was cancelled."); }
        catch (Exception ex) { Ui.Toast(from, "Could not start " + profile.Name, ex.GetBaseException().Message); }
        return false;
    }

    // ---- profiles ----

    private void NewProfileMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("llama.cpp server…", () => _ = EditProfileAsync(new WorkloadProfile { Kind = WorkloadProfile.LlamaCpp, Binary = LastBinary() })));
        menu.Items.Add(Ui.Item("Command (any server)…", () => _ = EditProfileAsync(new WorkloadProfile { Kind = WorkloadProfile.CommandKind, Group = "gpu" })));
        menu.Items.Add(Ui.Item("Remote llama.cpp RPC server…", () => _ = EditProfileAsync(new WorkloadProfile { Kind = WorkloadProfile.Rpc, Group = "" })));
        menu.Items.Add(Ui.Item("Hosted API…", () => _ = EditProfileAsync(new WorkloadProfile { Kind = WorkloadProfile.Api, Group = "", Url = "https://api.openai.com/v1", ApiKeyEnv = "OPENAI_API_KEY" })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Script folders…", () => _ = EditFoldersAsync()));
        menu.Open(this);
    }

    private static string LastBinary() =>
        Workloads.Profiles.Where(p => p.Binary.Length > 0 && File.Exists(Avamux.Core.Ai.AiService.ExpandHome(p.Binary))).Select(p => p.Binary).FirstOrDefault() ?? "";

    internal async Task EditProfileAsync(WorkloadProfile p)
    {
        var isNew = p.Id.Length == 0;
        var fields = new StackPanel { Spacing = 12 };
        TextBox Box(string value, string? placeholder = null, bool multi = false, bool mono = false)
        {
            var b = new TextBox { Text = value, PlaceholderText = placeholder, AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multi ? 64 : double.NaN };
            if (mono) b.Classes.Add("mono");
            return b;
        }
        void Add(string label, Control editor, string? help = null) => fields.Children.Add(Ui.Field(label, editor, help));
        static string Lines(IEnumerable<string> items) => string.Join("\n", items);
        static List<string> FromLines(string? text) => (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        static List<string> FromCommas(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var name = Box(p.Name, "What to call it");
        Add("Name", name);
        var binary = Box(p.Binary, "/path/to/llama.cpp/build/bin/llama-server");
        var model = Box(p.Model, "/path/to/model.gguf (the first shard of a split model)");
        var alias = Box(p.Alias, "The name clients ask for (empty: the profile's id)");
        var host = Box(p.Host);
        var port = Box(p.Port > 0 ? p.Port.ToString() : "");
        var ctx = Box(p.Context > 0 ? p.Context.ToString() : "", "Empty: the model's own");
        var args = Box(Lines(p.Args), "One argument per line, e.g.\n--n-gpu-layers\n99", multi: true, mono: true);
        var env = Box(Lines(p.Env.Select(kv => $"{kv.Key}={kv.Value}")), "NAME=value, one per line", multi: true, mono: true);
        var startArgs = Box(Lines(p.StartArgs), "One per line", multi: true, mono: true);
        var command = Box(p.Command, "The command line that starts it (and stays running)", mono: true);
        var stopCommand = Box(p.StopCommand, "Empty: avamux ends the process it started", mono: true);
        var url = Box(p.Url, "http://127.0.0.1:8080");
        var folder = Box(p.Folder);
        var rpcAddress = Box(p.RpcAddress, "host:50052");
        var statusCommand = Box(p.StatusCommand, "Exits 0 when it runs, e.g. ssh host systemctl is-active rpc-server", mono: true);
        var startCommand = Box(p.StartCommand, mono: true);
        var restartCommand = Box(p.RestartCommand, mono: true);
        var versionCommand = Box(p.VersionCommand, "Prints the llama.cpp commit it was built from", mono: true);
        var apiKeyEnv = Box(p.ApiKeyEnv, "OPENAI_API_KEY");
        var apiStyle = new ComboBox { ItemsSource = new[] { "openai", "anthropic", "ollama" }, SelectedItem = p.ApiStyle, MinWidth = 160 };
        var group = Box(p.Group, "Empty: runs beside anything");
        var needsRpc = Box(string.Join(", ", p.NeedsRpc), "Ids of RPC profiles");
        var leases = Box(string.Join(", ", p.Leases), "Ids of lease providers (from plugins)");
        var idle = Box(p.IdleMinutes > 0 ? p.IdleMinutes.ToString() : "", "Empty: never");
        var startMinutes = Box(p.StartMinutes.ToString());
        var notes = Box(p.Notes, multi: true);

        if (p.Discovered)
        {
            fields.Children.Add(Ui.Text($"Found in {p.Folder}. The script is the config; only these are kept by avamux.", "dim", 12));
            Add("Start script arguments", startArgs, p.ContextArg ? "Its first argument is the context size: avamux passes one when you start it with a context." : null);
        }
        else
            switch (p.Kind)
            {
                case WorkloadProfile.LlamaCpp:
                    Add("llama-server", binary);
                    Add("Model", model);
                    Add("Alias", alias);
                    fields.Children.Add(new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*"),
                        Children = { Ui.Field("Host", host).At(0, 0), Ui.Field("Port", port).At(0, 2), Ui.Field("Context", ctx).At(0, 4) },
                    });
                    Add("More arguments", args, "GPU layers, KV cache type, threads, speculative decoding …: whatever this llama-server takes.");
                    Add("Environment", env);
                    break;
                case WorkloadProfile.CommandKind:
                    Add("Command", command);
                    Add("Stop command", stopCommand);
                    Add("Address", url, "Where it answers, for health and readiness. Empty: it counts as running while its process runs.");
                    Add("Folder", folder);
                    Add("Environment", env);
                    break;
                case WorkloadProfile.Rpc:
                    Add("Address", rpcAddress, "What llama-server's --rpc gets. Profiles and scripts with this address use it.");
                    Add("Status command", statusCommand);
                    Add("Start command", startCommand);
                    Add("Stop command", stopCommand);
                    Add("Restart command", restartCommand);
                    Add("Version command", versionCommand, "Checked against the local llama-server before a server that uses this one starts: RPC needs both built from the same commit.");
                    break;
                case WorkloadProfile.Api:
                    Add("Base URL", url);
                    Add("API key variable", apiKeyEnv, "The environment variable that holds the key. avamux never stores keys.");
                    Add("Style", apiStyle);
                    break;
            }
        if (p.Kind is not WorkloadProfile.Api)
        {
            if (p.Kind != WorkloadProfile.Rpc)
            {
                Add("Group", group, "Profiles in one group never run together: starting one stops the others (they share the GPU). Servers here default to gpu.");
                Add("RPC servers it uses", needsRpc);
                Add("Idle stop (minutes)", idle, "Stop it after this long without a request, when avamux started it.");
                Add("Wait for it (minutes)", startMinutes);
            }
            Add("Leases", leases, p.Kind == WorkloadProfile.Rpc ? "Asked before any server that uses this RPC server starts (another machine's queue)." : null);
        }
        Add("Notes", notes);

        var saved = await Ui.FormAsync(Ui.Owner(this), isNew ? "New profile" : "Edit " + p.Name, fields, () =>
        {
            var q = p.Clone();
            q.Name = name.Text?.Trim() ?? "";
            if (!p.Discovered)
            {
                q.Binary = binary.Text?.Trim() ?? ""; q.Model = model.Text?.Trim() ?? ""; q.Alias = alias.Text?.Trim() ?? ""; q.Host = host.Text?.Trim() ?? "";
                q.Port = int.TryParse(port.Text, out var po) ? po : 0;
                q.Context = long.TryParse(ctx.Text?.Replace(",", ""), out var c) ? c : 0;
                q.Args = FromLines(args.Text);
                q.Env = FromLines(env.Text).Select(l => l.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].Trim(), x => x[1]);
                q.Command = command.Text?.Trim() ?? ""; q.StopCommand = stopCommand.Text?.Trim() ?? ""; q.Url = url.Text?.Trim() ?? ""; q.Folder = folder.Text?.Trim() ?? "";
                q.RpcAddress = rpcAddress.Text?.Trim() ?? ""; q.StatusCommand = statusCommand.Text?.Trim() ?? ""; q.StartCommand = startCommand.Text?.Trim() ?? "";
                q.RestartCommand = restartCommand.Text?.Trim() ?? ""; q.VersionCommand = versionCommand.Text?.Trim() ?? "";
                q.ApiKeyEnv = apiKeyEnv.Text?.Trim() ?? ""; q.ApiStyle = apiStyle.SelectedItem as string ?? "openai";
                if (q.Kind == WorkloadProfile.LlamaCpp && (q.Binary.Length == 0 || q.Model.Length == 0)) return Task.FromResult<string?>("A llama.cpp profile needs llama-server and a model.");
                if (q.Kind == WorkloadProfile.LlamaCpp && q.Port <= 0) return Task.FromResult<string?>("Choose a port.");
                if (q.Kind == WorkloadProfile.CommandKind && q.Command.Length == 0) return Task.FromResult<string?>("A command profile needs a command.");
                if (q.Kind == WorkloadProfile.Rpc && q.RpcAddress.Length == 0) return Task.FromResult<string?>("Give the RPC server's address (host:port).");
            }
            else q.StartArgs = FromLines(startArgs.Text);
            q.Group = group.Text?.Trim() ?? "";
            q.NeedsRpc = FromCommas(needsRpc.Text);
            q.Leases = FromCommas(leases.Text);
            q.IdleMinutes = double.TryParse(idle.Text, out var im) ? im : 0;
            q.StartMinutes = double.TryParse(startMinutes.Text, out var sm) && sm > 0 ? sm : 15;
            q.Notes = notes.Text ?? "";
            Workloads.PutProfile(q);
            return Task.FromResult<string?>(null);
        }, width: 600);
        if (saved) { Show(Tab.Profiles); Refresh(force: true); }
    }

    private async Task EditFoldersAsync()
    {
        var scripts = new TextBox { Text = string.Join("\n", Workloads.ScriptFolders), AcceptsReturn = true, MinHeight = 60, Classes = { "mono" } };
        var models = new TextBox { Text = string.Join("\n", Workloads.ModelFolders), AcceptsReturn = true, MinHeight = 90, Classes = { "mono" } };
        var body = Ui.Column(12,
            Ui.Field("Script folders", scripts, "Folders of start-<name>.sh / stop-<name>.sh pairs, one per line. Each pair is offered as a profile."),
            Ui.Field("Model folders", models, "Searched for .gguf, .safetensors, .onnx and whisper.cpp files, one per line."));
        var saved = await Ui.FormAsync(Ui.Owner(this), "AI Workloads folders", body, () =>
        {
            static List<string> L(string? t) => (t ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Workloads.SetScriptFolders(L(scripts.Text));
            Workloads.SetModelFolders(L(models.Text));
            return Task.FromResult<string?>(null);
        });
        if (saved) { _modelList = null; if (_tab == Tab.Models) ScanModels(); Refresh(force: true); }
    }

    // ---- models on disk ----

    private void ScanModels()
    {
        if (_scanning) return;
        _scanning = true;
        _modelSummary.Text = "Scanning…";
        var folders = Workloads.ModelFolders;
        Task.Run(() => Workloads.Models.Scan(folders)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _scanning = false;
            if (!t.IsCompletedSuccessfully) { _modelSummary.Text = "The scan failed: " + t.Exception?.GetBaseException().Message; return; }
            _modelList = t.Result;
            ShowModels();
        }));
    }

    private void ShowModels()
    {
        if (_modelList is null) return;
        var filter = _modelFilter.Text?.Trim() ?? "";
        var usedBy = Workloads.ModelUse();
        var shown = _modelList.Where(m => filter.Length == 0 || $"{m.Name} {m.Title} {m.Architecture} {m.Quant} {m.Size} {m.Path} {m.Format}".Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _modelSummary.Text = $"{shown.Count:N0} of {_modelList.Count:N0} files · {shown.Sum(m => m.Bytes) / 1073741824.0:N0} GB";
        _models.Children.Clear();
        foreach (var m in shown.Take(300)) _models.Children.Add(ModelRow(m, usedBy.TryGetValue(m.Path, out var u) ? u : null));
        if (shown.Count > 300) _models.Children.Add(Ui.Text($"…and {shown.Count - 300:N0} more. Filter to find them.", "dim", 12));
    }

    private Control ModelRow(ModelFile m, List<string>? usedBy)
    {
        var facts = new List<string> { $"{m.Bytes / 1073741824.0:0.0} GB" };
        if (m.Shards > 1) facts.Add($"{m.Shards} parts");
        if (m.Architecture is { } a) facts.Add(a);
        if (m.Size is { } s) facts.Add(s);
        if (m.Quant is { } q) facts.Add(q);
        if (m.ContextLength is { } c) facts.Add($"{c:N0} context");
        if (m.Layers is { } l) facts.Add($"{l} layers");
        if (m.Experts is { } e) facts.Add($"{e} experts");
        if (m.Projector) facts.Add("projector (mmproj)");
        if (m.Format is not "gguf") facts.Add(m.Format);
        var lines = Ui.Column(2,
            Ui.Row(8, Ui.Apply(Ui.Text(m.Name), t => { t.FontWeight = FontWeight.SemiBold; t.FontSize = 13; }), Ui.Text(m.Title is { } title && title != m.Name ? title : "", "dim", 12)),
            Ui.Text(string.Join(" · ", facts), "dim", 12),
            Ui.Apply(new SelectableTextBlock { Text = m.Path, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }, t => t.Foreground = Ui.Tk("faint")));
        if (usedBy is not null) lines.Children.Add(Ui.Text("Used by: " + string.Join(", ", usedBy), size: 12));
        if (m.Error is { } err) lines.Children.Add(Ui.Apply(Ui.Text("Could not read its header: " + err, size: 12), t => t.Foreground = Ui.Tk("warn")));
        var buttons = Ui.Row(4,
            Ui.IconButton(Icons.FolderOpen, "Show it in the file manager", () => Avamux.Core.Shell.ShowInFolder(m.Path)),
            Ui.IconButton(Icons.ContentCopy, "Copy its path", () => _ = App.Services.Host.SetClipboardAsync(m.Path)));
        if (m.Format == "gguf" && !m.Projector)
            buttons.Children.Add(Ui.IconButton(Icons.Plus, "A llama.cpp profile that serves this model", () => _ = EditProfileAsync(new WorkloadProfile
            {
                Kind = WorkloadProfile.LlamaCpp, Name = m.Title ?? m.Name, Model = m.Path, Binary = LastBinary(), Port = 8080,
                Args = ["--jinja", "--flash-attn", "on", "--fit", "on"],
            })));
        return Ui.Card(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { lines.At(0, 0), Ui.Apply(buttons, b => b.VerticalAlignment = VerticalAlignment.Top).At(0, 1) } });
    }
}
