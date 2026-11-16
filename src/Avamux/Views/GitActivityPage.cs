using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.GitActivity;

namespace Avamux.Views;

/// <summary>
/// Git Activity: where the work is going across every repository of an organisation (or several), with the
/// settings that say which repositories those are. Two screens: the figures, and Git Activity Settings.
/// </summary>
public sealed class GitActivityPage : UserControl, IPage
{
    private readonly Button _figuresTab, _settingsTab;
    private readonly GitActivityView _figures = new();
    private readonly GitActivitySettingsView _settings = new();
    private readonly TextBlock _status = new() { FontSize = 12, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _bar = new() { Height = 3, MinHeight = 3, IsVisible = false, Minimum = 0, Maximum = 1, Margin = new Thickness(20, 0, 20, 0) };
    private readonly Button _read, _stop;

    private static GitActivityService Activity => App.Services.Activity;

    public GitActivityPage()
    {
        _figuresTab = new Button { Content = "Git Activity", Classes = { "tab" } };
        _settingsTab = new Button { Content = "Git Activity Settings", Classes = { "tab" } };
        _figuresTab.Click += (_, _) => ShowSettings(false);
        _settingsTab.Click += (_, _) => ShowSettings(true);
        _read = Ui.IconButton(Icons.Refresh, "List the sources and read every repository that changed", () => _ = Activity.ScanAsync(), "Read Now");
        _stop = Ui.IconButton(Icons.Stop, "Stop reading (what was read so far is kept)", Activity.Cancel, "Stop");
        _stop.IsVisible = false;
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 14, 16, 6),
            Children =
            {
                new StackPanel { Children = { Ui.Text("Git Activity", "h1"), _status } },
                Ui.Row(4, _stop, _read).At(0, 1),
            },
        };
        var tabs = new Border { Padding = new Thickness(14, 0), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Row(0, _figuresTab, _settingsTab) };
        _settings.IsVisible = false;
        _settings.OpenFigures = () => ShowSettings(false);
        _figures.OpenSettings = () => ShowSettings(true);
        Content = new DockPanel
        {
            Children = { header.Dock(Dock.Top), _bar.Dock(Dock.Top), tabs.Dock(Dock.Top), new Panel { Children = { _figures, _settings } } },
        };
        ShowSettings(false);
        Activity.ProgressChanged += _ => Dispatcher.UIThread.Post(ShowProgress);
        Activity.ConfigChanged += () => Dispatcher.UIThread.Post(ShowProgress);
        Activity.DataChanged += () => Dispatcher.UIThread.Post(ShowProgress);
        ShowProgress();
    }

    public void OnShown()
    {
        if (_settings.IsVisible) _settings.Refresh(); else _figures.Refresh();
        ShowProgress();
    }

    public void ShowSettings(bool settings)
    {
        _figures.IsVisible = !settings;
        _settings.IsVisible = settings;
        _figuresTab.Classes.Set("on", !settings);
        _settingsTab.Classes.Set("on", settings);
        if (settings) _settings.Refresh(); else _figures.Refresh();
    }

    private void ShowProgress()
    {
        var p = Activity.Progress;
        _read.IsVisible = !p.Running;
        _stop.IsVisible = p.Running;
        _bar.IsVisible = p.Running;
        _bar.IsIndeterminate = p.Running && p.Total == 0;
        _bar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
        var repos = Activity.Repos.Count;
        var disabled = Activity.Config.DisabledRepositories.Count;
        string text;
        if (p.Running)
            text = p.Total == 0 ? $"{Cap(p.Phase)}…" : $"Reading {p.Done:N0} of {p.Total:N0} repositories{(p.Errors > 0 ? $" ({p.Errors} failed)" : "")}" +
                (p.Current.Count > 0 ? $"  ·  {string.Join(", ", p.Current.Take(3))}{(p.Current.Count > 3 ? "…" : "")}" : "");
        else
        {
            var last = Activity.Repos.Select(r => Activity.ActivityOf(r.Key)?.ScannedAt).Where(t => t is not null).DefaultIfEmpty(null).Max();
            var sources = Activity.Config.Sources.Count(s => s.Enabled);
            text = $"{repos:N0} repositor{(repos == 1 ? "y" : "ies")} from {sources} source{(sources == 1 ? "" : "s")}{(disabled > 0 ? $" ({disabled} switched off)" : "")}" +
                   $"  ·  {(last is null ? "not read yet" : "read " + Ui.Ago(last.Value.LocalDateTime))}" +
                   (p.Finished is not null && p.Errors > 0 ? $"  ·  {p.Errors} could not be read (see Settings)" : "") +
                   (p.Phase.StartsWith("failed") ? "  ·  " + p.Phase : "");
        }
        _status.Text = text;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Opens Git Activity's repositories, commits and branches on the Repositories page, cloning a remote one first if need be.</summary>
public static class ActivityToRepositories
{
    private static readonly Dictionary<string, string?> Remotes = new();

    public static async Task<Core.Repos.RepoEntry?> FindAsync(ActivityRepo repo)
    {
        var repos = App.Services.Repos;
        if (repo.LocalPath is { } path && repos.Items.FirstOrDefault(r => SamePath(r.Path, path)) is { } byPath) return byPath;
        foreach (var entry in repos.Items)
        {
            string? web;
            lock (Remotes) Remotes.TryGetValue(entry.Path, out web);
            if (web is null)
            {
                web = Core.Repos.RepoService.ToWebUrl(await repos.RemoteUrlAsync(entry))?.ToLowerInvariant().TrimEnd('/') ?? "";
                lock (Remotes) Remotes[entry.Path] = web;
            }
            if (web.Length > 0 && web == repo.Identity) return entry;
        }
        return null;
    }

    private static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd('/') == Path.GetFullPath(b).TrimEnd('/');

    /// <param name="what">"commit:hash", "branch:name", or null for the repository itself.</param>
    public static async Task OpenAsync(Control from, ActivityRepo repo, string? what)
    {
        await Ui.TryAsync(from, async () =>
        {
            var repos = App.Services.Repos;
            var entry = await FindAsync(repo);
            if (entry is null && repo.LocalPath is { } local) entry = await repos.AddAsync(local, repo.Name);
            if (entry is null)
            {
                if (repo.CloneUrl is null) throw new InvalidOperationException($"{repo.Name} has no clone URL.");
                var folder = Path.Combine(App.Services.Paths.HomeDir, "src", repo.Name[(repo.Name.LastIndexOf('/') + 1)..]);
                var target = await Ui.PromptAsync(Ui.Owner(from), "Clone repository",
                    $"{repo.Name} is not on this computer. Clone it into this folder and add it to Repositories:", folder);
                if (string.IsNullOrWhiteSpace(target)) return;
                target = Path.GetFullPath(target.StartsWith('~') ? App.Services.Paths.HomeDir + target[1..] : target);
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new InvalidOperationException($"{target} is not empty.");
                Ui.Toast(from, $"Cloning {repo.Name}…", target);
                var source = App.Services.Activity.FindSource(repo.SourceId);
                var env = source is null ? [] : await ActivitySources.For(source.Kind).GitEnvAsync(source, repo, CancellationToken.None);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Git.Check(await Git.RunAsync(Path.GetDirectoryName(target)!, ["clone", "--quiet", repo.CloneUrl, target], env, null, TimeSpan.FromMinutes(30), CancellationToken.None));
                entry = await repos.AddAsync(target, repo.Name);
            }
            if (what?.StartsWith("commit:", StringComparison.Ordinal) == true)
            {
                // A commit made since the copy was last fetched: fetch, then show it.
                var has = await Shell.RunAsync("git", ["cat-file", "-e", what[7..] + "^{commit}"], entry.Path, null, TimeSpan.FromSeconds(20));
                if (has.ExitCode != 0) { Ui.Toast(from, "Fetching…", $"{what[7..Math.Min(what.Length, 17)]} is not in {entry.Name} yet."); await repos.FetchAsync(entry); }
            }
            App.Services.Host.Navigate("repositories");
            Ui.Main?.Page<RepositoriesPage>("repositories")?.Reveal(entry.Id, what);
        }, $"Could not open {repo.Name}");
    }

    public static void Browse(ActivityRepo repo, string? what)
    {
        var url = what?.StartsWith("commit:") == true ? WebLinks.Commit(repo.WebUrl, what[7..]) : what?.StartsWith("branch:") == true ? WebLinks.Branch(repo.WebUrl, what[7..]) : repo.WebUrl;
        if (url is not null) Shell.Open(url); else Ui.Toast(null, "No web page", $"{repo.Name} has no known web address.");
    }
}

/// <summary>The figures: summary, heatmap, leaderboards, rhythm, AI, technology and the commits themselves.</summary>
public sealed class GitActivityView : UserControl
{
    private enum View { Heatmap, Leaders, Rhythm, Ai, Tech, Commits }

    /// <summary>A quick choice of range. From null is the beginning; To null is the present.</summary>
    private sealed record Preset(string Label, Func<(DateTime? From, DateTime? To)>? Range);

    private static readonly Preset[] Presets = BuildPresets();

    private static Preset[] BuildPresets()
    {
        static DateTime T() => DateTime.Today;
        static DateTime Quarter(DateTime d) => new(d.Year, (d.Month - 1) / 3 * 3 + 1, 1);
        return
        [
            new("Last 7 days", () => (T().AddDays(-7), null)), new("Last 14 days", () => (T().AddDays(-14), null)), new("Last 30 days", () => (T().AddDays(-30), null)),
            new("Last 2 months", () => (T().AddDays(-61), null)), new("Last 3 months", () => (T().AddDays(-91), null)), new("Last 6 months", () => (T().AddDays(-182), null)),
            new("Last 12 months", () => (T().AddDays(-365), null)), new("This quarter", () => (Quarter(T()), null)),
            new("Last quarter", () => (Quarter(T()).AddMonths(-3), Quarter(T()).AddDays(-1))), new("This year", () => (new DateTime(T().Year, 1, 1), null)),
            new("Last year", () => (new DateTime(T().Year - 1, 1, 1), new DateTime(T().Year - 1, 12, 31))), new("All time", () => (null, null)), new("Custom range", null),
        ];
    }
    private static readonly ActivityDimension[] Dimensions = Enum.GetValues<ActivityDimension>();

    private readonly ActivityFilter _filter = new();
    private DateTime? _from, _to;   // null: the beginning / the present
    private ActivityMeasure _measure = ActivityMeasure.Commits;
    private View _view = View.Heatmap;
    private ActivityDimension _rows = ActivityDimension.Repository, _cols = ActivityDimension.Day;
    private int _rowCount = 40;
    private int _generation;
    private bool _building;

    private readonly ComboBox _preset = new() { MinWidth = 150 }, _branches = new() { MinWidth = 170 }, _ai = new() { MinWidth = 150 }, _measureBox = new() { MinWidth = 140 };
    private readonly ComboBox _rowBox = new() { MinWidth = 150 }, _colBox = new() { MinWidth = 150 }, _countBox = new() { MinWidth = 80 };
    private static readonly int[] ActiveChoices = [0, 7, 14, 30, 60, 90, 180, 365];
    private readonly ComboBox _peopleActive = new() { MinWidth = 210 }, _reposActive = new() { MinWidth = 220 };
    private readonly CalendarDatePicker _fromPicker = new() { PlaceholderText = "Beginning", Width = 140 }, _toPicker = new() { PlaceholderText = "Present", Width = 140 };
    private readonly CheckBox _bots = new() { Content = "Hide bots", FontSize = 12.5 }, _merges = new() { Content = "Merges", FontSize = 12.5 };
    private readonly TextBox _search = new() { PlaceholderText = "Search commits, people, branches…", Width = 240 };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly Button[] _viewTabs;
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _hover = new() { FontSize = 11.5, Classes = { "dim" }, TextTrimming = TextTrimming.CharacterEllipsis, Height = 18 };
    private readonly StackPanel _pivotBar;
    private readonly Action _refreshSoon;

    public Action? OpenSettings { get; set; }

    private static GitActivityService Activity => App.Services.Activity;

    public GitActivityView()
    {
        var since = Activity.Since;
        _from = ActivityScanner.AllTime(since) ? null : since.Date;
        _fromPicker.SelectedDate = _from;
        _filter.HideBots = Activity.Config.HideBots;
        _bots.IsChecked = _filter.HideBots;
        Fill(_preset, Presets, p => p.Label);
        _preset.SelectedItem = Presets.FirstOrDefault(p => p.Range?.Invoke() is var (f, t) && f == _from && t == _to) ?? Presets[^1];
        ToolTip.SetTip(_fromPicker, "From this day. Empty: from the beginning of every repository's history");
        ToolTip.SetTip(_toPicker, "To this day, included. Empty: up to now");
        Fill(_peopleActive, ActiveChoices, d => d == 0 ? "All contributors" : $"Contributors active in last {d} days");
        Fill(_reposActive, ActiveChoices, d => d == 0 ? "All repositories" : $"Repositories active in last {d} days");
        _filter.PeopleActiveDays = Activity.Config.ActivePeopleDays;
        _filter.ReposActiveDays = Activity.Config.ActiveReposDays;
        _peopleActive.SelectedItem = ActiveChoices.Contains(_filter.PeopleActiveDays) ? _filter.PeopleActiveDays : 0;
        _reposActive.SelectedItem = ActiveChoices.Contains(_filter.ReposActiveDays) ? _filter.ReposActiveDays : 0;
        ToolTip.SetTip(_peopleActive, "Leave out contributors with no commit in this many days before the end of the range (today when it runs to the present)");
        ToolTip.SetTip(_reposActive, "Leave out repositories with no commit in this many days before the end of the range (today when it runs to the present)");
        _peopleActive.SelectionChanged += (_, _) => { if (!_building && _peopleActive.SelectedItem is int d) { _filter.PeopleActiveDays = d; _refreshSoon!(); } };
        _reposActive.SelectionChanged += (_, _) => { if (!_building && _reposActive.SelectedItem is int d) { _filter.ReposActiveDays = d; _refreshSoon!(); } };
        Fill(_branches, ["All branches", "Leave out mainline branches", "Only mainline branches"], s => s);
        _branches.SelectedIndex = 0;
        ToolTip.SetTip(_branches, "Mainline branches (main, master, develop, release/*…) are set under Git Activity Settings");
        Fill(_ai, ["With and without AI", "With AI help", "Without AI help", "By AI agents"], s => s);
        _ai.SelectedIndex = 0;
        Fill(_measureBox, Enum.GetValues<ActivityMeasure>(), ActivityEngine.Label);
        _measureBox.SelectedItem = _measure;
        Fill(_rowBox, Dimensions, ActivityEngine.Label);
        Fill(_colBox, Dimensions, ActivityEngine.Label);
        _rowBox.SelectedItem = _rows; _colBox.SelectedItem = _cols;
        Fill(_countBox, new[] { 10, 20, 40, 80, 200, 1000 }, n => $"Top {n}");
        _countBox.SelectedItem = _rowCount;
        ToolTip.SetTip(_measureBox, "What the figures count");
        ToolTip.SetTip(_bots, "Leave out automation accounts (dependabot, renovate, build services). AI agents are not bots.");
        ToolTip.SetTip(_merges, "Count merge commits too (they change nothing of their own)");

        _refreshSoon = Ui.Debounced(Refresh, 150);
        _preset.SelectionChanged += (_, _) =>
        {
            if (_building || _preset.SelectedItem is not Preset { Range: { } range }) return;
            var (f, t) = range();
            SetRange(f, t, fromPreset: true);
        };
        _fromPicker.SelectedDateChanged += (_, _) => { if (!_building) SetRange(_fromPicker.SelectedDate?.Date, _to, false); };
        _toPicker.SelectedDateChanged += (_, _) => { if (!_building) SetRange(_from, _toPicker.SelectedDate?.Date, false); };
        _branches.SelectionChanged += (_, _) => { if (_building) return; _filter.Branches = (BranchScope)Math.Max(0, _branches.SelectedIndex); _refreshSoon(); };
        _ai.SelectionChanged += (_, _) => { if (_building) return; _filter.Ai = (AiScope)Math.Max(0, _ai.SelectedIndex); _refreshSoon(); };
        _measureBox.SelectionChanged += (_, _) => { if (!_building && _measureBox.SelectedItem is ActivityMeasure m) { _measure = m; _refreshSoon(); } };
        _rowBox.SelectionChanged += (_, _) => { if (!_building && _rowBox.SelectedItem is ActivityDimension d) { _rows = d; _refreshSoon(); } };
        _colBox.SelectionChanged += (_, _) => { if (!_building && _colBox.SelectedItem is ActivityDimension d) { _cols = d; _refreshSoon(); } };
        _countBox.SelectionChanged += (_, _) => { if (!_building && _countBox.SelectedItem is int n) { _rowCount = n; _refreshSoon(); } };
        _bots.IsCheckedChanged += (_, _) => { if (_building) return; _filter.HideBots = _bots.IsChecked == true; _refreshSoon(); };
        _merges.IsCheckedChanged += (_, _) => { if (_building) return; _filter.IncludeMerges = _merges.IsChecked == true; _refreshSoon(); };
        _search.TextChanged += (_, _) => { _filter.Text = _search.Text; _refreshSoon(); };

        var people = Ui.IconButton(Icons.People, "Choose which contributors are counted", () => _ = ChoosePeopleAsync(), "Contributors…");
        var swap = Ui.TextButton("⇄", () => { (_rows, _cols) = (_cols, _rows); _building = true; _rowBox.SelectedItem = _rows; _colBox.SelectedItem = _cols; _building = false; _refreshSoon(); }, "Swap rows and columns");
        _pivotBar = Ui.Row(8, Label("Rows"), _rowBox, swap, Label("Columns"), _colBox, _countBox);
        foreach (var (label, rows, cols) in new[] { ("Repos × days", ActivityDimension.Repository, ActivityDimension.Day), ("People × days", ActivityDimension.Contributor, ActivityDimension.Day),
                     ("People × repos", ActivityDimension.Contributor, ActivityDimension.Repository), ("Frameworks × weeks", ActivityDimension.Framework, ActivityDimension.Week),
                     ("Languages × repos", ActivityDimension.Language, ActivityDimension.Repository), ("Weekday × hour", ActivityDimension.Weekday, ActivityDimension.Hour) })
            _pivotBar.Children.Add(Ui.Apply(Ui.Link(label, () => { _rows = rows; _cols = cols; _building = true; _rowBox.SelectedItem = rows; _colBox.SelectedItem = cols; _building = false; _refreshSoon(); }), b => b.FontSize = 11.5));

        _viewTabs = new[] { "Heatmap", "Leaderboards", "Rhythm", "AI", "Frameworks & Languages", "Commits" }.Select((label, i) =>
        {
            var b = new Button { Content = label, Classes = { "tab" } };
            b.Click += (_, _) => { _view = (View)i; _refreshSoon(); };
            return b;
        }).ToArray();

        var filters = new WrapPanel
        {
            Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6,
            Children =
            {
                _preset,
                Ui.Row(4, _fromPicker, Clear("Back to the beginning", () => SetRange(null, _to, false)), Label("to"), _toPicker, Clear("Up to now", () => SetRange(_from, null, false))),
                _branches, _ai, _measureBox, _peopleActive, _reposActive, people, _bots, _merges, _search,
            },
        };
        _scroll = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(20, 8, 20, 24), Children = { _tiles, _body } }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = new DockPanel
        {
            Children =
            {
                new StackPanel
                {
                    Margin = new Thickness(20, 10, 20, 0), Spacing = 6,
                    Children = { filters, _chips, new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Row(0, _viewTabs) }, _pivotBar, _hover },
                }.Dock(Dock.Top),
                _scroll,
            },
        };
        Activity.DataChanged += () => Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible) _refreshSoon(); });
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Dim, FontSize = 12 };

    private static void Fill<T>(ComboBox box, IEnumerable<T> items, Func<T, string> label)
    {
        box.ItemTemplate = new FuncDataTemplate<T>((x, _) => new TextBlock { Text = x is null ? "" : label(x) }, false);
        box.ItemsSource = items.ToList();
    }

    private static Button Clear(string tip, Action onClick)
    {
        var b = new Button { Content = new PathIcon { Data = Icons.Close, Width = 10, Height = 10 }, Classes = { "flat" }, Padding = new Thickness(5), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Shows a range of days (null ends are open). A start before what has been read reads further back.</summary>
    private void SetRange(DateTime? from, DateTime? to, bool fromPreset)
    {
        if (from is { } f && to is { } t && f > t) (from, to) = (t, f);
        _from = from; _to = to;
        _building = true;
        _fromPicker.SelectedDate = from;
        _toPicker.SelectedDate = to;
        if (!fromPreset) _preset.SelectedItem = Presets.FirstOrDefault(p => p.Range?.Invoke() is var (pf, pt) && pf == from && pt == to) ?? Presets[^1];
        _building = false;
        if (Activity.ReadBackTo(from is { } d ? new DateTimeOffset(d) : null))
            Ui.Toast(this, "Reading further back", from is { } d2 ? $"Git Activity now reads from {d2:d MMM yyyy}; the repositories are being fetched again." : "Git Activity now reads every repository's whole history; this can take a while.");
        _refreshSoon();
    }

    private ActivityFilter Ranged(ActivityFilter f)
    {
        f.From = _from is { } from ? new DateTimeOffset(from) : DateTimeOffset.MinValue;
        f.To = _to is { } to ? new DateTimeOffset(to.AddDays(1)).AddTicks(-1) : DateTimeOffset.MaxValue;
        return f;
    }

    // ---- narrowing down ----

    private void Only(ActivityDimension d, string key)
    {
        _filter.Only[d] = [key];
        _refreshSoon();
    }

    private void Exclude(string person) { _filter.ExcludedPeople.Add(person); _refreshSoon(); }

    private void ShowChips(ActivityEngine engine)
    {
        _chips.Children.Clear();
        foreach (var (d, values) in _filter.Only.Where(kv => kv.Value.Count > 0).ToList())
            foreach (var v in values)
                _chips.Children.Add(Chip($"{ActivityEngine.Label(d)}: {engine.Display(d, v)}", () => { values.Remove(v); if (values.Count == 0) _filter.Only.Remove(d); _refreshSoon(); }, "accentDeep"));
        foreach (var p in _filter.ExcludedPeople.ToList())
            _chips.Children.Add(Chip($"Without {engine.Display(ActivityDimension.Contributor, p)}", () => { _filter.ExcludedPeople.Remove(p); _refreshSoon(); }, "delBg"));
        if (_chips.Children.Count > 1)
            _chips.Children.Add(Ui.Apply(Ui.Link("Clear all", () => { _filter.Only.Clear(); _filter.ExcludedPeople.Clear(); _refreshSoon(); }), b => b.Margin = new Thickness(6, 0)));
        _chips.IsVisible = _chips.Children.Count > 0;
    }

    private static Border Chip(string text, Action remove, string color)
    {
        var x = new Button { Content = new PathIcon { Data = Icons.Close, Width = 9, Height = 9 }, Classes = { "flat" }, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        x.Click += (_, _) => remove();
        ToolTip.SetTip(x, "Remove this filter");
        return new Border
        {
            Background = Ui.Tk(color), CornerRadius = Ui.R(10), Padding = new Thickness(10, 1, 2, 1), Margin = new Thickness(0, 0, 6, 0),
            Child = Ui.Row(2, new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Tk("textStrong") }, x),
        };
    }

    private async Task ChoosePeopleAsync()
    {
        var engine = Activity.Engine();
        var f = _filter.Clone();
        f.ExcludedPeople.Clear();
        f.Only.Remove(ActivityDimension.Contributor);
        Ranged(f);
        var everyone = await Task.Run(() => engine.Top(engine.Filter(f), ActivityDimension.Contributor, ActivityMeasure.Commits, f, 100000));
        var checks = everyone.Select(e => new CheckBox
        {
            Content = $"{e.Name}   ({e.Tally.Commits:N0} commits{(e.Tally.AiCommits > 0 ? $", {HeatmapControl.Pct(e.Tally.AiCommits, e.Tally.Commits)} with AI" : "")})",
            IsChecked = !_filter.ExcludedPeople.Contains(e.Key), Tag = e.Key, FontSize = 12.5,
        }).ToList();
        var list = new StackPanel { Spacing = 2 };
        foreach (var c in checks) list.Children.Add(c);
        var search = new TextBox { PlaceholderText = "Find a contributor" };
        search.TextChanged += (_, _) => { foreach (var c in checks) c.IsVisible = string.IsNullOrEmpty(search.Text) || (c.Content as string)!.Contains(search.Text, StringComparison.OrdinalIgnoreCase); };
        var always = new CheckBox { Content = "Also leave the unticked ones out for good (saved in the settings)", FontSize = 12 };
        var body = Ui.Column(8,
            Ui.Text("Untick the contributors to leave out. Clicking a contributor anywhere else shows only them.", "dim", 12),
            search,
            Ui.Row(8, Ui.TextButton("All", () => { foreach (var c in checks.Where(c => c.IsVisible)) c.IsChecked = true; }), Ui.TextButton("None", () => { foreach (var c in checks.Where(c => c.IsVisible)) c.IsChecked = false; })),
            new ScrollViewer { Height = 380, Content = list },
            always);
        var saved = await Ui.FormAsync(Ui.Owner(this), "Contributors", body, () =>
        {
            _filter.ExcludedPeople.Clear();
            foreach (var c in checks.Where(c => c.IsChecked != true)) _filter.ExcludedPeople.Add((string)c.Tag!);
            if (always.IsChecked == true)
            {
                var names = checks.Where(c => c.IsChecked != true).Select(c => (string)c.Tag!).ToList();
                Activity.Update(cfg => { foreach (var n in names) if (!cfg.ExcludedContributors.Contains(n, StringComparer.OrdinalIgnoreCase)) cfg.ExcludedContributors.Add(n); });
                _filter.ExcludedPeople.Clear();
            }
            return Task.FromResult<string?>(null);
        }, "Apply", 520);
        if (saved) _refreshSoon();
    }

    /// <summary>The right-click menu for one value of a dimension.</summary>
    private ContextMenu Menu(ActivityEngine engine, ActivityDimension d, string key)
    {
        var menu = new ContextMenu();
        var name = engine.Display(d, key);
        menu.Items.Add(Ui.Item("Look at Just This", () => Only(d, key), icon: Icons.Magnify));
        switch (d)
        {
            case ActivityDimension.Contributor:
                menu.Items.Add(Ui.Item("Leave Out", () => Exclude(key), icon: Icons.MinusCircle));
                menu.Items.Add(Ui.Item("Always Leave Out (in the settings)", () => Activity.Update(c => c.ExcludedContributors.Add(key))));
                menu.Items.Add(Ui.Item("Heatmap of Their Work", () => { Only(d, key); _view = View.Heatmap; _rows = ActivityDimension.Repository; _cols = ActivityDimension.Day; SyncPivot(); }));
                break;
            case ActivityDimension.Repository when Activity.FindRepo(key) is { } repo:
                menu.Items.Add(Ui.Item("Open in Repositories", () => ActivityToRepositories.OpenAsync(this, repo, null), icon: Icons.SourceBranch));
                menu.Items.Add(Ui.Item("Open in the Browser", () => ActivityToRepositories.Browse(repo, null), repo.WebUrl is not null, Icons.OpenInNew));
                menu.Items.Add(Ui.Item("Read Again Now", () => Activity.ScanAsync(discover: false, only: [repo.Key], force: true), icon: Icons.Refresh));
                menu.Items.Add(Ui.Item("Stop Reading This Repository", () => Activity.Update(c => c.DisabledRepositories.Add(repo.Key))));
                break;
            case ActivityDimension.Branch:
                var (repoKey, branch) = ActivityEngine.SplitBranch(key);
                if (Activity.FindRepo(repoKey) is { } r)
                {
                    menu.Items.Add(Ui.Item("Open Branch in Repositories", () => ActivityToRepositories.OpenAsync(this, r, "branch:" + branch), icon: Icons.SourceBranch));
                    menu.Items.Add(Ui.Item("Open Branch in the Browser", () => ActivityToRepositories.Browse(r, "branch:" + branch), r.WebUrl is not null, Icons.OpenInNew));
                }
                var mainline = Glob.Any(Activity.Config.MainlineBranches, branch);
                menu.Items.Add(Ui.Item(mainline ? $"“{branch}” Is Mainline (set in the settings)" : $"Count “{branch}” as Mainline", () => Activity.Update(c => c.MainlineBranches.Add(branch)), !mainline));
                break;
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Copy Name", () => App.Services.Host.SetClipboardAsync(name), icon: Icons.ContentCopy));
        return menu;
    }

    private void SyncPivot()
    {
        _building = true;
        _rowBox.SelectedItem = _rows; _colBox.SelectedItem = _cols;
        _building = false;
        _refreshSoon();
    }

    // ---- building ----

    private sealed record Leaders(ActivitySummary Summary, Dictionary<string, List<TopEntry>> Lists, List<ActivityMatrix> Maps, List<Selected>? Commits);

    public void Refresh()
    {
        if (!IsEffectivelyVisible && _body.Content is not null) return;
        var generation = ++_generation;
        Ranged(_filter);
        var filter = _filter.Clone();
        var (view, measure, rows, cols, count) = (_view, _measure, _rows, _cols, _rowCount);
        var top = Math.Max(5, Activity.Config.TopCount);
        for (var i = 0; i < _viewTabs.Length; i++) _viewTabs[i].Classes.Set("on", i == (int)view);
        _pivotBar.IsVisible = view == View.Heatmap;
        Task.Run(() =>
        {
            Activity.EnsureLoaded();
            var engine = Activity.Engine();
            var selected = engine.Filter(filter);
            var summary = engine.Summarize(selected);
            // Long ranges are shown by week or month: thousands of day columns cannot be read.
            var start = filter.OpenStart ? summary.First ?? DateTimeOffset.Now : filter.From;
            var end = filter.OpenEnd ? DateTimeOffset.Now : filter.To;
            var span = (end - start).TotalDays;
            ActivityDimension Grain(ActivityDimension d) => d switch
            {
                ActivityDimension.Day when span > 1100 => ActivityDimension.Month,
                ActivityDimension.Day when span > 183 => ActivityDimension.Week,
                ActivityDimension.Week when span > 1100 => ActivityDimension.Month,
                _ => d,
            };
            var lists = new Dictionary<string, List<TopEntry>>();
            var maps = new List<ActivityMatrix>();
            List<Selected>? commits = null;
            switch (view)
            {
                case View.Heatmap:
                    maps.Add(engine.Pivot(selected, Grain(rows), Grain(cols), measure, filter, Grain(rows) != rows ? int.MaxValue : count));
                    break;
                case View.Leaders:
                    foreach (var (name, d) in new[] { ("Contributors", ActivityDimension.Contributor), ("Repositories", ActivityDimension.Repository), ("Branches", ActivityDimension.Branch),
                                 ("Languages", ActivityDimension.Language), ("Frameworks", ActivityDimension.Framework), ("File types", ActivityDimension.FileType) })
                        lists[name] = engine.Top(selected, d, measure, filter, top);
                    break;
                case View.Rhythm:
                    maps.Add(engine.Pivot(selected, ActivityDimension.Weekday, ActivityDimension.Hour, measure, filter, 7));
                    maps.Add(engine.Pivot(selected, ActivityDimension.Contributor, ActivityDimension.Hour, measure, filter, top));
                    maps.Add(engine.Pivot(selected, ActivityDimension.Contributor, ActivityDimension.Weekday, measure, filter, top));
                    maps.Add(engine.Pivot(selected, ActivityDimension.Repository, Grain(ActivityDimension.Week), measure, filter, top));
                    break;
                case View.Ai:
                    maps.Add(engine.Pivot(selected, ActivityDimension.AiUse, Grain(ActivityDimension.Week), measure, filter, 3));
                    lists["AI tools"] = engine.Top(selected.Where(s => s.Commit.WithAi).ToList(), ActivityDimension.AiTool, measure, filter, top);
                    lists["AI models"] = engine.Top(selected.Where(s => s.Commit.WithAi).ToList(), ActivityDimension.AiModel, measure, filter, top);
                    lists["Who uses AI most"] = engine.Top(selected.Where(s => s.Commit.WithAi).ToList(), ActivityDimension.Contributor, measure, filter, top);
                    lists["Where AI is used most"] = engine.Top(selected.Where(s => s.Commit.WithAi).ToList(), ActivityDimension.Repository, measure, filter, top);
                    maps.Add(engine.Pivot(selected.Where(s => s.Commit.WithAi).ToList(), ActivityDimension.Contributor, ActivityDimension.AiTool, measure, filter, top));
                    break;
                case View.Tech:
                    maps.Add(engine.Pivot(selected, ActivityDimension.Framework, Grain(ActivityDimension.Week), measure, filter, top * 2));
                    lists["Kinds of framework"] = engine.Top(selected, ActivityDimension.FrameworkCategory, measure, filter, top);
                    lists["Languages"] = engine.Top(selected, ActivityDimension.Language, measure, filter, top);
                    lists["Kinds of file"] = engine.Top(selected, ActivityDimension.FileCategory, measure, filter, top);
                    lists["Code or tests"] = engine.Top(selected, ActivityDimension.CodeOrTests, measure, filter, 3);
                    maps.Add(engine.Pivot(selected, ActivityDimension.Contributor, ActivityDimension.Framework, measure, filter, top));
                    maps.Add(engine.Pivot(selected, ActivityDimension.Language, ActivityDimension.Repository, measure, filter, top));
                    break;
                case View.Commits:
                    commits = selected.OrderByDescending(s => s.Commit.When).Take(3000).ToList();
                    break;
            }
            return (engine, new Leaders(summary, lists, maps, commits), selected.Count);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation) return;
            if (t.IsFaulted) { _body.Content = Ui.Text("Could not add up the figures: " + t.Exception?.GetBaseException().Message, "dim"); return; }
            var (engine, result, total) = t.Result;
            ShowChips(engine);
            Show(engine, view, measure, result, total);
        }));
    }

    private void Show(ActivityEngine engine, View view, ActivityMeasure measure, Leaders r, int total)
    {
        ShowTiles(r.Summary);
        if (Activity.Config.Sources.Count == 0 || Activity.Repos.Count == 0 && !Activity.Progress.Running)
        {
            _body.Content = Ui.Column(10,
                Ui.Text(Activity.Config.Sources.Count == 0 ? "No repositories to look at yet" : "No repositories listed yet", "h2"),
                Ui.Text("Add a GitHub organisation, an Azure DevOps organisation or project, a folder of repositories, or single repositories under Git Activity Settings, then Read Now.", "dim"),
                Ui.Row(8, Ui.TextButton("Git Activity Settings", () => OpenSettings?.Invoke()), Ui.TextButton("Read Now", () => _ = Activity.ScanAsync())));
            return;
        }
        if (total == 0)
        {
            _body.Content = Ui.Column(10, Ui.Text("Nothing in this view", "h2"),
                Ui.Text(Activity.Progress.Running ? "Still reading; figures appear as repositories are read." : "No commits match: try a longer period, or remove a filter.", "dim"));
            return;
        }
        var note = $"by {ActivityEngine.Label(measure).ToLowerInvariant()}";
        switch (view)
        {
            case View.Heatmap:
                _body.Content = Map(engine, r.Maps[0], null);
                break;
            case View.Leaders:
                _body.Content = Wrap(r.Lists.Select(kv => Board(engine, kv.Key, note, kv.Value, kv.Key switch
                {
                    "Contributors" => ActivityDimension.Contributor, "Repositories" => ActivityDimension.Repository, "Branches" => ActivityDimension.Branch,
                    "Languages" => ActivityDimension.Language, "Frameworks" => ActivityDimension.Framework, _ => ActivityDimension.FileType,
                }, measure)));
                break;
            case View.Rhythm:
                _body.Content = Ui.Column(14,
                    Section("When the work is done", "Each commit at the hour and weekday it was written, in its author's own time zone.", Map(engine, r.Maps[0], null, 120)),
                    Section("Each contributor's hours", null, Map(engine, r.Maps[1], null)),
                    Section("Each contributor's week", null, Map(engine, r.Maps[2], null)),
                    Section("Momentum", "Over time: which repositories are speeding up and which are winding down.", Map(engine, r.Maps[3], null)));
                break;
            case View.Ai:
                var s = r.Summary;
                _body.Content = Ui.Column(14,
                    Section("AI help over time", $"{HeatmapControl.Pct(s.AiCommits, s.Commits)} of commits had AI help ({s.AgentCommits:N0} were written by an AI agent); {s.AiPeople} of {s.People} contributors used AI. " +
                        "AI is recognised from Co-authored-by trailers, “Generated with” lines and agent accounts; the person credited is the human, not the tool.", Map(engine, r.Maps[0], null, 120)),
                    Wrap(r.Lists.Select(kv => Board(engine, kv.Key, note, kv.Value, kv.Key switch
                    {
                        "AI tools" => ActivityDimension.AiTool, "AI models" => ActivityDimension.AiModel, "Who uses AI most" => ActivityDimension.Contributor, _ => ActivityDimension.Repository,
                    }, measure))),
                    Section("Who uses which AI tool", null, Map(engine, r.Maps[1], null)));
                break;
            case View.Tech:
                _body.Content = Ui.Column(14,
                    Section("Frameworks over time", "Frameworks are recognised from each project's manifests (package.json, .csproj, packages.config, pom.xml, requirements.txt, go.mod, Cargo.toml…) and tell-tale files, as they were when each commit was made: work done before a move from Angular to React counts as Angular. Every file changed counts toward the frameworks of the projects it is in.",
                        Map(engine, r.Maps[0], null)),
                    Wrap(r.Lists.Select(kv => Board(engine, kv.Key, note, kv.Value, kv.Key switch
                    {
                        "Kinds of framework" => ActivityDimension.FrameworkCategory, "Languages" => ActivityDimension.Language, "Kinds of file" => ActivityDimension.FileCategory, _ => ActivityDimension.CodeOrTests,
                    }, measure))),
                    Section("Who works in which framework", null, Map(engine, r.Maps[1], null)),
                    Section("Languages in each repository", null, Map(engine, r.Maps[2], null)));
                break;
            case View.Commits:
                _body.Content = Commits(engine, r.Commits!, total);
                break;
        }
    }

    private void ShowTiles(ActivitySummary s)
    {
        _tiles.Children.Clear();
        _tiles.Children.Add(StatTile.Build(s.Commits.ToString("N0"), "commits", s.First is null ? null : $"{s.First:d MMM} – {s.Last:d MMM}"));
        _tiles.Children.Add(StatTile.Build(s.People.ToString("N0"), "contributors"));
        _tiles.Children.Add(StatTile.Build(s.Repositories.ToString("N0"), "active repositories", $"{s.Branches:N0} branches"));
        _tiles.Children.Add(StatTile.Build($"+{Heat.Number(s.Added)}", "lines added", $"−{Heat.Number(s.Deleted)} removed · {s.Files:N0} files", "ok"));
        _tiles.Children.Add(StatTile.Build(HeatmapControl.Pct(s.AiCommits, s.Commits), "with AI help", $"{s.AgentCommits:N0} by agents · {s.AiPeople} people", "purple",
            "Commits that show AI help: Co-authored-by an AI, “Generated with …”, or written by an AI agent account"));
        _tiles.Children.Add(StatTile.Build(s.Languages.ToString("N0"), "languages", $"{s.Frameworks:N0} frameworks"));
        if (s.BusiestDay is not null) _tiles.Children.Add(StatTile.Build(s.BusiestDay, "busiest day", $"busiest hour {s.BusiestHour}"));
    }

    private static Control Section(string title, string? note, Control content) => Ui.Column(4,
        Ui.Text(title, "h2"), Ui.Apply(Ui.Text(note ?? "", "dim", 12), t => { t.IsVisible = note is not null; t.MaxWidth = 1000; t.HorizontalAlignment = HorizontalAlignment.Left; }), content);

    private static WrapPanel Wrap(IEnumerable<Control> items)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) w.Children.Add(i);
        return w;
    }

    private Control Map(ActivityEngine engine, ActivityMatrix m, string? title, double labelWidth = 230)
    {
        var heat = new HeatmapControl { LabelWidth = labelWidth, HorizontalAlignment = HorizontalAlignment.Left, FitWidth = Math.Max(500, _scroll.Viewport.Width - 64) };
        heat.Set(m, engine.Display);
        heat.Hovered += text => _hover.Text = text;
        heat.RowClicked += key => Only(m.RowDimension, key);
        heat.CellClicked += (row, col) =>
        {
            _filter.Only[m.RowDimension] = [row];
            _filter.Only[m.ColumnDimension] = [col];
            _refreshSoon();
        };
        heat.RowMenu = key => Menu(engine, m.RowDimension, key);
        var stack = new StackPanel { Spacing = 4 };
        if (title is not null) stack.Children.Add(Ui.Text(title, "h2"));
        // Years of days do not fit: the map scrolls sideways rather than squeezing them away.
        stack.Children.Add(new Border
        {
            Classes = { "card" }, Padding = new Thickness(10, 8),
            Child = new ScrollViewer { Content = heat, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        });
        var foot = (ActivityEngine.IsTime(m.ColumnDimension) ? $"By {ActivityEngine.Label(m.ColumnDimension).ToLowerInvariant()} · " : "") + $"{m.Rows.Count} of {m.Rows.Count + m.HiddenRows} {ActivityEngine.Label(m.RowDimension).ToLowerInvariant()} rows · {Heat.Number(m.Total)} {ActivityEngine.Label(m.Measure).ToLowerInvariant()}" +
                   (m.HiddenRows > 0 ? " · choose a larger Top for more" : "") + " · click a row or cell to narrow the view, right-click a row for more";
        stack.Children.Add(Ui.Text(foot, "dim", 11));
        return stack;
    }

    private Border Board(ActivityEngine engine, string title, string note, List<TopEntry> entries, ActivityDimension d, ActivityMeasure measure) =>
        Leaderboard.Build(title, note, entries, measure, e => Only(d, e.Key), e => Menu(engine, d, e.Key), e => d switch
        {
            ActivityDimension.Branch when e.Mainline => Ui.Chip("mainline", Ui.Tk("faint"), 9.5),
            ActivityDimension.Framework when e.Key != ActivityEngine.None => Ui.Chip(engine.FrameworkCategory(e.Key), Ui.Tk("accentDeep"), 9.5),
            ActivityDimension.AiModel or ActivityDimension.AiTool => null,
            _ => null,
        });

    private Control Commits(ActivityEngine engine, List<Selected> commits, int total)
    {
        var list = new ListBox { Classes = { "plain" }, Height = Math.Max(300, Bounds.Height - 260) };
        list.ItemTemplate = new FuncDataTemplate<Selected>((s, _) => s is null ? new Panel() : CommitRow(engine, s), false);
        list.ItemsSource = commits;
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is Selected s && Activity.FindRepo(s.Commit.RepoKey) is { } repo) _ = ActivityToRepositories.OpenAsync(this, repo, "commit:" + s.Commit.Hash); };
        return Ui.Column(6,
            Ui.Text(total > commits.Count ? $"The newest {commits.Count:N0} of {total:N0} commits. Double-click one to open it in Repositories; right-click for more." : $"{total:N0} commits. Double-click one to open it in Repositories; right-click for more.", "dim", 12),
            new Border { Classes = { "card" }, Padding = new Thickness(0), Child = list });
    }

    private Control CommitRow(ActivityEngine engine, Selected s)
    {
        var c = s.Commit;
        var repo = Activity.FindRepo(c.RepoKey);
        var line1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line1.Children.Add(new TextBlock { Text = c.Hash[..Math.Min(9, c.Hash.Length)], FontFamily = Ui.Mono, FontSize = 11.5, Foreground = Ui.Tk("faint"), VerticalAlignment = VerticalAlignment.Center });
        line1.Children.Add(new TextBlock { Text = c.Subject, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 720, VerticalAlignment = VerticalAlignment.Center });
        if (c.Agent) line1.Children.Add(Ui.Chip("agent", Ui.Tk("purple"), 9.5));
        foreach (var tool in c.AiTools) line1.Children.Add(Ui.Chip(tool, Ui.Tk("purple"), 9.5));
        foreach (var model in c.AiModels) line1.Children.Add(Ui.Chip(model, Ui.Tk("accentDeep"), 9.5));
        if (c.Bot) line1.Children.Add(Ui.Chip("bot", Ui.Tk("faint"), 9.5));
        if (c.Merge) line1.Children.Add(Ui.Chip("merge", Ui.Tk("faint"), 9.5));
        var who = engine.Display(ActivityDimension.Contributor, s.Person) + (c.PersonName != c.AuthorName ? $" (via {c.AuthorName})" : "");
        var frameworks = s.Parts.SelectMany(p => p.Frameworks.Split('|', StringSplitOptions.RemoveEmptyEntries)).Distinct().Take(4).ToList();
        var line2 = $"{who}  ·  {repo?.Name ?? c.RepoKey}  ·  {c.Branch}{(engine.IsMainline(c) ? " (mainline)" : "")}  ·  {c.When:ddd d MMM HH:mm}  ·  " +
                    (c.Boundary ? "(start of the period: changes not counted)" : $"+{s.Added:N0} −{s.Deleted:N0} in {s.Files:N0} files") +
                    (frameworks.Count > 0 ? "  ·  " + string.Join(", ", frameworks) : "");
        var row = new StackPanel { Margin = new Thickness(12, 4), Children = { line1, new TextBlock { Text = line2, FontSize = 11.5, Foreground = Ui.Dim, TextTrimming = TextTrimming.CharacterEllipsis } } };
        var menu = new ContextMenu();
        if (repo is not null)
        {
            menu.Items.Add(Ui.Item("Open Commit in Repositories", () => ActivityToRepositories.OpenAsync(this, repo, "commit:" + c.Hash), icon: Icons.SourceBranch));
            menu.Items.Add(Ui.Item("Open Branch in Repositories", () => ActivityToRepositories.OpenAsync(this, repo, "branch:" + c.Branch)));
            menu.Items.Add(Ui.Item("Open Repository in Repositories", () => ActivityToRepositories.OpenAsync(this, repo, null)));
            menu.Items.Add(Ui.Item("Open Commit in the Browser", () => ActivityToRepositories.Browse(repo, "commit:" + c.Hash), repo.WebUrl is not null, Icons.OpenInNew));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Ui.Item($"Just {engine.Display(ActivityDimension.Contributor, s.Person)}", () => Only(ActivityDimension.Contributor, s.Person)));
        menu.Items.Add(Ui.Item($"Just {repo?.Name ?? "This Repository"}", () => Only(ActivityDimension.Repository, c.RepoKey)));
        menu.Items.Add(Ui.Item($"Just {c.Branch}", () => Only(ActivityDimension.Branch, c.RepoKey + "\u001f" + c.Branch)));
        menu.Items.Add(Ui.Item("Copy Hash", () => App.Services.Host.SetClipboardAsync(c.Hash), icon: Icons.ContentCopy));
        row.ContextMenu = menu;
        ToolTip.SetTip(row, $"{c.Subject}\n{who} <{c.PersonEmail}>{(c.PersonName != c.AuthorName ? $"\nAuthor: {c.AuthorName} <{c.AuthorEmail}>" : "")}\n{c.When:yyyy-MM-dd HH:mm zzz}");
        return row;
    }
}

