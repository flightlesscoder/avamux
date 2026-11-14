using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avamux.Core;
using Avamux.Core.Repos;
using Avamux.Core.Terminal;
using Avamux.Core.Text;
using Avamux.Core.Watching;
using Avamux.Plugins;

namespace Avamux.Views;

/// <summary>
/// Git for one configured repository or all of them: changes (staged and not), history, branches and pull
/// requests on the left; a diff, file or commit list on the right.
/// </summary>
public sealed class RepositoriesPage : UserControl, IPage
{
    private enum Tab { Changes, History, Branches, PullRequests }
    private enum RowKind { Meta, FileHeader, Hunk, Context, Add, Del, Note, Code, Log, Info }

    /// <summary>One line of the right-hand pane.</summary>
    private sealed record Row(RowKind Kind, string Text, int? Old = null, int? New = null, DiffLine? Diff = null, IReadOnlyList<Core.Text.Span>? Spans = null, bool Marked = false, RepoCommit? Commit = null)
    {
        /// <summary>True while this row belongs to the change the user jumped to, so it is drawn highlighted.</summary>
        public bool Current { get; set; }
        /// <summary>Which sides of the highlight box this row draws: the first and last rows of a change close it off.</summary>
        public Thickness Edges { get; set; }
    }
    private sealed record RepoChoice(string? Id, string Label);
    private sealed record BranchChoice(RepoBranch? Branch, string Label);
    /// <summary>A changed file as listed on the left: the same file can appear under Staged and under Changes.</summary>
    private sealed record ChangeRef(RepoChange Change, bool Staged);
    private sealed record CommitRef(string RepoId, RepoCommit Commit);
    private sealed record CommitFileRef(string RepoId, string Hash, RepoCommitFile File);
    private sealed record BranchRef(string RepoId, RepoBranch Branch);
    private sealed record PrRef(string RepoId, WatchItem Watch, WatchResultItem Item);
    private sealed record HeaderRef(RepoEntry Repo);
    /// <summary>The "merge in progress" row of a repository: selecting it shows where the merge stands.</summary>
    private sealed record MergeRef(string RepoId);
    private sealed record CommitView(RepoEntry Repo, RepoCommitInfo Info, IReadOnlyList<RepoCommitFile> Files);