/// <summary>Git Activity Settings: the sources, the repositories they give, and how they are read. Everything is saved in git-activity.json.</summary>
public sealed class GitActivitySettingsView : UserControl
{
    private readonly StackPanel _sources = new() { Spacing = 6 };
    private readonly ListBox _repos = new() { Classes = { "plain" }, Height = 420 };
    private readonly TextBox _repoSearch = new() { PlaceholderText = "Find a repository", Width = 260 };
    private readonly ComboBox _repoShow = new() { MinWidth = 160 };
    private readonly TextBlock _repoCount = new() { FontSize = 12, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _readFrom = new() { Width = 120, PlaceholderText = "(period)" };
    private readonly TextBox _activePeople = Num(), _activeRepos = Num();
    private readonly TextBox _days = Num(), _top = Num(), _parallel = Num(), _refresh = Num(), _maxMb = Num();
    private readonly ComboBox _detail = new() { MinWidth = 200, ItemsSource = new[] { "lines", "files" } };
    private readonly TextBox _mirror = new() { PlaceholderText = "(the data folder's git-activity/mirrors)" };
    private readonly CheckBox _hideBots = new() { Content = "Hide bots by default" }, _byName = new() { Content = "Merge contributors who share a name" };
    private readonly TextBox _mainline = Lines(), _excluded = Lines(), _aliases = Lines(), _excludePaths = Lines();
    private readonly TextBlock _error = new() { Foreground = Ui.Tk("errorText"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Action _refreshSoon;

    public Action? OpenFigures { get; set; }

    private static GitActivityService Activity => App.Services.Activity;

    private static TextBox Num() => new() { Width = 120 };
    private static TextBox Lines() => new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 120, FontFamily = Ui.Mono, FontSize = 12 };

    public GitActivitySettingsView()
    {
        _refreshSoon = Ui.Debounced(Refresh, 200);
        _repoShow.ItemsSource = new[] { "All repositories", "With commits", "Not read yet", "Could not be read", "Switched off" };
        _repoShow.SelectedIndex = 0;
        _repoShow.SelectionChanged += (_, _) => ShowRepos();
        _repoSearch.TextChanged += (_, _) => ShowRepos();
        _repos.ItemTemplate = new FuncDataTemplate<ActivityRepo>((r, _) => r is null ? new Panel() : RepoRow(r), false);

        var add = Ui.Row(6,
            Ui.IconButton(Icons.Plus, ActivitySources.For(ActivitySource.GitHub).Description, () => _ = EditSourceAsync(new ActivitySource { Kind = ActivitySource.GitHub }, true), "GitHub"),
            Ui.IconButton(Icons.Plus, ActivitySources.For(ActivitySource.AzureDevOps).Description, () => _ = EditSourceAsync(new ActivitySource { Kind = ActivitySource.AzureDevOps }, true), "Azure DevOps"),
            Ui.IconButton(Icons.Plus, ActivitySources.For(ActivitySource.Local).Description, () => _ = EditSourceAsync(new ActivitySource { Kind = ActivitySource.Local }, true), "Local folder"),
            Ui.IconButton(Icons.Plus, ActivitySources.For(ActivitySource.Git).Description, () => _ = EditSourceAsync(new ActivitySource { Kind = ActivitySource.Git }, true), "Git URLs"),
            Ui.IconButton(Icons.Plus, ActivitySources.For(ActivitySource.RepositoriesPage).Description, () => _ = EditSourceAsync(new ActivitySource { Kind = ActivitySource.RepositoriesPage }, true), "Repositories page"));

        var sourcesCard = Card("Sources", "Where the repositories come from. Organisations are listed again each time Git Activity reads, so new repositories are picked up by themselves.",
            Ui.Column(10, _sources, add));
        var reposCard = Card("Repositories", "Everything the sources gave. Untick a repository to stop reading it; right-click for more.",
            Ui.Column(8, Ui.Row(8, _repoSearch, _repoShow, _repoCount,
                    Ui.TextButton("All On", () => SetAll(true), "Switch on every repository shown"), Ui.TextButton("All Off", () => SetAll(false), "Switch off every repository shown")),
                new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = Ui.R(4), Child = _repos }));

        _detail.ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s == "lines" ? "Lines and files" : "Files only (less to download)" }, false);
        var options = new WrapPanel
        {
            Orientation = Orientation.Horizontal, ItemSpacing = 18, LineSpacing = 12,
            Children =
            {
                Ui.Field("Period (days)", _days, "How far back to read. 61 is about two months."),
                Ui.Field("Or read from", _readFrom, "A date (yyyy-MM-dd), or all for the whole history. Empty: the period."),
                Ui.Field("Top", _top, "Entries per leaderboard."),
                Ui.Field("Contributors active in (days)", _activePeople, "Start by hiding contributors with no commit in this many days. 0: show everyone."),
                Ui.Field("Repositories active in (days)", _activeRepos, "Start by hiding repositories with no commit in this many days. 0: show all."),
                Ui.Field("At the same time", _parallel, "Repositories read at once."),
                Ui.Field("Refresh every (minutes)", _refresh, "0: only on Read Now."),
                Ui.Field("Detail", _detail, "Files only is for very large organisations: no line counts."),
                Ui.Field("Lines up to (MB)", _maxMb, "Bigger repositories are read as files only. 0: no limit."),
            },
        };
        foreach (var f in options.Children.OfType<StackPanel>()) f.Width = 220;
        var optionsCard = Card("Reading", null, Ui.Column(12, options, Ui.Field("Mirror folder", _mirror, "Where copies of remote repositories are kept (shallow, only the period)."),
            Ui.Row(18, _hideBots, _byName)));
        var listsCard = Card("Lists", "One per line. Branch and path patterns: * within a folder, ** across folders.", new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,16,*"), RowDefinitions = new RowDefinitions("Auto,12,Auto"),
            Children =
            {
                Ui.Field("Mainline branches", _mainline, "Branches that are not feature work. Each repository's default branch always counts."),
                Ui.Field("Contributors left out", _excluded, "Names or email addresses.").At(0, 2),
                Ui.Field("One person, several identities", _aliases, "Name = email, other email, other name").At(2, 0),
                Ui.Field("Paths not counted", _excludePaths, "Lock files, generated and vendored files.").At(2, 2),
            },
        });
        var advanced = Card("In the file", null, Ui.Column(6,
            Ui.Text("AI rules (aiRules), extra frameworks (frameworks) and file types (languages) are set in git-activity.json. For example:", "dim", 12),
            new SelectableTextBlock
            {
                FontFamily = Ui.Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Text = "\"aiRules\": [{ \"tool\": \"Our Bot\", \"pattern\": \"ourbot@corp\\\\.com\", \"field\": \"author\", \"agent\": true }],\n" +
                       "\"frameworks\": [{ \"name\": \"Our Platform\", \"category\": \"Platform\", \"packages\": [\"Corp.Platform.*\"], \"files\": [\"platform/**\"] }],\n" +
                       "\"languages\": { \".xyz\": \"Our DSL\" }",
            },
            Ui.Text($"{Languages.KnownKeys} file types and {Frameworks.BuiltIn.Count} frameworks are known without any of this.", "dim", 11.5)));

        var save = Ui.IconButton(Icons.ContentSave, "Save the reading options and lists", Save, "Save");
        save.Classes.Add("accent");
        var actions = Ui.Row(6, save,
            Ui.IconButton(Icons.Pencil, "Open git-activity.json in the editor", () => Ui.OpenInEditor(Activity.ConfigFile), "Edit File"),
            Ui.IconButton(Icons.FolderOpen, "Show the file and the mirrors in the file manager", () => Shell.ShowInFolder(Activity.ConfigFile), "Show Folder"),
            Ui.IconButton(Icons.Delete, "Remove saved figures and mirrors of repositories no longer listed", () =>
            {
                var n = Activity.Prune();
                Ui.Toast(this, "Cleaned up", n == 0 ? "Nothing to remove." : $"Removed {n} files and folders.");
            }, "Clean Up"),
            Ui.IconButton(Icons.ArrowLeft, "Back to the figures", () => OpenFigures?.Invoke(), "Figures"));
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20, 12, 20, 24), Spacing = 12, MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Left,
                Children = { Ui.Text(Activity.ConfigFile, "dim", 11.5), actions, _error, sourcesCard, reposCard, optionsCard, listsCard, advanced },
            },
        };
        Activity.ConfigChanged += () => Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible) _refreshSoon(); });
        Activity.ProgressChanged += _ => Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible) _refreshSoon(); });
    }

    private static Border Card(string title, string? note, Control body) => new()
    {
        Classes = { "card" }, Padding = new Thickness(16, 12),
        Child = Ui.Column(8, Ui.Text(title, "h2"), Ui.Apply(Ui.Text(note ?? "", "dim", 12), t => t.IsVisible = note is not null), body),
    };

    public void Refresh()
    {
        var c = Activity.Config;
        _error.Text = Activity.ConfigError is { } e ? $"git-activity.json could not be read, so the last good settings are in use: {e}" : "";
        _error.IsVisible = Activity.ConfigError is not null;
        _days.Text = c.Days.ToString(); _activePeople.Text = c.ActivePeopleDays.ToString(); _activeRepos.Text = c.ActiveReposDays.ToString(); _readFrom.Text = c.ReadFrom; _top.Text = c.TopCount.ToString(); _parallel.Text = c.Parallel.ToString(); _refresh.Text = c.RefreshMinutes.ToString(); _maxMb.Text = c.MaxLinesRepoMb.ToString();
        _detail.SelectedItem = c.Detail == "files" ? "files" : "lines";
        _mirror.Text = c.MirrorFolder;
        _hideBots.IsChecked = c.HideBots; _byName.IsChecked = c.MergeByName;
        _mainline.Text = string.Join('\n', c.MainlineBranches);
        _excluded.Text = string.Join('\n', c.ExcludedContributors);
        _excludePaths.Text = string.Join('\n', c.ExcludePaths);
        _aliases.Text = string.Join('\n', c.Aliases.Select(kv => $"{kv.Key} = {string.Join(", ", kv.Value)}"));
        ShowSources();
        ShowRepos();
    }

    private void Save()
    {
        static int Int(TextBox t, string what, int min, int max) =>
            int.TryParse(t.Text?.Trim(), out var v) && v >= min && v <= max ? v : throw new ArgumentException($"{what} must be a whole number from {min} to {max}.");
        static List<string> List(TextBox t) => (t.Text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        Ui.Try(this, () =>
        {
            var days = Int(_days, "Period", 1, 36600);
            var readFrom = _readFrom.Text?.Trim() ?? "";
            if (readFrom.Length > 0 && !readFrom.Equals("all", StringComparison.OrdinalIgnoreCase) && !DateTime.TryParse(readFrom, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("Read from must be a date (yyyy-MM-dd), all, or empty.");
            var top = Int(_top, "Top", 3, 500);
            var activePeople = Int(_activePeople, "Contributors active in", 0, 36600);
            var activeRepos = Int(_activeRepos, "Repositories active in", 0, 36600);
            var parallel = Int(_parallel, "At the same time", 1, 32);
            var refresh = Int(_refresh, "Refresh", 0, 100000);
            var maxMb = Int(_maxMb, "Lines up to", 0, 1000000);
            var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in List(_aliases))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"“{line}” should be: Name = email, other email");
                aliases[line[..eq].Trim()] = line[(eq + 1)..].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            }
            var before = Activity.Since;
            Activity.Update(c =>
            {
                c.Days = days; c.ActivePeopleDays = activePeople; c.ActiveReposDays = activeRepos; c.ReadFrom = readFrom.ToLowerInvariant() == "all" ? "all" : readFrom; c.TopCount = top; c.Parallel = parallel; c.RefreshMinutes = refresh; c.MaxLinesRepoMb = maxMb;
                c.Detail = _detail.SelectedItem as string ?? "lines"; c.MirrorFolder = _mirror.Text?.Trim() ?? "";
                c.HideBots = _hideBots.IsChecked == true; c.MergeByName = _byName.IsChecked == true;
                c.MainlineBranches = List(_mainline); c.ExcludedContributors = List(_excluded); c.ExcludePaths = List(_excludePaths); c.Aliases = aliases;
            });
            Ui.Toast(this, "Git Activity settings saved", Activity.Since < before ? "Reading reaches further back now: Read Now to fetch it." : "Branch patterns, paths and AI rules apply the next time repositories are read.");
        }, "Could not save");
    }

    // ---- sources ----

    private void ShowSources()
    {
        _sources.Children.Clear();
        var statuses = Activity.SourceStatuses.ToDictionary(s => s.Id);
        foreach (var source in Activity.Config.Sources)
        {
            var st = statuses.GetValueOrDefault(source.Id);
            var on = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, IsChecked = source.Enabled, VerticalAlignment = VerticalAlignment.Center };
            var id = source.Id;
            on.IsCheckedChanged += (_, _) => Activity.Update(c => { if (c.Sources.FirstOrDefault(s => s.Id == id) is { } s) s.Enabled = on.IsChecked == true; });
            var kept = Activity.Repos.Count(r => r.SourceId == source.Id);
            var copies = st is null ? 0 : st.Repositories - kept;
            var status = st is null ? "not listed yet" : st.Error is not null ? "could not list: " + st.Error
                : $"{kept:N0} repositor{(kept == 1 ? "y" : "ies")}{(copies > 0 ? $" ({copies:N0} more are copies of these, or of another source's, and are read once)" : "")} · listed {Ui.Ago(st.Listed?.LocalDateTime)}";
            var detail = new TextBlock { Text = $"{Kind(source.Kind)}  ·  {status}", FontSize = 11.5, Foreground = st?.Error is not null ? Ui.Tk("errorText") : Ui.Dim, TextWrapping = TextWrapping.Wrap };
            var row = new DockPanel
            {
                Children =
                {
                    Ui.Row(2,
                        Ui.IconButton(Icons.Refresh, "List and read this source's repositories now", () => _ = ReadSourceAsync(id)),
                        Ui.IconButton(Icons.Pencil, "Change this source", () => _ = EditSourceAsync(source, false)),
                        Ui.IconButton(Icons.Delete, "Remove this source", () => _ = RemoveSourceAsync(source))).Dock(Dock.Right),
                    on.Dock(Dock.Left),
                    new StackPanel { Margin = new Thickness(8, 0), Children = { new TextBlock { Text = source.Title, FontWeight = FontWeight.SemiBold }, detail } },
                },
            };
            _sources.Children.Add(new Border { Background = Ui.Tk("cell"), CornerRadius = Ui.R(4), Padding = new Thickness(10, 6), Child = row });
        }
        if (Activity.Config.Sources.Count == 0) _sources.Children.Add(Ui.Text("No sources yet: add one below.", "dim"));
    }

    private static string Kind(string kind) => kind switch
    {
        ActivitySource.GitHub => "GitHub", ActivitySource.AzureDevOps => "Azure DevOps", ActivitySource.Local => "Local folder", ActivitySource.Git => "Git URLs", _ => "Repositories page",
    };

    private async Task ReadSourceAsync(string id)
    {
        await Ui.TryAsync(this, async () =>
        {
            await Activity.DiscoverAsync(onlySource: id);
            var keys = Activity.Repos.Where(r => r.SourceId == id).Select(r => r.Key).ToList();
            _ = Activity.ScanAsync(discover: false, only: keys);
        }, "Could not list the source");
    }

    private async Task RemoveSourceAsync(ActivitySource source)
    {
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Remove source", $"Stop reading {source.Title}? Its repositories leave Git Activity (Clean Up removes their mirrors).", "Remove")) return;
        Activity.Update(c => c.Sources.RemoveAll(s => s.Id == source.Id));
        await Activity.DiscoverAsync();
    }

    private async Task EditSourceAsync(ActivitySource original, bool isNew)
    {
        var s = JsonUtil.To<ActivitySource>(JsonUtil.ToNode(original))!;
        TextBox Box(string? value, string? watermark = null) => new() { Text = value ?? "", PlaceholderText = watermark };
        static string Join(List<string> l) => string.Join('\n', l);
        static List<string> Split(string? t) => (t ?? "").Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        var name = Box(s.Name, "(shown name)");
        var owner = Box(s.Owner, "my-org");
        var api = Box(s.ApiUrl, "https://api.github.com");
        var tokenEnv = Box(s.TokenEnv, "GITHUB_TOKEN, then GH_TOKEN, then gh auth token");
        var org = Box(s.Organization, "contoso");
        var project = Box(s.Project, "(every project)");
        var baseUrl = Box(s.BaseUrl, "https://dev.azure.com");
        var patEnv = Box(s.PatEnv, "AZURE_DEVOPS_EXT_PAT, then ADO_PAT, then az login");
        var path = Box(s.Path, "~/src");
        var depth = Box(s.Depth.ToString());
        var repos = new TextBox { Text = Join(s.Repositories), AcceptsReturn = true, Height = 90, FontFamily = Ui.Mono, FontSize = 12 };
        var include = Box(string.Join(", ", s.Include), "(all)");
        var exclude = Box(string.Join(", ", s.Exclude), "*-archive, sandbox-*");
        var archived = new CheckBox { Content = "Include archived repositories", IsChecked = s.IncludeArchived };
        var forks = new CheckBox { Content = "Include forks", IsChecked = s.IncludeForks };
        var fetch = new CheckBox { Content = "Fetch before reading, so remote branches are current", IsChecked = s.Fetch };
        var browse = Ui.IconButton(Icons.FolderOpen, "Choose the folder", () => _ = Ui.TryAsync(this, async () =>
        {
            var picked = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Folder with repositories" });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } p) path.Text = p;
        }));
        var fields = new StackPanel { Spacing = 12 };
        fields.Children.Add(Ui.Text(ActivitySources.For(s.Kind).Description, "dim", 12));
        fields.Children.Add(Ui.Field("Name", name));
        switch (s.Kind)
        {
            case ActivitySource.GitHub:
                fields.Children.Add(Ui.Field("Organisation or user", owner));
                fields.Children.Add(Ui.Field("Only these repositories", repos, "Optional: owner/name or name, one per line. Empty lists every repository of the organisation."));
                fields.Children.Add(Ui.Field("API URL", api, "For GitHub Enterprise Server: https://your-host/api/v3"));
                fields.Children.Add(Ui.Field("Token variable", tokenEnv, "Environment variable with a token that can read the repositories (contents: read)."));
                fields.Children.Add(Ui.Row(18, archived, forks));
                break;
            case ActivitySource.AzureDevOps:
                fields.Children.Add(Ui.Field("Organisation", org, "dev.azure.com/<organisation>"));
                fields.Children.Add(Ui.Field("Project", project, "Empty reads the repositories of every project you can see."));
                fields.Children.Add(Ui.Field("Only these repositories", repos, "Optional: project/name or name, one per line."));
                fields.Children.Add(Ui.Field("Server URL", baseUrl, "For Azure DevOps Server (on-premises), which needs a PAT."));
                fields.Children.Add(Ui.Field("PAT variable", patEnv, "Without one, the Azure CLI's sign-in is used: run az login first."));
                fields.Children.Add(forks);
                break;
            case ActivitySource.Local:
                fields.Children.Add(Ui.Field("Folder", new DockPanel { Children = { browse.Dock(Dock.Right), path } }, "A repository, or a folder with repositories inside."));
                fields.Children.Add(Ui.Field("Levels to look down", depth));
                fields.Children.Add(fetch);
                break;
            case ActivitySource.Git:
                fields.Children.Add(Ui.Field("Clone URLs", repos, "One per line (https or ssh). git's own credentials are used."));
                break;
            default:
                fields.Children.Add(fetch);
                break;
        }
        if (s.Kind is ActivitySource.GitHub or ActivitySource.AzureDevOps or ActivitySource.Local or ActivitySource.RepositoriesPage)
        {
            fields.Children.Add(Ui.Field("Only names matching", include, "Patterns, comma separated: api-*, team-a/*"));
            fields.Children.Add(Ui.Field("Leave out names matching", exclude));
        }
        var saved = await Ui.FormAsync(Ui.Owner(this), isNew ? $"Add {Kind(s.Kind)}" : $"Change {original.Title}", fields, () =>
        {
            s.Name = name.Text?.Trim() ?? "";
            s.Owner = Null(owner.Text); s.ApiUrl = Null(api.Text); s.TokenEnv = Null(tokenEnv.Text);
            s.Organization = Null(org.Text); s.Project = Null(project.Text); s.BaseUrl = Null(baseUrl.Text); s.PatEnv = Null(patEnv.Text);
            s.Path = Null(path.Text); s.Depth = int.TryParse(depth.Text, out var d) ? Math.Clamp(d, 0, 8) : 3;
            s.Repositories = Split(repos.Text).Select(x => x.Replace(",", "")).ToList();
            s.Include = Split(include.Text); s.Exclude = Split(exclude.Text);
            s.IncludeArchived = archived.IsChecked == true; s.IncludeForks = forks.IsChecked == true; s.Fetch = fetch.IsChecked == true;
            var problem = s.Kind switch
            {
                ActivitySource.GitHub when s.Owner is null && s.Repositories.Count == 0 => "Give the organisation or user.",
                ActivitySource.AzureDevOps when s.Organization is null => "Give the organisation.",
                ActivitySource.Local when s.Path is null => "Give the folder.",
                ActivitySource.Git when s.Repositories.Count == 0 => "Give at least one clone URL.",
                _ => null,
            };
            if (problem is not null) return Task.FromResult<string?>(problem);
            Activity.Update(c =>
            {
                var i = c.Sources.FindIndex(x => x.Id == s.Id && s.Id.Length > 0);
                if (i >= 0) c.Sources[i] = s; else c.Sources.Add(s);
            });
            return Task.FromResult<string?>(null);
        }, isNew ? "Add" : "Save", 560);
        if (!saved) return;
        var id = isNew ? Activity.Config.Sources[^1].Id : s.Id;
        await ReadSourceAsync(id);
    }

    private static string? Null(string? t) => string.IsNullOrWhiteSpace(t) ? null : t.Trim();

    // ---- repositories ----

    private List<ActivityRepo> ShownRepos()
    {
        var disabled = Activity.Config.DisabledRepositories.ToHashSet(StringComparer.Ordinal);
        var text = _repoSearch.Text?.Trim();
        return Activity.Repos.Where(r =>
        {
            if (!string.IsNullOrEmpty(text) && !r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return false;
            var a = Activity.ActivityOf(r.Key);
            return _repoShow.SelectedIndex switch
            {
                1 => a?.Commits.Count > 0, 2 => a is null, 3 => a?.Status == "error", 4 => disabled.Contains(r.Key), _ => true,
            };
        }).OrderByDescending(r => Activity.ActivityOf(r.Key)?.Commits.Count ?? -1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ShowRepos()
    {
        var shown = ShownRepos();
        _repos.ItemsSource = shown;
        var all = Activity.Repos;
        var errors = all.Count(r => Activity.ActivityOf(r.Key)?.Status == "error");
        _repoCount.Text = $"{shown.Count:N0} of {all.Count:N0}" + (errors > 0 ? $" · {errors} could not be read" : "");
    }

    private void SetAll(bool on)
    {
        var keys = ShownRepos().Select(r => r.Key).ToHashSet();
        Activity.Update(c =>
        {
            if (on) c.DisabledRepositories.RemoveAll(keys.Contains);
            else foreach (var k in keys) if (!c.DisabledRepositories.Contains(k)) c.DisabledRepositories.Add(k);
        });
    }

    private Control RepoRow(ActivityRepo r)
    {
        var a = Activity.ActivityOf(r.Key);
        var disabled = Activity.Config.DisabledRepositories.Contains(r.Key);
        var check = new CheckBox { IsChecked = !disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        check.IsCheckedChanged += (_, _) => Activity.Update(c =>
        {
            c.DisabledRepositories.Remove(r.Key);
            if (check.IsChecked != true) c.DisabledRepositories.Add(r.Key);
        });
        var source = Activity.FindSource(r.SourceId)?.Title ?? r.SourceId;
        var status = a is null ? "not read yet" : a.Status switch
        {
            "error" => "could not be read: " + a.Error,
            "inactive" => "no commits in the period",
            _ => $"{a.Commits.Count:N0} commit{(a.Commits.Count == 1 ? "" : "s")} · {a.Branches} branch{(a.Branches == 1 ? "" : "es")}" + (a.Mode == "files" ? " · files only" : ""),
        };
        var line2 = $"{source}  ·  {status}{(a?.ScannedAt is { } t ? "  ·  read " + Ui.Ago(t.LocalDateTime) : "")}" + (a?.Frameworks.Count > 0 ? "  ·  " + string.Join(", ", a.Frameworks.Take(8)) : "");
        var row = new DockPanel
        {
            Margin = new Thickness(8, 3),
            Children =
            {
                check.Dock(Dock.Left),
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = r.Name + (r.Archived ? "  (archived)" : ""), FontSize = 12.5, Foreground = disabled ? Ui.Tk("faint") : Ui.Tk("text") },
                        new TextBlock { Text = line2, FontSize = 11, Foreground = a?.Status == "error" ? Ui.Tk("errorText") : Ui.Dim, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Read Again Now", () => Activity.ScanAsync(discover: false, only: [r.Key], force: true), icon: Icons.Refresh));
        menu.Items.Add(Ui.Item("Open in Repositories", () => ActivityToRepositories.OpenAsync(this, r, null), icon: Icons.SourceBranch));
        menu.Items.Add(Ui.Item("Open in the Browser", () => ActivityToRepositories.Browse(r, null), r.WebUrl is not null, Icons.OpenInNew));
        if (r.LocalPath is null)
        {
            var mirror = new ActivityScanner(Activity.Config, Activity.MirrorDir).MirrorPath(r);
            menu.Items.Add(Ui.Item("Show the Mirror", () => Shell.ShowInFolder(mirror), Directory.Exists(mirror), Icons.FolderOpen));
        }
        menu.Items.Add(Ui.Item("Copy Key", () => App.Services.Host.SetClipboardAsync(r.Key), icon: Icons.ContentCopy));
        row.ContextMenu = menu;
        ToolTip.SetTip(row, $"{r.Name}\n{r.CloneUrl ?? r.LocalPath}\n{(a?.Error ?? "")}".TrimEnd());
        return row;
    }
}