    private readonly ComboBox _picker = new() { MinWidth = 240 };
    private readonly ComboBox _branchPicker = new() { MinWidth = 180, PlaceholderText = "Branch" };
    private readonly ListBox _list = new() { Classes = { "plain" } };
    private readonly ListBox _diff = new() { Classes = { "rows" } };
    private readonly TextBlock _diffTitle = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = Ui.Mono, FontSize = 12.5 };
    private readonly TextBlock _diffSub = new() { Foreground = Ui.Dim, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
    private readonly Button _stage, _edit, _remove, _diffMode, _fileModeButton, _open, _commitButton, _changeUp, _changeDown;
    private readonly TextBlock _changeCount = new() { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _changeNav;
    private readonly Button[] _tabs;
    private readonly CheckBox _mine = new() { Content = "Only mine", FontSize = 12.5 }, _active = new() { Content = "Only active", FontSize = 12.5 };
    private readonly Border _prBar;
    private readonly StackPanel _empty;
    private readonly Grid _main;
    private readonly MergeView _merge = new() { IsVisible = false };
    private readonly DockPanel _right;
    private readonly Border _leftPane;
    private Dictionary<string, MergeState> _mergeStates = [];

    private string? _repoId;     // null = all repositories
    private Tab _tab = Tab.Changes;
    private bool _building, _fileMode, _refreshing, _refreshAgain;
    private string _repoSignature = "", _branchSignature = "";
    private ChangeRef? _change;
    private CommitRef? _commit;
    private CommitView? _view;           // a commit being looked into: its files are listed on the left
    private CommitFileRef? _viewFile;
    private BranchRef? _branch;
    private PrRef? _pr;
    private (string RepoId, string Path, int Line)? _file;   // a plain file shown from Search
    private object? _lastSelected;
    // How many commits each History section shows once "Show more" was used; keyed by repository and by whether
    // all repositories are shown, as the two views start from different page sizes.
    private readonly Dictionary<(string RepoId, bool All), int> _historyLimit = [];
    // A commit (full hash) that History should unfold, load and select the next time it is built.
    private (string RepoId, string Hash)? _revealCommit;
    private string _diffText = "";
    private string? _openLink;
    private DiffDoc? _doc;               // the diff on screen, when hunks and lines of it can be staged
    private RepoEntry? _docRepo;
    private bool _docStaged;
    private int _generation;
    private List<(int Start, int End)> _changeSpans = [];   // first and last row index of each group of changed lines
    private List<Row> _shownRows = [];                      // the rows in the list, indexed the same way
    private int _changeIndex = -1;                          // the group last jumped to, -1 before the first jump

    private static RepoService Repos => App.Services.Repos;
    private static WatchService Watches => App.Services.Watches;
    private static UiStateStore State => App.Services.Ui;

    /// <summary>The one repository this page is for (the wiki's), or null for the user's list of repositories.</summary>
    private readonly RepoEntry? _pinned;
    private IReadOnlyList<RepoEntry> AllRepos => _pinned is null ? Repos.Items : [_pinned];

    /// <param name="pinned">
    /// When given, the page works on this repository alone: no repository list to choose from, add to or remove
    /// from, and no pull requests. This is how the wiki gets the same changes, history and branches screen.
    /// </param>
    public RepositoriesPage(RepoEntry? pinned = null)
    {
        _pinned = pinned;
        _repoId = pinned?.Id;
        _picker.ItemTemplate = new FuncDataTemplate<RepoChoice>((c, _) => new TextBlock { Text = c?.Label ?? "", TextTrimming = TextTrimming.CharacterEllipsis }, false);
        _branchPicker.ItemTemplate = new FuncDataTemplate<BranchChoice>((c, _) => new TextBlock { Text = c?.Label ?? "", TextTrimming = TextTrimming.CharacterEllipsis }, false);
        _picker.SelectionChanged += (_, _) =>
        {
            if (_building || _picker.SelectedItem is not RepoChoice choice || choice.Id == _repoId) return;
            _repoId = choice.Id;
            ClearSelection();
            // Refresh after the combo box has finished its own selection work; rebuilding it from inside this
            // handler is what used to leave it with a single entry and the list showing the old repository.
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        };
        _branchPicker.SelectionChanged += (_, _) =>
        {
            if (_building || _branchPicker.SelectedItem is not BranchChoice { Branch: { IsCurrent: false } branch } || CurrentRepo is not { } repo) return;
            Dispatcher.UIThread.Post(() => _ = Ui.TryAsync(this, async () =>
            {
                try { await Repos.SwitchAsync(repo, branch); Ui.Toast(this, "Switched branch", $"{repo.Name} is now on {branch.Name}"); }
                finally { _branchSignature = ""; await RefreshAsync(); }
            }, "Could not switch branch"));
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            var tag = (_list.SelectedItem as Control)?.Tag;
            if (tag is HeaderRef or null)
            {
                // Headers are there to be right-clicked, not selected: put the selection back.
                _building = true;
                try { _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, _lastSelected)); } finally { _building = false; }
                return;
            }
            Select(tag);
            _ = ShowAsync();
        };
        _diff.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row is null ? new Panel() : BuildRow(row), false);
        _diff.AddHandler(ContextRequestedEvent, OnDiffContext, Avalonia.Interactivity.RoutingStrategies.Bubble);
        _diff.DoubleTapped += (_, _) =>
        {
            if (_diff.SelectedItem is Row { Commit: { } c } && _branch is { } b && Repos.Find(b.RepoId) is { } repo) _ = ViewCommitAsync(repo, c.Hash);
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_diff, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        _stage = Ui.TextButton("Stage", () => _ = StageSelectedAsync());
        _edit = Ui.IconButton(Icons.Pencil, "Open the file in the editor (repositories.editor) in a terminal tab", EditFile, "Edit");
        _open = Ui.IconButton(Icons.OpenInNew, "Open in the browser", () => { if (_openLink is { } l) Shell.Open(l); }, "Open");
        _diffMode = Ui.TextButton("Diff", () => { _fileMode = false; _ = ShowAsync(); }, "Show what changed");
        _fileModeButton = Ui.TextButton("File", () => { _fileMode = true; _ = ShowAsync(); }, "Show the whole file with syntax highlighting and line numbers");
        _changeUp = Ui.TextButton("↑", () => JumpChange(-1), "Previous change (wraps around to the last one)");
        _changeDown = Ui.TextButton("↓", () => JumpChange(1), "Next change (wraps around to the first one)");
        _changeUp.IsEnabled = _changeDown.IsEnabled = false;
        _changeNav = Ui.Row(2, _changeUp, _changeDown, _changeCount);
        _changeNav.IsVisible = false;
        _remove = Ui.IconButton(Icons.Delete, "Remove the selected repository from the list (files are not touched)", () => _ = RemoveAsync());
        _commitButton = Ui.IconButton(Icons.Check, "Commit what is staged", () => _ = CommitAsync(CurrentRepo), "Commit");
        _tabs = new[] { "Changes", "History", "Branches", "Pull Requests" }.Select((label, i) =>
        {
            var b = new Button { Content = label, Classes = { "tab" } };
            b.Click += (_, _) => { _tab = (Tab)i; _view = null; _viewFile = null; _file = null; _ = RefreshAsync(); };
            return b;
        }).ToArray();

        _mine.IsChecked = State.Data.PullRequestsMineOnly;
        _active.IsChecked = State.Data.PullRequestsActiveOnly;
        void Filters()
        {
            State.Data.PullRequestsMineOnly = _mine.IsChecked == true;
            State.Data.PullRequestsActiveOnly = _active.IsChecked == true;
            State.SaveSoon();
            _ = RefreshAsync();
        }
        _mine.IsCheckedChanged += (_, _) => { if (!_building) Filters(); };
        _active.IsCheckedChanged += (_, _) => { if (!_building) Filters(); };
        ToolTip.SetTip(_mine, "Show only pull requests that are yours");
        ToolTip.SetTip(_active, "Hide merged, closed and abandoned pull requests");
        _prBar = new Border
        {
            Padding = new Thickness(12, 4), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false,
            Child = new DockPanel
            {
                Children =
                {
                    Ui.IconButton(Icons.Refresh, "Check for pull requests now", () => _ = CheckPullRequestsAsync()).Dock(Dock.Right),
                    Ui.Row(16, _mine, _active),
                },
            },
        };

        var left = new DockPanel
        {
            Children =
            {
                new Border
                {
                    Padding = new Thickness(6, 0), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new DockPanel { Children = { _commitButton.Dock(Dock.Right), Ui.Row(0, _tabs) } },
                }.Dock(Dock.Top),
                _prBar.Dock(Dock.Top),
                _list,
            },
        };
        _merge.Saved = AfterResolved;
        _merge.StepFile = StepConflict;
        _merge.ExpandChanged = _ => SizeList();
        _merge.ContinueMerge = ContinueMergeAsync;
        _merge.AbortMerge = AbortMergeAsync;
        _merge.ResolveNext = repo => SelectConflict(repo.Id, null);
        var right = _right = new DockPanel
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
                            Ui.Row(4, Ui.Row(0, _diffMode, _fileModeButton), _changeNav, _stage, _edit, _open,
                                Ui.IconButton(Icons.Console, "Open a terminal in this repository", OpenTerminal, "Terminal"),
                                Ui.TextButton("Copy", () => _ = App.Services.Host.SetClipboardAsync(_diffText), "Copy what is shown to the clipboard")).Dock(Dock.Right),
                            new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _diffTitle, _diffSub } },
                        },
                    },
                }.Dock(Dock.Top),
                _diff,
            },
        };
        _main = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("500,*"),
            Children =
            {
                (_leftPane = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 1, 0), Child = left }),
                new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = new Panel { Children = { right, _merge } } }.At(0, 1),
            },
        };
        _empty = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 10, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                Ui.Text("No repositories yet", "h2"),
                Ui.Text("Add any number of git repositories, then browse the changes of one of them or of all at once, and open files in vim or neovim.", "dim"),
                Ui.Apply(Ui.TextButton("Add repository", () => _ = AddAsync()), b => b.HorizontalAlignment = HorizontalAlignment.Left),
            },
        };

        ToolTip.SetTip(_branchPicker, "Switch branch");
        var branchRow = Ui.Row(6, new PathIcon { Data = Icons.SourceBranch, Width = 15, Height = 15, Foreground = Ui.Dim }, _branchPicker);
        var refresh = Ui.IconButton(Icons.Refresh, "Refresh", () => { _branchSignature = ""; _ = RefreshAsync(); });
        Content = new DockPanel
        {
            Children =
            {
                (_pinned is null
                    ? Ui.Header("Repositories", "Git changes, history, branches and pull requests for one repository or all of them.",
                        branchRow, _picker, Ui.IconButton(Icons.Plus, "Add a repository", () => _ = AddAsync(), "Add"), _remove, refresh)
                    : Ui.Header($"{_pinned.Name} in git", $"Changes, history and branches of {_pinned.Path}", branchRow, refresh)).Dock(Dock.Top),
                new Panel { Children = { _main, _empty } },
            },
        };
        if (_pinned is not null)
        {
            _tabs[(int)Tab.PullRequests].IsVisible = false;
            // Pages saved in the wiki are changes here.
            if (_pinned.Id == Core.Wiki.WikiService.RepoId) App.Services.Wiki.Changed += Ui.Debounced(() => { if (IsEffectivelyVisible) _ = RefreshAsync(); }, 300);
        }
        Repos.Changed += Ui.Debounced(() => _ = RefreshAsync());
        Watches.Changed += Ui.Debounced(() => { if (_tab == Tab.PullRequests && IsVisible) _ = RefreshAsync(); }, 250);
        _ = RefreshAsync();
    }

    public void OnShown() => _ = RefreshAsync();

    /// <summary>Goes to a tab by name: "changes", "history" or "branches".</summary>
    public void ShowTab(string name)
    {
        if (!Enum.TryParse<Tab>(name, true, out var tab) || (tab == Tab.PullRequests && _pinned is not null)) return;
        _tab = tab; _view = null; _viewFile = null; _file = null;
        _ = RefreshAsync();
    }

    public void SelectRepo(string? id)
    {
        if (_pinned is not null) return;
        _repoId = id;
        ClearSelection();
        _ = RefreshAsync();
    }

    /// <summary>Shows a repository, and optionally a commit ("commit:hash") or a file ("file:path") in it. Used by Search.</summary>
    public void Reveal(string repoId, string? what, int line = 0)
    {
        if (Repos.Find(repoId) is not { } repo) return;
        _repoId = repo.Id;
        ClearSelection();
        if (what?.StartsWith("commit:", StringComparison.Ordinal) == true) { _tab = Tab.History; _ = ViewCommitAsync(repo, what[7..]); return; }
        if (what?.StartsWith("file:", StringComparison.Ordinal) == true) { _tab = Tab.Changes; _file = (repo.Id, what[5..], line); }
        _ = RefreshAsync();
    }

    private void ClearSelection()
    {
        _change = null; _commit = null; _view = null; _viewFile = null; _branch = null; _pr = null; _file = null; _lastSelected = null; _revealCommit = null;
    }

    private void Select(object? tag)
    {
        _lastSelected = tag;
        switch (tag)
        {
            case ChangeRef c: _change = c; _file = null; break;
            case MergeRef: _change = null; _file = null; break;
            case CommitRef c: _commit = c; break;
            case CommitFileRef f: _viewFile = f; break;
            case BranchRef b: _branch = b; break;
            case PrRef p: _pr = p; break;
        }
    }

    private RepoEntry? CurrentRepo =>
        AllRepos.FirstOrDefault(r => r.Id == (_view?.Repo.Id ?? (_tab switch
        {
            Tab.Changes => _file?.RepoId ?? _change?.Change.RepoId ?? (_lastSelected as MergeRef)?.RepoId, Tab.History => _commit?.RepoId, Tab.Branches => _branch?.RepoId, _ => _pr?.RepoId,
        }) ?? _repoId)) ?? AllRepos.FirstOrDefault(r => r.Id == _repoId) ?? (AllRepos.Count == 1 ? AllRepos[0] : null);

    // ---- look ----

    /// <summary>The glyph in front of a changed file: what happened to it, in a shape and a colour.</summary>
    private static (Geometry Icon, IBrush Brush) Glyph(string description) => description switch
    {
        "untracked" => (Icons.PlusCircle, Ui.Tk("accent")),
        "added" => (Icons.PlusCircle, Ui.Tk("ok")),
        "deleted" => (Icons.MinusCircle, Ui.Tk("error")),
        "conflict" => (Icons.AlertCircle, Ui.Tk("errorHi")),
        "renamed" or "copied" => (Icons.ArrowRightCircle, Ui.Tk("purple")),
        "type changed" => (Icons.HelpCircle, Ui.Tk("warn")),
        _ => (Icons.PencilCircle, Ui.Tk("warn")),
    };

    private static Control StatusGlyph(string description, string tip)
    {
        var (icon, brush) = Glyph(description);
        var glyph = new PathIcon { Data = icon, Foreground = brush, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(glyph, tip);
        return glyph;
    }

    private static Control FileRow(string path, string description, string tip, string? was = null)
    {
        var dir = Path.GetDirectoryName(path.TrimEnd('/'))?.Replace('\\', '/') ?? "";
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            Children =
            {
                StatusGlyph(description, tip),
                Ui.Row(6,
                    new TextBlock { Text = Path.GetFileName(path.TrimEnd('/')) + (path.EndsWith('/') ? "/" : ""), FontSize = 12.5 },
                    new TextBlock { Text = dir + (was is null ? "" : $"  (was {was})"), FontSize = 11.5, Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center }).At(0, 1),
            },
        };
    }

    /// <param name="collapsed">When set, the header shows a fold arrow and a click on it calls <paramref name="toggle"/>.</param>
    private ListBoxItem HeaderRow(RepoEntry repo, string detail, string? error, ContextMenu? menu, bool? collapsed = null, Action? toggle = null, string folds = "its commits", Control? badge = null)
    {
        var title = Ui.Row(8, new TextBlock { Text = repo.Name, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = detail, Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (badge is not null) title.Children.Add(badge);
        if (collapsed is { } folded)
            title.Children.Insert(0, new TextBlock { Text = folded ? "▶" : "▼", Width = 14, FontSize = 10, Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center });
        var content = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(8, 9, 8, 3),
            Cursor = toggle is null ? null : new Cursor(StandardCursorType.Hand),
            Child = Ui.Column(1,
                title,
                Ui.Apply(new TextBlock { Text = error ?? "", Foreground = Ui.Tk("error"), FontSize = 12, TextWrapping = TextWrapping.Wrap }, t => t.IsVisible = error is not null)),
        };
        if (toggle is not null)
            content.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; toggle(); } };
        ToolTip.SetTip(content, repo.Path + (toggle is null ? "" : collapsed == true ? $"\nClick to show {folds}" : $"\nClick to fold {folds} away") + "\nRight-click for branch, folder, remote and more");
        return new ListBoxItem { Tag = new HeaderRef(repo), Padding = new Thickness(0), Focusable = false, Content = content, ContextMenu = menu ?? RepoMenu(repo) };
    }

    private static ListBoxItem SubHeader(string text) => new()
    {
        IsHitTestVisible = false, Focusable = false, Padding = new Thickness(10, 5, 8, 1), MinHeight = 0,
        Content = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, Foreground = Ui.Tk("faint"), FontWeight = FontWeight.SemiBold },
    };

    private static ListBoxItem Note(string text) => new()
    {
        IsHitTestVisible = false, Focusable = false, Padding = new Thickness(12, 4, 8, 6),
        Content = new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Dim, TextWrapping = TextWrapping.Wrap },
    };

    // ---- refresh ----

    private async Task RefreshAsync()
    {
        // One refresh at a time; a request that arrives meanwhile runs once more afterwards.
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                try { await RefreshCoreAsync(); }
                catch (Exception ex) { Core.Logging.Log.Error("repos", "Refresh failed", ex); }
            } while (_refreshAgain);
        }
        finally { _refreshing = false; }
    }

    private async Task RefreshCoreAsync()
    {
        var gen = ++_generation;
        var repos = AllRepos;
        if (_repoId is not null && repos.All(r => r.Id != _repoId)) { _repoId = null; ClearSelection(); }

        // The repository list is only rebuilt when the set of repositories changed.
        var signature = string.Join("|", repos.Select(r => r.Id + "=" + r.Name));
        _building = true;
        try
        {
            if (signature != _repoSignature)
            {
                _repoSignature = signature;
                var choices = new List<RepoChoice> { new(null, $"All repositories ({repos.Count})") };
                choices.AddRange(repos.Select(r => new RepoChoice(r.Id, r.Name)));
                _picker.ItemsSource = choices;
            }
            var wanted = (_picker.ItemsSource as List<RepoChoice>)?.FirstOrDefault(c => c.Id == _repoId);
            if (!Equals(_picker.SelectedItem, wanted)) _picker.SelectedItem = wanted;
        }
        finally { _building = false; }

        _empty.IsVisible = repos.Count == 0;
        _main.IsVisible = repos.Count > 0;
        _picker.IsVisible = repos.Count > 0;
        _remove.IsEnabled = _repoId is not null;
        ToolTip.SetTip(_picker, _repoId is null ? "Showing every repository" : Repos.Find(_repoId)?.Path);
        for (var i = 0; i < _tabs.Length; i++) _tabs[i].Classes.Set("on", i == (int)_tab);
        _prBar.IsVisible = _tab == Tab.PullRequests && _view is null;
        _commitButton.IsVisible = _tab == Tab.Changes && _view is null;
        if (repos.Count == 0) return;

        await RefreshBranchPickerAsync(gen);
        if (gen != _generation) return;

        var shown = _repoId is null ? repos.ToList() : repos.Where(r => r.Id == _repoId).ToList();
        // Whether any repository on show is in the middle of a merge is worth knowing on every tab.
        var states = await Task.WhenAll(shown.Select(Repos.MergeStateAsync));
        if (gen != _generation) return;
        _mergeStates = shown.Zip(states).ToDictionary(x => x.First.Id, x => x.Second);
        var conflicts = states.Sum(s => s.Conflicts.Count);
        var merging = states.Count(s => s.InProgress);
        _tabs[0].Content = conflicts > 0 ? $"Changes  ⚠ {conflicts}" : merging > 0 ? "Changes  ✓" : "Changes";
        if (conflicts > 0 || merging > 0) _tabs[0].Foreground = Ui.Tk(conflicts > 0 ? "errorHi" : "ok"); else _tabs[0].ClearValue(Button.ForegroundProperty);
        if (conflicts > 0 || merging > 0)
            ToolTip.SetTip(_tabs[0], conflicts > 0 ? $"{conflicts} file{(conflicts == 1 ? " has" : "s have")} conflicts to resolve" : "A merge has every conflict resolved and is waiting to be finished");
        else ToolTip.SetTip(_tabs[0], null);
        var rows = new List<ListBoxItem>();
        if (_view is not null) BuildCommitView(rows);
        else switch (_tab)
        {
            case Tab.Changes: await BuildChangesAsync(rows, shown); break;
            case Tab.History: await BuildHistoryAsync(rows, shown); break;
            case Tab.Branches: await BuildBranchesAsync(rows, shown); break;
            default: BuildPullRequests(rows, shown); break;
        }
        if (gen != _generation) return;
        var revealed = _view is null && _tab == Tab.History && _revealCommit is not null;
        if (revealed) _revealCommit = null;

        // Keep showing what was selected if it still exists; otherwise pick the first entry. The row is marked
        // before it goes into the list: selecting an item that was only just added does not highlight it.
        var selected = rows.FirstOrDefault(r => r.IsSelected) ?? (_file is null ? rows.FirstOrDefault(r => r.Tag is ChangeRef or CommitRef or CommitFileRef or BranchRef or PrRef) ?? rows.FirstOrDefault(r => r.Tag is MergeRef) : null);
        if (selected is not null) selected.IsSelected = true;
        _building = true;
        try
        {
            _list.Items.Clear();
            foreach (var r in rows) _list.Items.Add(r);
            if (selected is null) { if (_view is null) { _change = null; _commit = null; _branch = null; _pr = null; } _lastSelected = null; }
            else Select(selected.Tag);
        }
        finally { _building = false; }
        if (revealed && selected is not null) Dispatcher.UIThread.Post(() => _list.ScrollIntoView(selected), DispatcherPriority.Loaded);
        await ShowAsync();
    }

    /// <summary>The branch switcher follows the repository being shown; with all repositories it is greyed out.</summary>
    private async Task RefreshBranchPickerAsync(int gen)
    {
        var repo = _repoId is null ? null : Repos.Find(_repoId);
        _branchPicker.IsEnabled = repo is not null;
        ToolTip.SetTip(_branchPicker, repo is null ? "Choose one repository to switch its branch" : $"Switch the branch of {repo.Name}");
        if (repo is null)
        {
            _building = true;
            try { _branchPicker.ItemsSource = null; _branchSignature = ""; _branchPicker.PlaceholderText = "All repositories"; }
            finally { _building = false; }
            return;
        }
        var branches = await Repos.BranchesAsync(repo);
        if (gen != _generation) return;
        var signature = repo.Id + ":" + string.Join("|", branches.Select(b => (b.IsCurrent ? "*" : "") + b.Name));
        if (signature == _branchSignature) return;
        _branchSignature = signature;
        var local = branches.Where(b => !b.IsRemote).Select(b => b.Name).ToHashSet();
        var choices = branches.Where(b => !b.IsRemote).Select(b => new BranchChoice(b, b.Name)).ToList();
        // Remote branches that have no local branch yet can be switched to as well.
        choices.AddRange(branches.Where(b => b.IsRemote && !local.Contains(b.Name[(b.Name.IndexOf('/') + 1)..])).Select(b => new BranchChoice(b, b.Name)));
        _building = true;
        try
        {
            _branchPicker.ItemsSource = choices;
            _branchPicker.SelectedItem = choices.FirstOrDefault(c => c.Branch!.IsCurrent);
            _branchPicker.PlaceholderText = choices.Count == 0 ? "No branches yet" : "Detached HEAD";
        }
        finally { _building = false; }
    }

    private async Task BuildChangesAsync(List<ListBoxItem> rows, List<RepoEntry> shown)
    {
        var statuses = await Task.WhenAll(shown.Select(Repos.StatusAsync));
        var folded = State.Data.ChangesCollapsed.ToHashSet();
        foreach (var st in statuses)
        {
            var repo = shown.First(r => r.Id == st.Id);
            var branch = st.Error is not null ? "error" : (st.Branch ?? "?") + (st.Ahead > 0 ? $" ↑{st.Ahead}" : "") + (st.Behind > 0 ? $" ↓{st.Behind}" : "");
            var merge = _mergeStates.GetValueOrDefault(st.Id, MergeState.None);
            var conflicted = st.Changes.Where(c => c.Description == "conflict").ToList();
            // The badge stays on the header when the repository is folded away, so a merge cannot hide.
            var badge = !merge.Active && conflicted.Count == 0 ? null
                : conflicted.Count > 0 ? Ui.Chip($"⚠ {conflicted.Count} conflict{(conflicted.Count == 1 ? "" : "s")}", Ui.Tk("error"))
                : Ui.Chip($"✓ {merge.Verb} ready", Ui.Tk("ok"));
            rows.Add(HeaderRow(repo, branch + (st.Error is null ? st.Changes.Count == 0 ? " · clean" : $" · {st.Changes.Count} changed" : ""), st.Error, null, folded.Contains(st.Id), () => ToggleChanges(st.Id), "its changed files", badge));
            if (folded.Contains(st.Id)) continue;
            if (merge.InProgress) rows.Add(MergeRow(repo, merge, conflicted.Count));
            if (conflicted.Count > 0) rows.Add(SubHeader($"Conflicts ({conflicted.Count})"));
            foreach (var c in conflicted) rows.Add(ChangeRow(repo, c, false));
            var staged = st.Changes.Where(c => c.Staged && c.Description != "conflict").ToList();
            var unstaged = st.Changes.Where(c => (c.Unstaged || c.Untracked) && c.Description != "conflict").ToList();
            if (staged.Count > 0) rows.Add(SubHeader($"Staged ({staged.Count})"));
            foreach (var c in staged) rows.Add(ChangeRow(repo, c, true));
            if (unstaged.Count > 0) rows.Add(SubHeader($"Changes ({unstaged.Count})"));
            foreach (var c in unstaged) rows.Add(ChangeRow(repo, c, false));
        }
    }

    /// <summary>The row that says a merge (or rebase, cherry-pick, revert) is under way, with what is left and how to finish.</summary>
    private ListBoxItem MergeRow(RepoEntry repo, MergeState merge, int conflicts)
    {
        var done = conflicts == 0;
        var name = char.ToUpperInvariant(merge.Verb[0]) + merge.Verb[1..];
        var go = Ui.TextButton("Continue", () => _ = Ui.TryAsync(this, () => ContinueMergeAsync(repo), $"Could not continue the {merge.Verb}"), done ? $"Commit the result and finish the {merge.Verb}" : "Resolve the conflicts first");
        go.IsEnabled = done;
        if (done) go.Classes.Add("accent");
        var abort = Ui.TextButton("Abort…", () => _ = Ui.TryAsync(this, () => AbortMergeAsync(repo), $"Could not abort the {merge.Verb}"), $"Give up the {merge.Verb} and put everything back the way it was");
        go.Padding = abort.Padding = new Thickness(9, 3);
        go.FontSize = abort.FontSize = 12;
        var tone = done ? "ok" : "error";
        var item = new ListBoxItem
        {
            Tag = new MergeRef(repo.Id), Padding = new Thickness(8, 3), IsSelected = _lastSelected is MergeRef m && m.RepoId == repo.Id,
            Content = new Border
            {
                Background = new SolidColorBrush(Ui.Color(tone), 0.14), BorderBrush = Ui.Tk(tone), BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = Ui.R(4), Padding = new Thickness(9, 6),
                Child = new DockPanel
                {
                    Children =
                    {
                        Ui.Row(4, go, abort).Dock(Dock.Right),
                        Ui.Column(1,
                            new TextBlock { Text = merge.Title, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock
                            {
                                Text = done ? $"✓ All conflicts resolved — continue to finish the {merge.Verb}" : $"⚠ {conflicts} file{(conflicts == 1 ? "" : "s")} with conflicts left to resolve",
                                Foreground = Ui.Tk(done ? "ok" : "errorHi"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                            }),
                    },
                },
            },
        };
        ToolTip.SetTip(item, $"{name} in progress in {repo.Name}. Click to see where it stands.");
        return item;
    }

    // ---- merging ----

    private IEnumerable<ListBoxItem> ConflictRows(string? repoId = null) =>
        _list.Items.OfType<ListBoxItem>().Where(i => i.Tag is ChangeRef { Staged: false, Change.Description: "conflict" } c && (repoId is null || c.Change.RepoId == repoId));

    /// <summary>Selects a conflicted file of a repository: the one after <paramref name="afterPath"/>, or the first.</summary>
    private void SelectConflict(string repoId, string? afterPath)
    {
        var rows = ConflictRows(repoId).ToList();
        if (rows.Count == 0) return;
        var at = afterPath is null ? -1 : rows.FindIndex(r => ((ChangeRef)r.Tag!).Change.Path == afterPath);
        _list.SelectedItem = rows[(at + 1) % rows.Count];
        _list.ScrollIntoView(_list.SelectedItem);
    }

    /// <summary>The file arrows of the merge tool: the next or previous conflicted file on show, in any repository.</summary>
    private void StepConflict(int step)
    {
        var rows = ConflictRows().ToList();
        if (rows.Count == 0) return;
        var at = rows.FindIndex(r => r.IsSelected);
        _list.SelectedItem = rows[((at < 0 ? 0 : at + step) % rows.Count + rows.Count) % rows.Count];
        _list.ScrollIntoView(_list.SelectedItem);
    }

    /// <summary>After a file was marked resolved: on to the repository's next conflict, or to its merge when none is left.</summary>
    private void AfterResolved(RepoEntry repo, string path)
    {
        var rest = ConflictRows(repo.Id).Select(r => (ChangeRef)r.Tag!).ToList();
        var at = rest.FindIndex(r => r.Change.Path == path);
        var next = rest.Where(r => r.Change.Path != path).ToList() is { Count: > 0 } others ? others[Math.Clamp(at, 0, others.Count - 1)] : null;
        if (next is not null) { _change = next; _lastSelected = next; }
        else if (_mergeStates.GetValueOrDefault(repo.Id)?.InProgress == true) { _change = null; _lastSelected = new MergeRef(repo.Id); }
        _ = RefreshAsync();
    }

    private async Task ContinueMergeAsync(RepoEntry repo)
    {
        var before = await Repos.MergeStateAsync(repo);
        var after = await Repos.ContinueMergeAsync(repo);
        _branchSignature = "";
        if (after.Conflicts.Count > 0) Ui.Toast(this, "More conflicts", $"The {before.Verb} of {repo.Name} stopped at its next commit: {after.Conflicts.Count} file{(after.Conflicts.Count == 1 ? "" : "s")} to resolve.");
        else if (after.InProgress) Ui.Toast(this, $"The {before.Verb} is not finished yet", repo.Name);
        else Ui.Toast(this, $"{char.ToUpperInvariant(before.Verb[0])}{before.Verb[1..]} finished", repo.Name);
        await RefreshAsync();
    }

    private async Task AbortMergeAsync(RepoEntry repo)
    {
        var state = await Repos.MergeStateAsync(repo);
        if (!await Ui.ConfirmAsync(Ui.Owner(this), $"Abort the {state.Verb}", $"Give up the {state.Verb} in {repo.Name}? Every conflict you resolved in it is thrown away and the repository goes back to where it was before.", "Abort")) return;
        await Repos.AbortMergeAsync(repo);
        _branchSignature = "";
        Ui.Toast(this, $"The {state.Verb} was aborted", repo.Name);
        await RefreshAsync();
    }

    /// <summary>
    /// Runs something that can stop for conflicts (a merge, a cherry-pick, a revert). When it does, that is not a
    /// failure to report: the Changes tab opens on the first conflict instead.
    /// </summary>
    private async Task MergingAsync(RepoEntry repo, Func<Task> work)
    {
        try { await work(); }
        catch (InvalidOperationException)
        {
            var state = await Repos.MergeStateAsync(repo);
            if (state.Conflicts.Count == 0) throw;
            Ui.Toast(this, "Conflicts to resolve", $"{state.Title} in {repo.Name}: {state.Conflicts.Count} file{(state.Conflicts.Count == 1 ? "" : "s")}.");
            _tab = Tab.Changes; _view = null; _viewFile = null; _file = null; _change = null; _lastSelected = null;
            State.Data.ChangesCollapsed.Remove(repo.Id);
            await RefreshAsync();
            SelectConflict(repo.Id, null);
        }
    }

    /// <summary>What happened to a file on one side: in the index (staged) or in the working tree.</summary>
    private static string Describe(RepoChange c, bool staged)
    {
        if (c.Description == "conflict") return "conflict";
        if (c.Untracked) return "untracked";
        return (staged ? c.Index : c.WorkTree) switch { 'A' => "added", 'D' => "deleted", 'R' => "renamed", 'C' => "copied", 'T' => "type changed", _ => "modified" };
    }

    private ListBoxItem ChangeRow(RepoEntry repo, RepoChange c, bool staged)
    {
        var what = Describe(c, staged);
        var tip = what switch
        {
            "untracked" => "New file that git is not tracking yet", "conflict" => "Conflict: select it to resolve it in the merge tool",
            _ => char.ToUpperInvariant(what[0]) + what[1..] + (staged ? ", staged for the next commit" : ", not staged"),
        };
        var reference = new ChangeRef(c, staged);
        var item = new ListBoxItem
        {
            Tag = reference, Padding = new Thickness(12, 4, 8, 4),
            IsSelected = _change is not null && _change.Staged == staged && _change.Change.RepoId == c.RepoId && _change.Change.Path == c.Path,
            Content = FileRow(c.Path, what, tip, staged ? c.OriginalPath : null),
        };
        var full = Path.Combine(repo.Path, c.Path);
        var menu = new ContextMenu();
        if (what == "conflict")
        {
            menu.Items.Add(Ui.Item("Accept Yours (whole file)", () => Repos.ResolveWithSideAsync(repo, c.Path, false)));
            menu.Items.Add(Ui.Item("Accept Theirs (whole file)", () => Repos.ResolveWithSideAsync(repo, c.Path, true)));
            menu.Items.Add(Ui.Item("Mark as Resolved (as the file is now)", () => Repos.StageAsync(repo, c.Path)));
            menu.Items.Add(new Separator());
        }
        else if (staged) menu.Items.Add(Ui.Item("Unstage", () => Repos.UnstageAsync(repo, c.Path)));
        else menu.Items.Add(Ui.Item("Stage", () => Repos.StageAsync(repo, c.Path)));
        if (!staged && what != "conflict") menu.Items.Add(Ui.Item(c.Untracked ? "Delete File…" : "Discard Changes…", () => DiscardAsync(repo, c)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Open in Editor", () => Ui.OpenInEditor(c.Path, repo.Path), what != "deleted" && !c.Path.EndsWith('/'), Icons.Pencil));
        menu.Items.Add(Ui.Item("Open in Default App", () => Shell.Open(full), File.Exists(full)));
        menu.Items.Add(Ui.Item("Show in Folder", () => Shell.ShowInFolder(File.Exists(full) || Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? repo.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Copy Path", () => { _ = App.Services.Host.SetClipboardAsync(full); }));
        menu.Items.Add(Ui.Item("Copy Relative Path", () => { _ = App.Services.Host.SetClipboardAsync(c.Path); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item($"Stage All in {repo.Name}", () => Repos.StageAllAsync(repo)));
        menu.Items.Add(Ui.Item($"Unstage All in {repo.Name}", () => Repos.UnstageAllAsync(repo)));
        item.ContextMenu = menu;
        return item;
    }

    /// <summary>How many commits a page of history holds: fewer per repository when every repository is shown.</summary>
    private int HistoryPage => _repoId is null ? 15 : Math.Max(1, App.Services.Config.Settings.Repositories.LogCount);

    private void ToggleChanges(string repoId)
    {
        var folded = State.Data.ChangesCollapsed;
        if (!folded.Remove(repoId)) folded.Add(repoId);
        State.SaveSoon();
        _ = RefreshAsync();
    }

    private void ToggleHistory(string repoId)
    {
        var folded = State.Data.HistoryCollapsed;
        if (!folded.Remove(repoId)) folded.Add(repoId);
        State.SaveSoon();
        _ = RefreshAsync();
    }

    /// <summary>
    /// Each repository is a foldable section that starts with one page of its newest commits; "Show more" at the end
    /// of a section adds another page. Folded sections do not run git log at all.
    /// </summary>
    private async Task BuildHistoryAsync(List<ListBoxItem> rows, List<RepoEntry> shown)
    {
        var page = HistoryPage;
        string? notOnBranch = null;
        if (_revealCommit is { } reveal && shown.FirstOrDefault(r => r.Id == reveal.RepoId) is { } target)
        {
            // A commit to land on (from Search, or coming back from looking into it): unfold its repository and
            // load whole pages until it is included.
            if (State.Data.HistoryCollapsed.Remove(target.Id)) State.SaveSoon();
            if (await Repos.CommitPositionAsync(target, reveal.Hash) is { } position)
            {
                var key = (target.Id, _repoId is null);
                var needed = (position / page + 1) * page;
                if (!_historyLimit.TryGetValue(key, out var have) || have < needed) _historyLimit[key] = Math.Max(needed, page);
            }
            else notOnBranch = target.Id;
        }
        var folded = State.Data.HistoryCollapsed.ToHashSet();
        var loaded = await Task.WhenAll(shown.Select(async repo =>
        {
            var limit = _historyLimit.TryGetValue((repo.Id, _repoId is null), out var l) ? l : page;
            var count = Repos.CommitCountAsync(repo);
            var log = folded.Contains(repo.Id) ? Task.FromResult<IReadOnlyList<RepoCommit>>([]) : Repos.LogAsync(repo, limit);
            return (repo, total: await count, commits: await log);
        }));
        foreach (var (repo, total, commits) in loaded)
        {
            var collapsed = folded.Contains(repo.Id);
            var detail = total is not { } t || t == 0 ? "no commits"
                : collapsed || commits.Count >= t ? $"{t:N0} commit{(t == 1 ? "" : "s")}"
                : $"newest {commits.Count:N0} of {t:N0}";
            rows.Add(HeaderRow(repo, detail, null, null, collapsed, () => ToggleHistory(repo.Id)));
            if (collapsed) continue;
            if (notOnBranch == repo.Id) rows.Add(Note($"Commit {_revealCommit!.Value.Hash[..Math.Min(10, _revealCommit.Value.Hash.Length)]} is not on the current branch of {repo.Name}, so it is not in this list. The Branches tab shows the commits of other branches."));
            var revealHere = _revealCommit is { } rc && rc.RepoId == repo.Id ? rc.Hash : null;
            foreach (var c in commits)
            {
                var selected = revealHere is not null
                    ? revealHere.StartsWith(c.Hash, StringComparison.OrdinalIgnoreCase)
                    : _commit is not null && _commit.RepoId == repo.Id && _commit.Commit.Hash == c.Hash;
                rows.Add(new ListBoxItem
                {
                    Tag = new CommitRef(repo.Id, c), IsSelected = selected, Padding = new Thickness(12, 5, 8, 5),
                    Content = Ui.Column(1,
                        new TextBlock { Text = c.Subject, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis },
                        Ui.Text($"{c.Hash} · {c.Author} · {c.Date}", "dim", 11)),
                    ContextMenu = CommitMenu(repo, c),
                });
            }
            if (total is { } all && commits.Count < all)
            {
                var more = Math.Min(page, all - commits.Count);
                var key = (repo.Id, _repoId is null);
                var shownNow = commits.Count;
                rows.Add(new ListBoxItem
                {
                    Focusable = false, Padding = new Thickness(12, 2, 8, 8), Background = Brushes.Transparent,
                    Content = Ui.TextButton($"Show {more:N0} more", () => { _historyLimit[key] = shownNow + more; _ = RefreshAsync(); },
                        $"Load the next {more:N0} commits of {repo.Name} ({all - shownNow:N0} not shown yet)"),
                });
            }
        }
    }

    private void BuildCommitView(List<ListBoxItem> rows)
    {
        var view = _view!;
        var back = new ListBoxItem
        {
            Padding = new Thickness(6, 4), Focusable = false, Tag = new HeaderRef(view.Repo),
            Content = Ui.IconButton(Icons.ArrowLeft, "Back to the list", () => { _view = null; _viewFile = null; _ = RefreshAsync(); }, _tab == Tab.Branches ? "Back to branches" : "Back to history"),
            ContextMenu = new ContextMenu { Items = { Ui.Item("Back", () => { _view = null; _viewFile = null; _ = RefreshAsync(); }) } },
        };
        rows.Add(back);
        rows.Add(Note($"{view.Repo.Name} · commit {view.Info.ShortHash}\n{view.Info.Subject}\n{view.Info.Author} · {view.Info.Date}"));
        rows.Add(SubHeader($"Files in this commit ({view.Files.Count})"));
        foreach (var f in view.Files)
        {
            var reference = new CommitFileRef(view.Repo.Id, view.Info.Hash, f);
            var full = Path.Combine(view.Repo.Path, f.Path);
            rows.Add(new ListBoxItem
            {
                Tag = reference, Padding = new Thickness(12, 4, 8, 4), IsSelected = _viewFile?.File.Path == f.Path,
                Content = FileRow(f.Path, f.Description, char.ToUpperInvariant(f.Description[0]) + f.Description[1..] + " in this commit", f.OriginalPath),
                ContextMenu = new ContextMenu
                {
                    Items =
                    {
                        Ui.Item("Open Current Version in Editor", () => Ui.OpenInEditor(f.Path, view.Repo.Path), File.Exists(full), Icons.Pencil),
                        Ui.Item("Show in Folder", () => Shell.ShowInFolder(full), File.Exists(full)),
                        Ui.Item("Copy Path", () => { _ = App.Services.Host.SetClipboardAsync(full); }),
                    },
                },
            });
        }
        if (view.Files.Count == 0) rows.Add(Note("This commit changed no files (a merge, perhaps)."));
    }

    private async Task BuildBranchesAsync(List<ListBoxItem> rows, List<RepoEntry> shown)
    {
        foreach (var repo in shown)
        {
            var branches = await Repos.BranchesAsync(repo);
            rows.Add(HeaderRow(repo, $"{branches.Count(b => !b.IsRemote)} local · {branches.Count(b => b.IsRemote)} remote", null, null));
            foreach (var group in branches.GroupBy(b => b.IsRemote))
            {
                rows.Add(SubHeader(group.Key ? "Remote" : "Local"));
                foreach (var b in group)
                {
                    var name = new TextBlock { Text = b.Name, FontSize = 12.5, FontWeight = b.IsCurrent ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
                    var line = Ui.Row(6, new PathIcon { Data = b.IsCurrent ? Icons.CheckCircle : Icons.SourceBranch, Width = 13, Height = 13, Foreground = b.IsCurrent ? Ui.Tk("ok") : Ui.Dim }, name);
                    if (b.Track.Length > 0) line.Children.Add(new TextBlock { Text = b.Track, FontSize = 11, Foreground = Ui.Tk("warn"), VerticalAlignment = VerticalAlignment.Center });
                    var item = new ListBoxItem
                    {
                        Tag = new BranchRef(repo.Id, b), Padding = new Thickness(12, 4, 8, 4), IsSelected = _branch is not null && _branch.RepoId == repo.Id && _branch.Branch.Name == b.Name,
                        Content = Ui.Column(1, line, Ui.Text($"{b.Date} · {b.Subject}", "dim", 11)),
                    };
                    ToolTip.SetTip(item, b.IsCurrent ? "The branch you are on" : b.Upstream is null ? b.Name : $"{b.Name} tracks {b.Upstream}");
                    var menu = new ContextMenu();
                    menu.Items.Add(Ui.Item(b.IsRemote ? "Switch to a Local Copy" : "Switch to This Branch", () => SwitchAsync(repo, b), !b.IsCurrent, Icons.SourceBranch));
                    menu.Items.Add(Ui.Item("New Branch from Here…", () => NewBranchAsync(repo, b.Name)));
                    menu.Items.Add(Ui.Item("Merge into Current Branch…", async () =>
                    {
                        if (await Ui.ConfirmAsync(Ui.Owner(this), "Merge", $"Merge {b.Name} into the current branch of {repo.Name}?", "Merge")) await MergingAsync(repo, () => Repos.MergeAsync(repo, b.Name));
                    }, !b.IsCurrent));
                    menu.Items.Add(new Separator());
                    menu.Items.Add(Ui.Item("Copy Name", () => { _ = App.Services.Host.SetClipboardAsync(b.Name); }));
                    if (!b.IsRemote)
                        menu.Items.Add(Ui.Item("Delete Branch…", async () =>
                        {
                            if (!await Ui.ConfirmAsync(Ui.Owner(this), "Delete branch", $"Delete the local branch {b.Name} of {repo.Name}? Git refuses if it has work that is not merged.")) return;
                            await Repos.DeleteBranchAsync(repo, b.Name);
                        }, !b.IsCurrent));
                    item.ContextMenu = menu;
                    rows.Add(item);
                }
            }
        }
    }

    // ---- pull requests: a projection of the watch for the repository ----

    private void BuildPullRequests(List<ListBoxItem> rows, List<RepoEntry> shown)
    {
        _building = true;
        try { _mine.IsChecked = State.Data.PullRequestsMineOnly; _active.IsChecked = State.Data.PullRequestsActiveOnly; }
        finally { _building = false; }
        foreach (var repo in shown)
        {
            var watch = Watches.FindRepoPullRequests(repo.Path);
            var menu = RepoMenu(repo);
            if (watch is null)
            {
                rows.Add(HeaderRow(repo, "not set up", null, menu));
                rows.Add(new ListBoxItem
                {
                    Focusable = false, Padding = new Thickness(12, 2, 8, 10), Tag = new HeaderRef(repo),
                    Content = Ui.Column(6,
                        Ui.Text("Pull requests come from a watch, so they refresh on a schedule and notify you when something changes.", "dim", 12),
                        Ui.Apply(Ui.TextButton("Set up pull requests…", () => _ = SetupPullRequestsAsync(repo, null)), b => b.HorizontalAlignment = HorizontalAlignment.Left)),
                });
                continue;
            }
            var all = watch.LastResult?.Items ?? [];
            var items = all.Where(i => (!State.Data.PullRequestsActiveOnly || i.Active != false) && (!State.Data.PullRequestsMineOnly || i.Mine == true)).ToList();
            var detail = watch.LastError is not null ? "error" : watch.LastChecked is null ? "checking…" : $"{items.Count} of {all.Count} · checked {Ui.Ago(watch.LastChecked)}";
            menu.Items.Insert(0, new Separator());
            menu.Items.Insert(0, Ui.Item("Show in Watching", () => { Ui.Main?.Navigate("watching"); Ui.Main?.Page<WatchingPage>("watching")?.Select(watch.Id); }, icon: Icons.Eye));
            menu.Items.Insert(0, Ui.Item("Configure Pull Requests…", () => SetupPullRequestsAsync(repo, watch), icon: Icons.Cog));
            menu.Items.Insert(0, Ui.Item("Check Now", () => Watches.CheckAsync(watch), icon: Icons.Refresh));
            rows.Add(HeaderRow(repo, detail, watch.LastError, menu));
            if (watch.LastError is null && items.Count == 0)
                rows.Add(Note(all.Count == 0 ? (watch.LastChecked is null ? "Checking…" : "No pull requests.") : "No pull requests match the filters above."));
            foreach (var pr in items)
            {
                var reference = new PrRef(repo.Id, watch, pr);
                var status = (pr.Status ?? "open").ToLowerInvariant();
                var chip = Ui.Chip(status, Ui.Tk(status switch
                {
                    "merged" or "completed" => "purple", "closed" or "abandoned" or "declined" => "error", "draft" => "faint",
                    "approved" => "ok", "changes requested" => "warn", _ => "accent",
                }));
                var item = new ListBoxItem
                {
                    Tag = reference, Padding = new Thickness(12, 5, 8, 5), IsSelected = _pr is not null && _pr.RepoId == repo.Id && _pr.Item.Id == pr.Id && _pr.Item.Title == pr.Title,
                    Opacity = pr.Active == false ? 0.65 : 1,
                    Content = Ui.Column(2,
                        new DockPanel { Children = { Ui.Apply(chip, c => c.Margin = new Thickness(8, 0, 0, 0)).Dock(Dock.Right), new TextBlock { Text = pr.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis } } },
                        Ui.Text((pr.Mine == true ? "mine · " : "") + (pr.Detail ?? pr.Author ?? ""), "dim", 11)),
                };
                item.DoubleTapped += (_, _) => { if (pr.Link is { Length: > 0 } l) Shell.Open(l); };
                item.ContextMenu = new ContextMenu
                {
                    Items =
                    {
                        Ui.Item("Open in Browser", () => Shell.Open(pr.Link!), pr.Link is { Length: > 0 }, Icons.OpenInNew),
                        Ui.Item("Copy Link", () => { _ = App.Services.Host.SetClipboardAsync(pr.Link ?? ""); }, pr.Link is { Length: > 0 }),
                        Ui.Item("Copy Title", () => { _ = App.Services.Host.SetClipboardAsync(pr.Title); }),
                    },
                };
                rows.Add(item);
            }
        }
    }

    private async Task CheckPullRequestsAsync()
    {
        var repos = _repoId is null ? AllRepos : AllRepos.Where(r => r.Id == _repoId).ToList();
        var watches = repos.Select(r => Watches.FindRepoPullRequests(r.Path)).Where(w => w is not null).ToList();
        if (watches.Count == 0) { Ui.Toast(this, "Nothing to check", "Set up pull requests for a repository first."); return; }
        await Ui.TryAsync(this, () => Task.WhenAll(watches.Select(w => Watches.CheckAsync(w!))));
    }

    /// <summary>Creates or edits the watch behind a repository's pull request list.</summary>
    private async Task SetupPullRequestsAsync(RepoEntry repo, WatchItem? existing)
    {
        var fallback = App.Services.Config.Settings.Repositories.PullRequestCommand;
        var command = new TextBox
        {
            Text = existing?.Settings.GetValueOrDefault("command") ?? "", Classes = { "mono" }, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap,
            PlaceholderText = fallback.Length > 0 ? fallback : "gh pr list --state all --json number,title,state,isDraft,author,url,headRefName,baseRefName,updatedAt,reviewDecision",
        };
        var me = new TextBox { Text = existing?.Settings.GetValueOrDefault("me") ?? "", PlaceholderText = "Asked from the GitHub CLI when empty" };
        var interval = new TextBox { Text = existing?.IntervalMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", Width = 140, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = $"default ({App.Services.Config.Settings.Watching.DefaultIntervalMinutes:0.##})" };
        var body = Ui.Column(12,
            Ui.Text($"Pull requests of {repo.Name} are kept by a watch named \"{existing?.Name ?? repo.Name + " pull requests"}\". It also appears on the Watching page, where it can be disabled or removed.", "dim", 12.5),
            Ui.Field("Command", command, "Runs in the repository folder and prints a JSON array of pull requests (number, title, state, isDraft, author, url, headRefName, baseRefName, updatedAt; mine and active are optional), " +
                                          "or the usual watch result object. Leave empty to use the GitHub CLI."),
            Ui.Field("Your user name", me, "Used to tell which pull requests are yours when the command does not say."),
            Ui.Field("Minutes between checks", interval, "Empty uses the default from Settings → Watching."));
        WatchItem? saved = null;
        var ok = await Ui.FormAsync(Ui.Owner(this), existing is null ? "Set up pull requests" : "Configure pull requests", body, () =>
        {
            double? minutes = null;
            if (!string.IsNullOrWhiteSpace(interval.Text))
            {
                if (!double.TryParse(interval.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) return Task.FromResult<string?>("Minutes between checks must be a positive number.");
                minutes = parsed;
            }
            var watch = existing ?? Watches.EnsureRepoPullRequests(repo.Name, repo.Path);
            void Set(string key, string? value) { if (string.IsNullOrWhiteSpace(value)) watch.Settings.Remove(key); else watch.Settings[key] = value.Trim(); }
            Set("command", command.Text);
            Set("me", me.Text);
            watch.IntervalMinutes = minutes;
            Watches.Save();
            saved = watch;
            return Task.FromResult<string?>(null);
        }, existing is null ? "Set up" : "Save", 600);
        if (!ok || saved is null) return;
        _ = Watches.CheckAsync(saved);
        await RefreshAsync();
    }

    // ---- menus ----

    private ContextMenu RepoMenu(RepoEntry repo)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Change Branch…", () => ChangeBranchAsync(repo), icon: Icons.SourceBranch));
        menu.Items.Add(Ui.Item("Open Folder", () => Shell.Open(repo.Path), icon: Icons.FolderOpen));
        menu.Items.Add(Ui.Item("Open Remote", async () =>
        {
            var url = RepoService.ToWebUrl(await Repos.RemoteUrlAsync(repo));
            if (url is null) Ui.Toast(this, "No remote", $"{repo.Name} has no remote to open."); else Shell.Open(url);
        }, icon: Icons.OpenInNew));
        menu.Items.Add(Ui.Item("Open Terminal Here", () => OpenTerminalIn(repo), icon: Icons.Console));
        menu.Items.Add(Ui.Item("Open in Editor", () => Ui.OpenInEditor(repo.Path), icon: Icons.Pencil));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Fetch", () => Network(repo, "Fetched", Repos.FetchAsync)));
        menu.Items.Add(Ui.Item("Pull", () => Network(repo, "Pulled", Repos.PullAsync)));
        menu.Items.Add(Ui.Item("Push", () => Network(repo, "Pushed", Repos.PushAsync)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Stage All", () => Repos.StageAllAsync(repo)));
        menu.Items.Add(Ui.Item("Unstage All", () => Repos.UnstageAllAsync(repo)));
        menu.Items.Add(Ui.Item("Commit…", () => CommitAsync(repo), icon: Icons.Check));
        if (_mergeStates.GetValueOrDefault(repo.Id) is { InProgress: true } merge)
        {
            var name = char.ToUpperInvariant(merge.Verb[0]) + merge.Verb[1..];
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item($"Continue {name}", () => ContinueMergeAsync(repo), merge.Conflicts.Count == 0, Icons.Check));
            menu.Items.Add(Ui.Item($"Abort {name}…", () => AbortMergeAsync(repo)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Show Only This Repository", () => SelectRepo(repo.Id), _repoId != repo.Id));
        menu.Items.Add(Ui.Item("Copy Path", () => { _ = App.Services.Host.SetClipboardAsync(repo.Path); }));
        return menu;
    }

    private async Task Network(RepoEntry repo, string done, Func<RepoEntry, Task> action)
    {
        Ui.Toast(this, "Talking to the remote…", repo.Name);
        await action(repo);
        Ui.Toast(this, done, repo.Name);
        _branchSignature = "";
    }

    private ContextMenu CommitMenu(RepoEntry repo, RepoCommit c)
    {
        async Task Guarded(string title, string question, string action, Func<Task> work)
        {
            if (!await Ui.ConfirmAsync(Ui.Owner(this), title, question, action)) return;
            await work();
            Ui.Toast(this, title, $"{repo.Name} · {c.Hash}");
        }
        var reset = new MenuItem { Header = "Reset Current Branch to Here" };
        reset.Items.Add(Ui.Item("Soft: keep changes staged…", () => Guarded("Reset (soft)", $"Move the current branch of {repo.Name} to {c.Hash}? The commits after it are undone; their changes stay staged.", "Reset", () => Repos.ResetAsync(repo, c.Hash, "soft"))));
        reset.Items.Add(Ui.Item("Mixed: keep changes unstaged…", () => Guarded("Reset (mixed)", $"Move the current branch of {repo.Name} to {c.Hash}? The commits after it are undone; their changes stay in your files.", "Reset", () => Repos.ResetAsync(repo, c.Hash, "mixed"))));
        reset.Items.Add(Ui.Item("Hard: discard changes…", () => Guarded("Reset (hard)", $"Move the current branch of {repo.Name} to {c.Hash} and THROW AWAY every change after it, including uncommitted work? This cannot be undone from here.", "Discard and reset", () => Repos.ResetAsync(repo, c.Hash, "hard"))));
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("View Commit", () => ViewCommitAsync(repo, c.Hash), icon: Icons.Eye));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Copy Hash", async () => { var info = await Repos.CommitInfoAsync(repo, c.Hash); await App.Services.Host.SetClipboardAsync(info?.Hash ?? c.Hash); }, icon: Icons.ContentCopy));
        menu.Items.Add(Ui.Item("Copy Message", async () => { var info = await Repos.CommitInfoAsync(repo, c.Hash); await App.Services.Host.SetClipboardAsync(info is null ? c.Subject : (info.Subject + "\n\n" + info.Body).Trim()); }));
        menu.Items.Add(Ui.Item("Open on Remote", async () =>
        {
            var url = RepoService.ToWebUrl(await Repos.RemoteUrlAsync(repo));
            var info = await Repos.CommitInfoAsync(repo, c.Hash);
            if (url is null) Ui.Toast(this, "No remote", $"{repo.Name} has no remote to open."); else Shell.Open($"{url}/commit/{info?.Hash ?? c.Hash}");
        }, icon: Icons.OpenInNew));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("New Branch Here…", () => NewBranchAsync(repo, c.Hash), icon: Icons.SourceBranch));
        menu.Items.Add(Ui.Item("New Tag Here…", async () =>
        {
            var name = await Ui.PromptAsync(Ui.Owner(this), "New tag", $"Tag name for {c.Hash}");
            if (!string.IsNullOrWhiteSpace(name)) { await Repos.TagAsync(repo, name.Trim(), c.Hash); Ui.Toast(this, "Tag created", name.Trim()); }
        }));
        menu.Items.Add(Ui.Item("Check Out This Commit…", () => Guarded("Check out", $"Check out {c.Hash} in {repo.Name}? You will be on a detached HEAD: switch to a branch afterwards to keep working.", "Check out", () => Repos.CheckoutCommitAsync(repo, c.Hash))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Cherry-pick onto Current Branch…", () => Guarded("Cherry-pick", $"Apply the changes of {c.Hash} (\"{c.Subject}\") as a new commit on the current branch of {repo.Name}?", "Cherry-pick", () => MergingAsync(repo, () => Repos.CherryPickAsync(repo, c.Hash)))));
        menu.Items.Add(Ui.Item("Revert This Commit…", () => Guarded("Revert", $"Create a new commit in {repo.Name} that undoes {c.Hash} (\"{c.Subject}\")?", "Revert", () => MergingAsync(repo, () => Repos.RevertAsync(repo, c.Hash)))));
        menu.Items.Add(reset);
        return menu;
    }

    // ---- right-hand pane ----

    private void SetButtons(bool stage = false, bool edit = false, bool modes = false, bool open = false)
    {
        _stage.IsVisible = stage; _edit.IsVisible = edit; _open.IsVisible = open;
        _diffMode.IsVisible = _fileModeButton.IsVisible = modes;
        _changeNav.IsVisible = modes;
        _diffMode.Opacity = _fileMode ? 0.55 : 1;
        _fileModeButton.Opacity = _fileMode ? 1 : 0.55;
    }

    private void SetTitle(string title, string? sub = null)
    {
        _diffTitle.Text = title;
        _diffSub.Text = sub ?? "";
        _diffSub.IsVisible = !string.IsNullOrEmpty(sub);
        ToolTip.SetTip(_diffTitle, sub is null ? title : title + "\n" + sub);
    }

    private async Task ShowAsync()
    {
        var gen = _generation;
        _doc = null; _docRepo = null; _openLink = null;
        // A conflicted file, or a repository's merge row, is the merge tool's to show.
        if (_tab == Tab.Changes && _view is null && _file is null)
        {
            if (_lastSelected is MergeRef mr && Repos.Find(mr.RepoId) is { } mergeRepo && _mergeStates.GetValueOrDefault(mr.RepoId) is { Active: true } summary)
            {
                _merge.ShowSummary(mergeRepo, summary, _repoId is null);
                ShowMerge(true);
                return;
            }
            if (_change is { Staged: false, Change.Description: "conflict" } conflict && Repos.Find(conflict.Change.RepoId) is { } conflictRepo)
            {
                var all = ConflictRows().ToList();
                await _merge.ShowFileAsync(conflictRepo, conflict.Change, _mergeStates.GetValueOrDefault(conflictRepo.Id, MergeState.None), _repoId is null);
                if (gen != _generation) return;
                _merge.SetFilePosition(all.FindIndex(r => r.Tag is ChangeRef x && x.Change.RepoId == conflict.Change.RepoId && x.Change.Path == conflict.Change.Path), all.Count);
                ShowMerge(true);
                return;
            }
        }
        ShowMerge(false);
        List<Row> rows;
        var text = "";
        if (_view is { } view)
        {
            var body = view.Info.Body.Length > 0 ? view.Info.Body.ReplaceLineEndings(" ") : null;
            SetTitle($"{view.Info.ShortHash}  {view.Info.Subject}", $"{view.Repo.Name} · {view.Info.Author} · {view.Info.Date}" + (body is null ? "" : " · " + body));
            ToolTip.SetTip(_diffTitle, $"{view.Info.Hash}\n{view.Info.Subject}\n\n{view.Info.Body}".TrimEnd());
            SetButtons(modes: false);
            if (_viewFile is { } vf) { text = await Repos.CommitFileDiffAsync(view.Repo, vf.Hash, vf.File.Path); rows = DiffRows(DiffDoc.Parse(text)); }
            else rows = [new Row(RowKind.Info, "Select a file of this commit.")];
        }
        else if (_tab == Tab.Changes && _file is { } f && Repos.Find(f.RepoId) is { } fileRepo)
        {
            SetTitle((_repoId is null ? fileRepo.Name + " · " : "") + f.Path, "File");
            SetButtons(edit: true);
            text = await Repos.FileTextAsync(fileRepo, f.Path);
            rows = FileRows(text, f.Path, null);
        }
        else if (_tab == Tab.Changes && _change is { } c && Repos.Find(c.Change.RepoId) is { } repo)
        {
            var change = c.Change;
            SetTitle((_repoId is null ? repo.Name + " · " : "") + change.Path + (change.OriginalPath is { } o ? $"  (was {o})" : ""), c.Staged ? "Staged" : change.Untracked ? "Untracked" : "Not staged");
            _stage.Content = c.Staged ? "Unstage" : "Stage";
            SetButtons(stage: true, edit: !change.Path.EndsWith('/') && Describe(change, c.Staged) != "deleted", modes: true);
            var diff = await Repos.DiffAsync(repo, change.Path, staged: c.Staged, untracked: change.Untracked);
            var doc = DiffDoc.Parse(diff);
            if (_fileMode)
            {
                text = await Repos.FileTextAsync(repo, change.Path, c.Staged ? ":" : null);
                var changed = doc.Lines.Where(l => l.Kind == DiffKind.Add && l.New is not null).Select(l => l.New!.Value).ToHashSet();
                rows = FileRows(text, change.Path, changed);
            }
            else
            {
                text = diff;
                rows = DiffRows(doc);
                // Hunks and lines can be staged from a tracked file's own diff.
                if (!change.Untracked && change.Description != "conflict" && doc.Files.Count == 1) { _doc = doc; _docRepo = repo; _docStaged = c.Staged; }
            }
        }
        else if (_tab == Tab.History && _commit is { } k && Repos.Find(k.RepoId) is { } repo2)
        {
            SetTitle($"{k.Commit.Hash}  {k.Commit.Subject}", $"{repo2.Name} · {k.Commit.Author} · {k.Commit.Date} · right-click the commit for more");
            SetButtons();
            text = await Repos.ShowAsync(repo2, k.Commit.Hash);
            rows = DiffRows(DiffDoc.Parse(text));
        }
        else if (_tab == Tab.Branches && _branch is { } b && Repos.Find(b.RepoId) is { } repo3)
        {
            SetTitle($"{b.Branch.Name}", $"{repo3.Name} · commits on this branch · double-click one to see its files");
            SetButtons();
            var commits = await Repos.LogAsync(repo3, null, b.Branch.Name);
            rows = commits.Select(x => new Row(RowKind.Log, x.Subject, Commit: x)).ToList();
            text = string.Join('\n', commits.Select(x => $"{x.Hash} {x.Date} {x.Author}: {x.Subject}"));
            if (rows.Count == 0) rows.Add(new Row(RowKind.Info, "No commits."));
        }
        else if (_tab == Tab.PullRequests && _pr is { } pr)
        {
            SetTitle(pr.Item.Title, $"{Repos.Find(pr.RepoId)?.Name} · {pr.Item.Status}");
            _openLink = pr.Item.Link;
            SetButtons(open: pr.Item.Link is { Length: > 0 });
            var lines = new List<string> { pr.Item.Title, "" };
            void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label,-10}{value}"); }
            Add("Status", pr.Item.Status); Add("Author", pr.Item.Author + (pr.Item.Mine == true ? " (you)" : "")); Add("Details", pr.Item.Detail); Add("Link", pr.Item.Link);
            lines.Add("");
            lines.Add($"From the watch \"{pr.Watch.Name}\", checked {Ui.Ago(pr.Watch.LastChecked)}.");
            text = string.Join('\n', lines);
            rows = lines.Select(l => new Row(RowKind.Info, l)).ToList();
        }
        else
        {
            SetTitle(_tab switch { Tab.History => "Select a commit", Tab.Branches => "Select a branch", Tab.PullRequests => "Select a pull request", _ => "Nothing changed" });
            SetButtons();
            rows = [];
        }
        if (gen != _generation) return;
        _diffText = text;
        const int max = 20000;
        if (rows.Count > max) { var more = rows.Count - max; rows = rows.Take(max).ToList(); rows.Add(new Row(RowKind.Info, $"… {more} more lines not shown …")); }
        _diff.ItemsSource = rows;
        ResetChangeNav(rows);
        if (_file is { Line: > 0 } target && rows.Count >= target.Line) Dispatcher.UIThread.Post(() => { _diff.SelectedIndex = target.Line - 1; _diff.ScrollIntoView(target.Line - 1); }, DispatcherPriority.Background);
    }

    private void ShowMerge(bool on)
    {
        if (!on) _merge.Collapse();
        _merge.IsVisible = on;
        _right.IsVisible = !on;
        SizeList();
    }

    /// <summary>The list of files gives up room to the merge tool's three panes, and all of it when the tool asks.</summary>
    private void SizeList()
    {
        var hidden = _merge.IsVisible && _merge.Expanded;
        _leftPane.IsVisible = !hidden;
        _main.ColumnDefinitions[0].Width = new GridLength(hidden ? 0 : _merge.IsVisible ? 430 : 500);
        // In the narrower list the Commit button keeps its icon and tooltip only.
        if (_commitButton.Content is StackPanel { Children: [_, TextBlock label] }) label.IsVisible = !_merge.IsVisible;
    }

    /// <summary>
    /// Works out the groups of changed lines in what is shown: in a diff, each run of added or removed lines; in the
    /// file view, each run of lines marked as changed. Only a file or diff of the Changes tab has groups to jump between.
    /// </summary>
    private void ResetChangeNav(List<Row> rows)
    {
        _shownRows = rows;
        _changeSpans = [];
        if (_diffMode.IsVisible)
        {
            bool Changed(Row r) => _fileMode ? r.Kind == RowKind.Code && r.Marked : r.Kind is RowKind.Add or RowKind.Del;
            for (var i = 0; i < rows.Count; i++)
            {
                if (!Changed(rows[i])) continue;
                var end = i;
                while (end + 1 < rows.Count && Changed(rows[end + 1])) end++;
                _changeSpans.Add((i, end));
                i = end;
            }
        }
        _changeIndex = -1;
        _changeUp.IsEnabled = _changeDown.IsEnabled = _changeSpans.Count > 0;
        UpdateChangeLabel();
    }

    /// <summary>Moves to the next (step 1) or previous (step -1) group of changes, wrapping around at either end.</summary>
    private void JumpChange(int step)
    {
        var n = _changeSpans.Count;
        if (n == 0) return;
        var previous = _changeIndex;
        _changeIndex = _changeIndex < 0 ? (step > 0 ? 0 : n - 1) : ((_changeIndex + step) % n + n) % n;
        if (previous >= 0) MarkCurrent(_changeSpans[previous], false);
        MarkCurrent(_changeSpans[_changeIndex], true);
        var start = _changeSpans[_changeIndex].Start;
        _diff.SelectedIndex = start;
        _diff.ScrollIntoView(start);
        UpdateChangeLabel();
    }

    /// <summary>Flags the rows of a change as current (or not) and repaints the ones on screen.</summary>
    private void MarkCurrent((int Start, int End) span, bool current)
    {
        for (var i = span.Start; i <= span.End; i++)
        {
            // One box around the whole group: every row has the side edges, only the first and last close it off.
            _shownRows[i].Current = current;
            _shownRows[i].Edges = current ? new Thickness(1, i == span.Start ? 1 : 0, 1, i == span.End ? 1 : 0) : default;
            if (_diff.ContainerFromIndex(i) is { } container
                && container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Tag as string == "frame") is { } frame)
                StyleFrame(frame, _shownRows[i]);
        }
    }

    private void UpdateChangeLabel() =>
        _changeCount.Text = _changeSpans.Count == 0 ? "No changes" : $"{_changeIndex + 1} of {_changeSpans.Count}";

    private static List<Row> DiffRows(DiffDoc doc) => doc.Lines.Select(l => new Row(l.Kind switch
    {
        DiffKind.Meta => RowKind.Meta, DiffKind.FileHeader => RowKind.FileHeader, DiffKind.Hunk => RowKind.Hunk, DiffKind.Add => RowKind.Add,
        DiffKind.Del => RowKind.Del, DiffKind.NoNewline => RowKind.Note, _ => RowKind.Context,
    }, l.Text, l.Old, l.New, l)).ToList();

    /// <summary>A whole file, highlighted for its language. Lines in <paramref name="changed"/> get a mark in the margin.</summary>
    private static List<Row> FileRows(string text, string path, HashSet<int>? changed)
    {
        var lines = Syntax.Highlight(text.Replace("\r\n", "\n").TrimEnd('\n'), Syntax.LanguageOf(path));
        var rows = new List<Row>(lines.Count);
        for (var i = 0; i < lines.Count; i++) rows.Add(new Row(RowKind.Code, "", null, i + 1, null, lines[i], changed?.Contains(i + 1) == true));
        return rows;
    }

    private static Control BuildRow(Row r)
    {
        // The highlight box is a border laid over the row rather than around it: it takes up no room, so nothing
        // moves when a change is selected, and the row's own content (such as the change bar) cannot cover it.
        var box = new Border { IsHitTestVisible = false, BorderBrush = Ui.Tk("accent") };
        var frame = new Border { Tag = "frame", Child = new Panel { Children = { BuildRowContent(r), box } } };
        StyleFrame(frame, r);
        return frame;
    }

    private static IBrush RowBackground(Row r) => r.Kind switch
    {
        RowKind.Add => Ui.Tk("addBg"),
        RowKind.Del => Ui.Tk("delBg"),
        RowKind.Hunk => Ui.Tk("hunkBg"),
        RowKind.Code when r.Marked => Ui.Tk("addBg"),
        _ => Brushes.Transparent,
    };

    /// <summary>
    /// The current change is a darker tint of the row's own colour inside one thin box in the theme's accent: each
    /// row draws the box's sides, and only the first and last rows of the change draw its top and bottom.
    /// </summary>
    private static void StyleFrame(Border frame, Row r)
    {
        var bg = RowBackground(r);
        frame.Background = r.Current && bg is SolidColorBrush sb ? new SolidColorBrush(Darker(sb.Color)) : bg;
        if (frame.Child is Panel { Children: [_, Border box] })
        {
            box.BorderBrush = Ui.Tk("accent");
            box.BorderThickness = r.Current ? r.Edges : default;
        }
    }

    private static Avalonia.Media.Color Darker(Avalonia.Media.Color c) =>
        Avalonia.Media.Color.FromArgb(c.A, (byte)(c.R * 0.84), (byte)(c.G * 0.84), (byte)(c.B * 0.84));

    private static Control BuildRowContent(Row r)
    {
        if (r.Kind == RowKind.Log && r.Commit is { } c)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 3),
                Children =
                {
                    new TextBlock { Text = c.Hash, FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.Tk("warn"), Width = 74 },
                    new TextBlock { Text = c.Date, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Dim, Width = 122 },
                    new TextBlock { Text = c.Author, FontSize = 12.5, Foreground = Ui.Tk("textMute"), Width = 150, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = c.Subject, FontSize = 12.5 },
                },
            };
        }
        var numbered = r.Kind is RowKind.Context or RowKind.Add or RowKind.Del or RowKind.Hunk or RowKind.Note or RowKind.Code;
        var (fg, bar) = r.Kind switch
        {
            RowKind.Add => (Ui.Tk("okHi"), Ui.Tk("ok")),
            RowKind.Del => (Ui.Tk("errorText"), Ui.Tk("error")),
            RowKind.Hunk => (Ui.Tk("link"), Ui.Tk("accent")),
            RowKind.FileHeader or RowKind.Note => (Ui.Tk("dim"), (IBrush)Brushes.Transparent),
            RowKind.Meta => (r.Text.StartsWith("commit ", StringComparison.Ordinal) ? Ui.Tk("warn") : Ui.Tk("textCode"), (IBrush)Brushes.Transparent),
            RowKind.Code => (Ui.Tk("textCode"), r.Marked ? Ui.Tk("ok") : Brushes.Transparent),
            _ => (Ui.Tk("textCode"), Brushes.Transparent),
        };
        var text = new TextBlock { FontFamily = Ui.Mono, FontSize = 12.5, Foreground = fg, Padding = new Thickness(10, 0, 12, 0), Tag = "text" };
        if (r.Spans is not null) { MarkdownView.Fill(text.Inlines!, r.Spans); if (r.Spans.Count == 0) text.Text = " "; }
        else text.Text = (r.Kind switch { RowKind.Add => "+", RowKind.Del => "-", RowKind.Context => " ", _ => "" }) + (r.Text.Length > 0 || r.Kind is RowKind.Add or RowKind.Del or RowKind.Context ? r.Text : " ");
        if (!numbered) return new Border { Child = text };

        TextBlock Number(int? n) => new()
        {
            Text = n?.ToString() ?? "", Width = 46, TextAlignment = TextAlignment.Right, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tk("ghost"),
            Padding = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "number", Background = Brushes.Transparent,
        };
        // The coloured strip on the left is the hunk's handle; the numbers are each line's.
        var strip = new Border { Width = 10, Background = bar, Tag = "strip" };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(r.Kind == RowKind.Code ? "10,0,Auto,*" : "10,Auto,Auto,*") };
        grid.Children.Add(strip);
        if (r.Kind != RowKind.Code) grid.Children.Add(Number(r.Old).At(0, 1));
        grid.Children.Add(Number(r.New).At(0, 2));
        grid.Children.Add(text.At(0, 3));
        return grid;
    }

    /// <summary>
    /// Right-click in the diff. On the coloured strip it offers the hunk; on a line number, that one line; on the
    /// text, both. Staged diffs offer unstaging, unstaged ones staging and discarding.
    /// </summary>
    private void OnDiffContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;
        var part = source.GetSelfAndVisualAncestors().OfType<Control>().TakeWhile(x => x is not ListBoxItem).Select(x => x.Tag as string).FirstOrDefault(t => t is not null) ?? "text";
        var item = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is not Row row) return;
        var menu = new ContextMenu();
        if (_doc is { } doc && _docRepo is { } repo && row.Diff is { } line && doc.CanPatchHunk(line))
        {
            var staged = _docStaged;
            var hunkNo = line.Hunk + 1;
            var canLine = doc.CanPatchLine(line);
            var lineNo = line.Kind == DiffKind.Add ? line.New : line.Old;
            MenuItem Hunk(string label, bool cached, bool reverse, bool confirm = false) => Ui.Item(label, async () =>
            {
                if (confirm && !await Ui.ConfirmAsync(Ui.Owner(this), "Discard hunk", $"Throw away hunk {hunkNo} of the changes to {doc.Files[line.File].Path}? This cannot be undone.", "Discard")) return;
                await Repos.ApplyPatchAsync(repo, doc.HunkPatch(line.File, line.Hunk, reverse), cached, reverse);
            });
            MenuItem Line(string label, bool cached, bool reverse, bool confirm = false) => Ui.Item(label, async () =>
            {
                if (confirm && !await Ui.ConfirmAsync(Ui.Owner(this), "Discard line", $"Throw away the change on line {lineNo} of {doc.Files[line.File].Path}? This cannot be undone.", "Discard")) return;
                await Repos.ApplyPatchAsync(repo, doc.LinePatch(line.File, line.Hunk, line.Body, reverse), cached, reverse);
            });
            if (canLine && part != "strip")
            {
                menu.Items.Add(staged ? Line($"Unstage Line {lineNo}", true, true) : Line($"Stage Line {lineNo}", true, false));
                if (!staged) menu.Items.Add(Line($"Discard Line {lineNo}…", false, true, true));
            }
            if (part != "number" || !canLine)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                menu.Items.Add(staged ? Hunk($"Unstage Hunk {hunkNo}", true, true) : Hunk($"Stage Hunk {hunkNo}", true, false));
                if (!staged) menu.Items.Add(Hunk($"Discard Hunk {hunkNo}…", false, true, true));
            }
            menu.Items.Add(new Separator());
        }
        var copy = row.Commit is { } c ? $"{c.Hash} {c.Subject}" : row.Spans is not null ? string.Concat(row.Spans.Select(s => s.Text)) : row.Text;
        menu.Items.Add(Ui.Item("Copy Line", () => { _ = App.Services.Host.SetClipboardAsync(copy); }));
        if (row.Commit is { } commit && _branch is { } b && Repos.Find(b.RepoId) is { } branchRepo)
        {
            menu.Items.Insert(0, new Separator());
            menu.Items.Insert(0, Ui.Item("View Commit", () => ViewCommitAsync(branchRepo, commit.Hash), icon: Icons.Eye));
        }
        e.Handled = true;
        menu.Open(item);
    }

    // ---- actions ----

    private async Task ViewCommitAsync(RepoEntry repo, string hash)
    {
        await Ui.TryAsync(this, async () =>
        {
            var info = await Repos.CommitInfoAsync(repo, hash) ?? throw new InvalidOperationException($"Commit {hash} was not found in {repo.Name}.");
            var files = await Repos.CommitFilesAsync(repo, info.Hash);
            _view = new CommitView(repo, info, files);
            // Coming back to History should land on this commit, wherever it is in the list.
            if (_tab == Tab.History) _revealCommit = (repo.Id, info.Hash);
            _viewFile = files.Count > 0 ? new CommitFileRef(repo.Id, info.Hash, files[0]) : null;
            _lastSelected = null;
            await RefreshAsync();
        }, "Could not open the commit");
    }

    private async Task StageSelectedAsync()
    {
        if (_change is not { } c || Repos.Find(c.Change.RepoId) is not { } repo) return;
        await Ui.TryAsync(this, () => c.Staged ? Repos.UnstageAsync(repo, c.Change.Path) : Repos.StageAsync(repo, c.Change.Path), "git said no");
    }

    private async Task DiscardAsync(RepoEntry repo, RepoChange c)
    {
        var question = c.Untracked ? $"Delete {c.Path}? It is not tracked by git, so it cannot be brought back." : $"Throw away the changes to {c.Path} that are not staged? This cannot be undone.";
        if (!await Ui.ConfirmAsync(Ui.Owner(this), c.Untracked ? "Delete file" : "Discard changes", question, c.Untracked ? "Delete" : "Discard")) return;
        await Repos.DiscardAsync(repo, c.Path, c.Untracked);
    }

    private async Task CommitAsync(RepoEntry? repo)
    {
        if (repo is null) { Ui.Toast(this, "Choose a repository first", "Select one in the list, or right-click its name."); return; }
        await CommitDialogAsync(this, repo);
    }

    /// <summary>
    /// The commit dialog, shared by this page and the wiki. With <paramref name="everything"/> every change is
    /// committed, staged or not (they are staged when the dialog is confirmed); otherwise only what is staged.
    /// Returns true when a commit was made.
    /// </summary>
    public static async Task<bool> CommitDialogAsync(Control from, RepoEntry repo, bool everything = false)
    {
        var status = await Repos.StatusAsync(repo);
        var files = everything ? status.Changes.Select(c => c.Path).Distinct().ToList() : status.Changes.Where(c => c.Staged).Select(c => c.Path).Distinct().ToList();
        if (files.Count == 0)
        {
            if (everything) Ui.Toast(from, "Nothing to commit", $"{repo.Name} has no changes since the last commit.");
            else Ui.Toast(from, "Nothing is staged", $"Stage files, hunks or lines in {repo.Name} first.");
            return false;
        }
        var message = new TextBox { AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap, PlaceholderText = "What changed, and why" };
        var summary = $"{files.Count} {(everything ? "changed" : "staged")} file{(files.Count == 1 ? "" : "s")} on {status.Branch ?? "?"}.";
        var list = Ui.Apply(Ui.Text(string.Join("\n", files.Take(12)) + (files.Count > 12 ? $"\n… and {files.Count - 12} more" : ""), "dim", 12), t => { t.FontFamily = Ui.Mono; t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; });
        return await Ui.FormAsync(Ui.Owner(from), $"Commit to {repo.Name}", Ui.Column(10, Ui.Text(summary, "dim", 12.5), list, Ui.Field("Message", message)), async () =>
        {
            if (string.IsNullOrWhiteSpace(message.Text)) return "Write a commit message.";
            if (everything) await Repos.StageAllAsync(repo);
            await Repos.CommitAsync(repo, message.Text.Trim());
            return null;
        }, "Commit", 560);
    }

    private async Task SwitchAsync(RepoEntry repo, RepoBranch branch)
    {
        await Repos.SwitchAsync(repo, branch);
        _branchSignature = "";
        Ui.Toast(this, "Switched branch", $"{repo.Name} is now on {(branch.IsRemote ? branch.Name[(branch.Name.IndexOf('/') + 1)..] : branch.Name)}");
    }

    private async Task NewBranchAsync(RepoEntry repo, string from)
    {
        var name = await Ui.PromptAsync(Ui.Owner(this), "New branch", $"Name of the new branch, starting at {from}");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Repos.CreateBranchAsync(repo, name.Trim(), from);
        _branchSignature = "";
        Ui.Toast(this, "Branch created", $"{repo.Name} is now on {name.Trim()}");
    }

    /// <summary>The "Change Branch" dialog: pick an existing branch, or type a new name to create one.</summary>
    private async Task ChangeBranchAsync(RepoEntry repo)
    {
        var branches = await Repos.BranchesAsync(repo);
        var current = branches.FirstOrDefault(b => b.IsCurrent);
        var filter = new TextBox { PlaceholderText = "Filter, or type a new branch name" };
        var list = new ListBox { Classes = { "plain" }, MaxHeight = 320, MinHeight = 120 };
        var create = new CheckBox { Content = "Create this branch", IsVisible = false };
        void Fill()
        {
            var text = (filter.Text ?? "").Trim();
            list.Items.Clear();
            foreach (var b in branches.Where(b => text.Length == 0 || b.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new ListBoxItem
                {
                    Tag = b, IsSelected = b.IsCurrent && text.Length == 0,
                    Content = Ui.Row(8, new PathIcon { Data = b.IsCurrent ? Icons.CheckCircle : Icons.SourceBranch, Width = 13, Height = 13, Foreground = b.IsCurrent ? Ui.Tk("ok") : Ui.Dim },
                        new TextBlock { Text = b.Name }, new TextBlock { Text = b.IsRemote ? "remote" : b.Date, Foreground = Ui.Dim, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }),
                });
            var exists = branches.Any(b => b.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
            create.IsVisible = text.Length > 0 && !exists;
            create.Content = $"Create a new branch \"{text}\" from {current?.Name ?? "HEAD"}";
            create.IsChecked = create.IsVisible && list.Items.Count == 0;
            if (list.SelectedItem is null && list.Items.Count > 0 && text.Length > 0) list.SelectedIndex = 0;
        }
        filter.TextChanged += (_, _) => Fill();
        Fill();
        await Ui.FormAsync(Ui.Owner(this), $"Change branch — {repo.Name}", Ui.Column(10,
            Ui.Text($"Currently on {current?.Name ?? "a detached HEAD"}.", "dim", 12.5), filter, list, create), async () =>
        {
            if (create.IsVisible && create.IsChecked == true) await Repos.CreateBranchAsync(repo, filter.Text!.Trim());
            else if ((list.SelectedItem as ListBoxItem)?.Tag is RepoBranch b) { if (!b.IsCurrent) await Repos.SwitchAsync(repo, b); }
            else return "Pick a branch.";
            _branchSignature = "";
            return null;
        }, "Switch", 520);
    }

    private void EditFile()
    {
        Ui.Try(this, () =>
        {
            if (_file is { } f && Repos.Find(f.RepoId) is { } fileRepo) Ui.OpenInEditor(f.Path, fileRepo.Path);
            else if (_change is { } c && Repos.Find(c.Change.RepoId) is { } repo) Ui.OpenInEditor(c.Change.Path, repo.Path);
        }, "Could not start the editor");
    }

    private void OpenTerminal()
    {
        if (CurrentRepo is not { } repo) { Ui.Toast(this, "Select a repository first"); return; }
        OpenTerminalIn(repo);
    }

    private void OpenTerminalIn(RepoEntry repo) => Ui.Try(this, () =>
    {
        var session = App.Services.Terminals.Create(new TerminalStartOptions(Cwd: repo.Path));
        App.Services.Host.Navigate("terminals");
        App.Services.Host.SelectTerminal(session.Id);
    });

    private async Task AddAsync()
    {
        string? path = null;
        try
        {
            var picked = await Ui.Owner(this).StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add a git repository", AllowMultiple = false });
            path = picked.FirstOrDefault()?.TryGetLocalPath();
        }
        catch { /* no folder picker available: fall through to typing the path */ }
        path ??= await Ui.PromptAsync(Ui.Owner(this), "Add repository", "Path to a folder inside a git repository", App.Services.Paths.WorkingDir);
        if (string.IsNullOrWhiteSpace(path)) return;
        await Ui.TryAsync(this, async () => { _repoId = (await Repos.AddAsync(path.Trim())).Id; ClearSelection(); await RefreshAsync(); }, "Could not add the repository");
    }

    private async Task RemoveAsync()
    {
        if (_repoId is null || Repos.Find(_repoId) is not { } repo) return;
        if (!await Ui.ConfirmAsync(Ui.Owner(this), "Remove repository", $"Remove \"{repo.Name}\" from the list? The files stay where they are.", "Remove")) return;
        Ui.Try(this, () => Repos.Remove(repo.Id));
    }
}
