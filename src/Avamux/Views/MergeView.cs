using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Avamux.Core;
using Avamux.Core.Repos;
using Avamux.Core.Text;

namespace Avamux.Views;

/// <summary>
/// The merge tool of the Repositories page. For a conflicted file it shows three aligned panes: yours, the result
/// and theirs. Every change of either side can be taken into the result or left out; conflicts must be settled
/// before the file can be marked resolved. For a repository in the middle of a merge it shows where things stand.
/// </summary>
public sealed class MergeView : UserControl
{
    private const double RowHeight = 20;

    private enum Look { Plain, Ours, Theirs, Same, Conflict, Custom, Filler, Open }
    private enum Pane { Ours, Result, Theirs }

    /// <summary>One line of one pane. The three panes always hold the same number of rows, so they line up.</summary>
    private sealed class MRow
    {
        public int Chunk, Offset;
        public int? Number;          // the line number in the side's file; result rows are numbered as they are drawn
        public bool Line;            // false for a filler row that only keeps the panes aligned
        public string Text = "";
        public IReadOnlyList<Span>? Spans;
        public Look Look;
        public bool First, Last, Dim, Change;
        public IReadOnlyList<(int Start, int End)>? Marks;   // the stretches of the line that differ from the line it replaced
    }

    private sealed class PaneUi
    {
        public required Pane Pane;
        public required ItemsControl Items;
        public required ScrollViewer Scroll;
        public required TextBlock Label;
        public AvaloniaList<MRow> Rows = [];
    }

    private static RepoService Repos => App.Services.Repos;
    private static readonly Dictionary<string, (int Signature, MergeDoc Doc)> Sessions = [];

    private readonly PaneUi[] _panes;
    private readonly Grid _paneGrid;
    private readonly ScrollViewer _simple;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = Ui.Mono, FontSize = 12.5 };
    private readonly TextBlock _sub = new() { Foreground = Ui.Dim, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _status = new() { CornerRadius = Ui.R(4), Padding = new Thickness(9, 3), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _navLabel = new() { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 92, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _fileLabel = new() { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextAlignment = TextAlignment.Center };
    private readonly Button _save, _up, _down, _acceptOurs, _acceptTheirs, _reset, _expand, _filePrev, _fileNext, _editFile, _editToggle;
    private readonly TextEditor _editor = new()
    {
        ShowLineNumbers = true, FontFamily = Ui.Mono, FontSize = 12.5, IsVisible = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly SyntaxColorizer _colorizer = new();
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private FileSystemWatcher? _watcher;
    private string? _watchedFile;
    private bool _editing;
    private Dictionary<int, IReadOnlyList<(int Start, int End)>> _wordOurs = [], _wordTheirs = [];
    private readonly CheckBox _syncBox = new() { Content = "Sync scrolling", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _tools, _fileNav;
    private readonly Border _toolBar;

    private RepoEntry? _repo;
    private string _path = "";
    private MergeState _state = MergeState.None;
    private MergeDoc? _doc;
    private string? _shownKey;
    private int _shownSignature;
    private List<List<Span>> _hlBase = [], _hlOurs = [], _hlTheirs = [];
    private int[] _heights = [], _resultStart = [];
    private int _current = -1, _generation;
    private bool _syncing, _expanded;
    private IBrush _bOurs = Brushes.Transparent, _bTheirs = Brushes.Transparent, _bSame = Brushes.Transparent, _bConflict = Brushes.Transparent,
        _bCustom = Brushes.Transparent, _bFiller = Brushes.Transparent, _mOurs = Brushes.Transparent, _mTheirs = Brushes.Transparent,
        _mSame = Brushes.Transparent, _mConflict = Brushes.Transparent;

    /// <summary>Called after a file was marked resolved, so the page can move on to the next one.</summary>
    public Action<RepoEntry, string>? Saved { get; set; }
    /// <summary>Moves to the next (1) or previous (-1) conflicted file.</summary>
    public Action<int>? StepFile { get; set; }
    /// <summary>Asks the page to give the merge tool the whole width (true) or to bring the file list back.</summary>
    public Action<bool>? ExpandChanged { get; set; }
    public Func<RepoEntry, Task>? ContinueMerge { get; set; }
    public Func<RepoEntry, Task>? AbortMerge { get; set; }
    public Action<RepoEntry>? ResolveNext { get; set; }

    public MergeView()
    {
        _panes = new[] { Pane.Ours, Pane.Result, Pane.Theirs }.Select(MakePane).ToArray();
        _status.Child = _statusText;

        _up = Ui.TextButton("↑", () => Jump(-1), "Previous conflict (wraps around)");
        _down = Ui.TextButton("↓", () => Jump(1), "Next conflict (wraps around)");
        _acceptOurs = AcceptButton(false);
        _acceptTheirs = AcceptButton(true);
        _reset = Ui.TextButton("Reset", () => { if (_doc is { } d) { d.ResetAll(); _current = -1; RebuildAll(true); PushToFile(); } }, "Start this file over: every conflict open again, every other change applied, every hand edit undone");
        _editFile = Ui.IconButton(Icons.Pencil, "", () => Ui.Try(this, EditInEditor, "Could not start the editor"), "Editor");
        ToolTip.SetTip(_editFile, $"Edit the result in {App.Services.Config.Settings.Repositories.Editor} (repositories.editor) in a terminal tab.\nConflicts still open are between <<<<<<< and >>>>>>> markers. Each save there updates the Result pane here; what you did not touch keeps its decision.");
        _editToggle = Ui.TextButton("Edit Result", () => { if (_editing) EndEdit(); else BeginEdit(); });
        _watchTimer.Tick += (_, _) => { _watchTimer.Stop(); ReadFromFile(); };
        _save = Ui.TextButton("Mark Resolved", () => _ = SaveAsync(), "Write the result to the file and stage it");
        _expand = Ui.TextButton("⤢", () => { _expanded = !_expanded; ExpandChanged?.Invoke(_expanded); UpdateExpand(); });
        _filePrev = Ui.TextButton("◀", () => StepFile?.Invoke(-1), "Previous conflicted file");
        _fileNext = Ui.TextButton("▶", () => StepFile?.Invoke(1), "Next conflicted file");
        foreach (var b in new[] { _up, _down, _expand, _filePrev, _fileNext }) b.Padding = new Thickness(9, 6);
        UpdateExpand();

        _syncBox.IsChecked = App.Services.Ui.Data.MergeSyncScroll;
        ToolTip.SetTip(_syncBox, "Scroll the three panes together. Switch it off to look around one pane on its own.");
        _syncBox.IsCheckedChanged += (_, _) =>
        {
            App.Services.Ui.Data.MergeSyncScroll = Sync;
            App.Services.Ui.SaveSoon();
            // Switching it back on lines the other panes up with the result.
            if (Sync) SetOffset(_panes[1].Scroll.Offset.Y);
        };

        _fileNav = Ui.Row(2, _filePrev, _fileLabel, _fileNext);
        _tools = Ui.Row(6, Ui.Row(2, _up, _down, _navLabel), _acceptOurs, _acceptTheirs, _reset, _editToggle, _editFile, _syncBox);
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.ConvertTabsToSpaces = false;
        _editor.TextArea.TextView.Margin = new Thickness(8, 0, 0, 0);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextChanged += (_, _) => _colorizer.Invalidate();
        // Tunnelled from the whole tool: the editor's own text area would otherwise take Escape for itself, and the
        // key should work while a toolbar button still has the focus.
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && _editing) { e.Handled = true; EndEdit(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _toolBar = new Border { Padding = new Thickness(12, 4), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = _tools };
        _paneGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        for (var i = 0; i < 3; i++)
        {
            var p = _panes[i];
            var swatch = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, Tag = "swatch" };
            _paneGrid.Children.Add(new Border
            {
                Padding = new Thickness(10, 5), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Tk("bgSide"),
                Child = Ui.Row(7, swatch, p.Label),
            }.At(0, i * 2));
            _paneGrid.Children.Add(p.Scroll.At(1, i * 2));
            if (p.Pane == Pane.Result) _paneGrid.Children.Add(_editor.At(1, i * 2));
            if (i < 2)
                _paneGrid.Children.Add(Ui.Apply(new GridSplitter { Background = Ui.Line, ResizeDirection = GridResizeDirection.Columns }, s => Grid.SetRowSpan(s, 2)).At(0, i * 2 + 1));
        }
        _simple = new ScrollViewer { IsVisible = false };

        Content = new DockPanel
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
                            Ui.Row(6, _status, _fileNav, _expand, _save).Dock(Dock.Right),
                            new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _sub } },
                        },
                    },
                }.Dock(Dock.Top),
                _toolBar.Dock(Dock.Top),
                new Panel { Children = { _paneGrid, _simple } },
            },
        };
        Ui.ThemeChanged += () =>
        {
            _editor.Background = Ui.Tk("bgEditor"); _editor.Foreground = Ui.Tk("textCode"); _editor.LineNumbersForeground = Ui.Tk("ghost");
            _editor.TextArea.TextView.Redraw();
            if (_doc is not null && _paneGrid.IsVisible) RebuildAll(true);
        };
        _editor.Background = Ui.Tk("bgEditor"); _editor.Foreground = Ui.Tk("textCode"); _editor.LineNumbersForeground = Ui.Tk("ghost");
        DetachedFromVisualTree += (_, _) => StopWatching();
    }

    private bool Sync => _syncBox.IsChecked == true;
    /// <summary>True while the tool has asked for the whole width of the page.</summary>
    public bool Expanded => _expanded;

    private void UpdateExpand()
    {
        _expand.Content = _expanded ? "⤡" : "⤢";
        ToolTip.SetTip(_expand, _expanded ? "Bring the list of files back" : "Give the merge tool the whole width (the arrows still move between conflicted files)");
    }

    /// <summary>Tells the tool which of the conflicted files it is showing, for the file arrows.</summary>
    public void SetFilePosition(int index, int count)
    {
        _fileLabel.Text = count == 0 ? "" : $"File {index + 1} of {count}";
        _filePrev.IsEnabled = _fileNext.IsEnabled = count > 1;
        _fileNav.IsVisible = count > 0;
    }

    /// <summary>Puts the file list back if the tool had taken its room; called when the tool is hidden.</summary>
    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        ExpandChanged?.Invoke(false);
        UpdateExpand();
    }

    // ---- panes ----

    private PaneUi MakePane(Pane pane)
    {
        var items = new ItemsControl { ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
        items.ItemTemplate = new FuncDataTemplate<MRow>((row, _) => row is null ? new Panel() : BuildRow(row, pane), false);
        var scroll = new ScrollViewer
        {
            Content = items, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Tk("bgEditor"),
        };
        var ui = new PaneUi { Pane = pane, Items = items, Scroll = scroll, Label = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis } };
        items.ItemsSource = ui.Rows;
        scroll.ScrollChanged += (_, e) =>
        {
            if (!Sync || _syncing || e.OffsetDelta.Y == 0) return;
            _syncing = true;
            try { foreach (var other in _panes) if (other != ui) other.Scroll.Offset = new Vector(other.Scroll.Offset.X, scroll.Offset.Y); }
            finally { _syncing = false; }
        };
        return ui;
    }

    private void SetOffset(double y)
    {
        _syncing = true;
        try { foreach (var p in _panes) p.Scroll.Offset = new Vector(p.Scroll.Offset.X, Math.Max(0, y)); }
        finally { _syncing = false; }
    }

    /// <summary>
    /// The theme colour that stands for "theirs". "Yours" is the accent, so this is whichever of the theme's other
    /// colours is furthest from it on the colour wheel, and from the red that marks conflicts: in a theme whose
    /// accent is pink, purple would be too close to tell the two sides apart.
    /// </summary>
    private static string TheirsToken()
    {
        static double Hue(Color c) => c.ToHsv().H;
        static double Apart(double a, double b) { var d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }
        double accent = Hue(Ui.Color("accent")), error = Hue(Ui.Color("error"));
        return new[] { "purple", "cyan", "orange" }.MaxBy(t => { var h = Hue(Ui.Color(t)); return Math.Min(Apart(h, accent), Apart(h, error)); })!;
    }

    private static IBrush Tint(string token, double opacity) => new SolidColorBrush(Ui.Color(token), opacity);

    private IBrush RowBackground(MRow r) => r.Look switch
    {
        Look.Ours => _bOurs, Look.Theirs => _bTheirs, Look.Same => _bSame, Look.Conflict or Look.Open => _bConflict, Look.Custom => _bCustom, Look.Filler => _bFiller, _ => Brushes.Transparent,
    };

    private Control BuildRow(MRow r, Pane pane)
    {
        var grid = new Grid { Height = RowHeight, ColumnDefinitions = new ColumnDefinitions("44,38,*"), Background = RowBackground(r) };
        var chunk = _doc is { } doc && r.Chunk < doc.Chunks.Count ? doc.Chunks[r.Chunk] : null;

        if (chunk is { IsChange: true } && r.First && !_editing)
        {
            var gutter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (pane == Pane.Result)
            {
                var (glyph, brush, tip) = !chunk.Resolved ? ("⚠", Ui.Tk("errorHi"), "Unresolved conflict: click to choose")
                    : chunk.IsConflict ? ("✓", Ui.Tk("ok"), "Resolved conflict: click to change") : ("⋯", Ui.Dim, "Click for what the result takes here");
                gutter.Children.Add(Glyph(glyph, brush, tip, c => OpenChunkMenu(chunk, r.Chunk, c), 40));
            }
            else
            {
                var theirs = pane == Pane.Theirs;
                if (chunk.Has(theirs))
                {
                    var state = chunk.State(theirs);
                    var who = theirs ? "theirs" : "yours";
                    if (state != SideState.Applied)
                        gutter.Children.Add(Glyph(theirs ? "«" : "»", Ui.Tk("ok"), chunk.IsConflict && chunk.State(!theirs) == SideState.Applied ? $"Add {who} to the result as well" : $"Take {who} into the result", _ => Act(r.Chunk, d => d.Apply(chunk, theirs))));
                    else
                        gutter.Children.Add(Glyph("✓", Ui.Tk("ok"), $"{char.ToUpperInvariant(who[0])}{who[1..]} is in the result", null));
                    if (state != SideState.Rejected)
                        gutter.Children.Add(Glyph("✕", Ui.Tk("error"), $"Leave {who} out of the result", _ => Act(r.Chunk, d => d.Reject(chunk, theirs))));
                }
            }
            grid.Children.Add(gutter);
        }

        var number = new TextBlock
        {
            Text = pane == Pane.Result ? ResultNumber(r) : r.Number?.ToString() ?? "", TextAlignment = TextAlignment.Right, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tk("ghost"),
            Padding = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "number",
        };
        grid.Children.Add(number.At(0, 1));

        var text = new TextBlock { FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.Tk("textCode"), Padding = new Thickness(4, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = r.Dim ? 0.45 : 1 };
        if (r.Look == Look.Open) { text.Text = r.Text; text.Foreground = Ui.Tk("errorHi"); text.FontFamily = FontFamily.Default; text.FontSize = 12; text.FontStyle = FontStyle.Italic; }
        else if (r.Marks is null && r.Spans is not null) MarkdownView.Fill(text.Inlines!, r.Spans);
        else if (r.Marks is null) text.Text = r.Text;
        else FillMarked(text.Inlines!, r.Spans ?? [new Span(Tok.Text, r.Text)], r.Marks, r.Look switch { Look.Theirs => _mTheirs, Look.Same => _mSame, Look.Conflict => _mConflict, _ => _mOurs });
        if (r.Dim && r.Line) text.TextDecorations = TextDecorations.Strikethrough;
        grid.Children.Add(text.At(0, 2));

        if (chunk is { IsChange: true })
        {
            // The box around the change the arrows last went to; it takes no room, so nothing moves.
            var box = new Border { IsHitTestVisible = false, BorderBrush = Ui.Tk("accent"), BorderThickness = r.Chunk == _current ? new Thickness(1, r.First ? 1 : 0, 1, r.Last ? 1 : 0) : default };
            Grid.SetColumnSpan(box, 3);
            grid.Children.Add(box);
            grid.PointerPressed += (_, e) => { if (e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed && _current != r.Chunk) SetCurrent(r.Chunk, false); };
            if (pane == Pane.Result) grid.ContextRequested += (_, e) => { e.Handled = true; OpenChunkMenu(chunk, r.Chunk, grid); };
        }
        return grid;
    }

    /// <summary>A highlighted line whose changed words also get a stronger background.</summary>
    private static void FillMarked(Avalonia.Controls.Documents.InlineCollection target, IReadOnlyList<Span> spans, IReadOnlyList<(int Start, int End)> marks, IBrush mark)
    {
        var at = 0;
        foreach (var s in spans)
        {
            // Cut the span wherever a marked stretch begins or ends inside it.
            var cuts = new SortedSet<int> { 0, s.Text.Length };
            foreach (var (start, end) in marks)
            {
                if (start > at && start < at + s.Text.Length) cuts.Add(start - at);
                if (end > at && end < at + s.Text.Length) cuts.Add(end - at);
            }
            var edges = cuts.ToList();
            for (var i = 0; i + 1 < edges.Count; i++)
            {
                var from = at + edges[i];
                var run = new Avalonia.Controls.Documents.Run(s.Text[edges[i]..edges[i + 1]]);
                if (s.Kind != Tok.Text) run.Foreground = Ui.Tk(Syntax.ThemeToken(s.Kind));
                if (s.Kind == Tok.Comment) run.FontStyle = FontStyle.Italic;
                if (marks.Any(m => from >= m.Start && from < m.End)) run.Background = mark;
                target.Add(run);
            }
            at += s.Text.Length;
        }
    }

    private static Control Glyph(string text, IBrush brush, string tip, Action<Control>? click, double width = 19)
    {
        var b = new Border
        {
            Width = width, Height = RowHeight - 2, CornerRadius = new CornerRadius(3), Background = Brushes.Transparent,
            Cursor = click is null ? null : new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, Foreground = brush, FontSize = 12.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = click is null ? 0.7 : 1 },
        };
        ToolTip.SetTip(b, tip);
        if (click is not null)
        {
            b.PointerEntered += (_, _) => b.Background = Ui.Tk("lineSoft");
            b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
            b.PointerPressed += (_, e) => { if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) { e.Handled = true; click(b); } };
        }
        return b;
    }

    private string ResultNumber(MRow r) => r.Line && r.Chunk < _resultStart.Length ? (_resultStart[r.Chunk] + r.Offset + 1).ToString() : "";

    private void OpenChunkMenu(MergeChunk chunk, int index, Control anchor)
    {
        var menu = new ContextMenu();
        if (chunk.Has(false)) menu.Items.Add(Ui.Item(chunk.Kind == MergeChunkKind.Same ? "Take the Change (both sides made it)" : "Take Yours", () => Act(index, d => { d.Reject(chunk, true); d.Apply(chunk, false); })));
        if (chunk.Has(true) && chunk.Kind != MergeChunkKind.Same) menu.Items.Add(Ui.Item("Take Theirs", () => Act(index, d => { d.Reject(chunk, false); d.Apply(chunk, true); })));
        if (chunk.IsConflict)
        {
            menu.Items.Add(Ui.Item("Take Both, Yours First", () => Act(index, d => d.ApplyBoth(chunk, false))));
            menu.Items.Add(Ui.Item("Take Both, Theirs First", () => Act(index, d => d.ApplyBoth(chunk, true))));
        }
        if (chunk.Kind != MergeChunkKind.Stable)
        {
            menu.Items.Add(Ui.Item("Take Neither (keep the original lines)", () => Act(index, d => { d.Reject(chunk, false); d.Reject(chunk, true); })));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Ui.Item("Edit the Result Here…", () => EditChunkAsync(chunk, index), icon: Icons.Pencil));
        menu.Items.Add(Ui.Item(chunk.Kind == MergeChunkKind.Stable ? "Undo This Edit" : "Reset This Change", () => Act(index, d => d.Reset(chunk))));
        menu.Open(anchor);
    }

    private async Task EditChunkAsync(MergeChunk chunk, int index)
    {
        if (_doc is not { } doc) return;
        var start = chunk.Resolved ? chunk.Result().Select(l => l.Text) : chunk.Ours.Concat(chunk.Theirs);
        var box = new TextBox { AcceptsReturn = true, AcceptsTab = true, FontFamily = Ui.Mono, FontSize = 12.5, MinHeight = 180, MaxHeight = 460, Text = string.Join("\n", start) };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        await Ui.FormAsync(Ui.Owner(this), "Edit the result", Ui.Column(8,
            Ui.Text(chunk.Resolved ? "These lines replace what the result has for this change." : "Yours is followed by theirs: edit them into what the result should have here.", "dim", 12.5), box), () =>
        {
            Act(index, d => d.Edit(chunk, box.Text ?? ""));
            return Task.FromResult<string?>(null);
        }, "Use These Lines", 760);
    }

    private Button AcceptButton(bool theirs)
    {
        var who = theirs ? "Theirs" : "Yours";
        var b = new Button { Content = $"Accept {who} ▾", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 6) };
        ToolTip.SetTip(b, $"Settle conflicts with {who.ToLowerInvariant()}, or take {who.ToLowerInvariant()} for the whole file");
        var menu = new MenuFlyout();
        void All(Action<MergeDoc> change) { if (_doc is { } d) { change(d); RebuildAll(true); PushToFile(); } }
        menu.Items.Add(Ui.Item($"{who} for the Conflicts Still Open", () => All(d => d.AcceptConflicts(theirs, true))));
        menu.Items.Add(Ui.Item($"{who} for Every Conflict", () => All(d => d.AcceptConflicts(theirs, false))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item($"{who} for the Whole File (drops every change of the other side)", () => All(d => d.AcceptFile(theirs))));
        b.Flyout = menu;
        return b;
    }

    // ---- showing ----

    /// <summary>Shows a conflicted file. Decisions already made on it are kept while its three versions stay the same.</summary>
    public async Task ShowFileAsync(RepoEntry repo, RepoChange change, MergeState state, bool showRepoName)
    {
        var gen = ++_generation;
        var file = await Repos.ConflictAsync(repo, change.Path);
        if (gen != _generation) return;
        if (_shownKey != repo.Id + "\n" + change.Path) { if (_editing) EndEdit(); StopWatching(); }
        _repo = repo; _path = change.Path; _state = state;
        _title.Text = (showRepoName ? repo.Name + " · " : "") + change.Path;
        _sub.Text = (state.InProgress ? state.Title : "Conflict") + (_watcher is null ? "" : " · linked to the editor: what you save there shows up here");
        ToolTip.SetTip(_title, Path.Combine(repo.Path, change.Path));
        var rebase = state.Op == MergeOp.Rebase;
        _panes[0].Label.Text = $"YOURS · {state.OursLabel}";
        _panes[1].Label.Text = "RESULT";
        _panes[2].Label.Text = $"THEIRS · {state.TheirsLabel}";
        ToolTip.SetTip(_panes[0].Label, rebase ? "The branch being rebased onto. During a rebase git calls this side \"ours\"." : "The branch you are on, before the merge");
        ToolTip.SetTip(_panes[2].Label, rebase ? "Your commit being replayed. During a rebase git calls this side \"theirs\"." : "What is being brought in");
        ToolTip.SetTip(_panes[1].Label, "What the file will be once you mark it resolved");
        _save.IsVisible = _editFile.IsVisible = _expand.IsVisible = true;

        if (!file.Mergeable) { ShowWholeFileChoice(repo, file, state); return; }

        var key = repo.Id + "\n" + change.Path;
        var signature = HashCode.Combine(file.Base, file.Ours, file.Theirs);
        if (_shownKey == key && _shownSignature == signature && _doc is not null && _paneGrid.IsVisible) { UpdateStatus(); return; }
        if (!Sessions.TryGetValue(key, out var session) || session.Signature != signature)
            Sessions[key] = session = (signature, MergeDoc.Create(file.Base, file.Ours!, file.Theirs!));
        _doc = session.Doc; _shownKey = key; _shownSignature = signature;

        var lang = Syntax.LanguageOf(change.Path);
        _colorizer.Language = lang;
        _hlBase = Syntax.Highlight(string.Join('\n', _doc.BaseLines), lang);
        _hlOurs = Syntax.Highlight(string.Join('\n', _doc.OursLines), lang);
        _hlTheirs = Syntax.Highlight(string.Join('\n', _doc.TheirsLines), lang);
        _simple.IsVisible = false; _paneGrid.IsVisible = true; _toolBar.IsVisible = true;
        _current = -1;
        RebuildAll(false);
        // Start at the first conflict, or at the first change when nothing actually conflicts.
        Dispatcher.UIThread.Post(() => { if (gen == _generation) Jump(1); }, DispatcherPriority.Loaded);
    }

    /// <summary>A file that cannot be merged line by line: one side deleted it, or it is not text. One side has to win.</summary>
    private void ShowWholeFileChoice(RepoEntry repo, ConflictFile file, MergeState state)
    {
        if (_editing) EndEdit();
        StopWatching();
        _doc = null; _shownKey = null;
        _paneGrid.IsVisible = false; _toolBar.IsVisible = false; _simple.IsVisible = true;
        _save.IsVisible = false;
        string Side(string? text, string label) => text is null ? $"{label} deleted this file" : file.Binary ? $"{label} has a version that is not text (or is too large to merge here)" : $"{label} changed this file";
        var what = $"{Side(file.Ours, $"Yours ({state.OursLabel})")}; {Side(file.Theirs, $"theirs ({state.TheirsLabel})").ToLowerInvariant()}.";
        Button Choice(bool theirs) => Ui.Apply(Ui.TextButton((theirs ? file.Theirs : file.Ours) is null ? $"Delete the File ({(theirs ? "theirs" : "yours")})" : $"Keep {(theirs ? "Theirs" : "Yours")}",
            () => _ = Ui.TryAsync(this, async () => { await Repos.ResolveWithSideAsync(repo, file.Path, theirs); Done(repo, file.Path); }, "Could not resolve the file")), b => b.HorizontalAlignment = HorizontalAlignment.Left);
        SetStatus("Conflict: one side has to win", false);
        _simple.Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 12, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                Ui.Text("This file cannot be merged line by line", "h2"),
                Ui.Text(what, "dim"),
                Ui.Text("Choose which side's version to keep. The file is staged with that choice.", "dim"),
                Ui.Row(8, Choice(false), Choice(true)),
            },
        };
    }

    /// <summary>Shows where a repository's merge stands: what is being merged, what is left, and how to finish or give up.</summary>
    public void ShowSummary(RepoEntry repo, MergeState state, bool showRepoName)
    {
        _generation++;
        if (_editing) EndEdit();
        StopWatching();
        _repo = repo; _state = state; _doc = null; _shownKey = null;
        _paneGrid.IsVisible = false; _toolBar.IsVisible = false; _simple.IsVisible = true;
        _save.IsVisible = _editFile.IsVisible = _expand.IsVisible = false;
        _fileNav.IsVisible = false;
        // With nothing to compare side by side, the list of files comes back.
        Collapse();
        _title.Text = (showRepoName ? repo.Name + " · " : "") + state.Title;
        var left = state.Conflicts.Count;
        var done = left == 0;
        _sub.Text = done ? $"Ready to finish the {state.Verb}" : $"{left} of its files still to resolve";
        ToolTip.SetTip(_title, repo.Path);
        SetStatus(done ? "✓ All conflicts resolved" : $"⚠ {left} file{(left == 1 ? "" : "s")} with conflicts", done);
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 12, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Ui.Row(10,
            new PathIcon { Data = done ? Icons.CheckCircle : Icons.AlertCircle, Width = 26, Height = 26, Foreground = Ui.Tk(done ? "ok" : "errorHi") },
            Ui.Text(done ? "All conflicts are resolved" : $"{left} file{(left == 1 ? " still has" : "s still have")} conflicts", "h2")));
        panel.Children.Add(Ui.Text(state.Title + ".", "dim"));
        if (done)
        {
            panel.Children.Add(Ui.Text(state.InProgress
                ? $"Nothing is left to resolve. Finish the {state.Verb} to commit the result{(state.Op == MergeOp.Rebase ? " and carry on with the remaining commits" : "")}."
                : "Nothing is left to resolve.", "dim"));
        }
        else
        {
            panel.Children.Add(Ui.Text("Resolve each file under Conflicts in the list. A file leaves that list when you mark it resolved.", "dim"));
            var files = new StackPanel { Spacing = 2 };
            foreach (var f in state.Conflicts.Take(30)) files.Children.Add(new TextBlock { Text = f, FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.Tk("errorHi") });
            if (state.Conflicts.Count > 30) files.Children.Add(Ui.Text($"… and {state.Conflicts.Count - 30} more", "dim", 12));
            panel.Children.Add(files);
        }
        var buttons = Ui.Row(8);
        if (!done) buttons.Children.Add(Ui.Apply(Ui.TextButton("Resolve Next Conflict", () => ResolveNext?.Invoke(repo)), b => b.Classes.Add("accent")));
        if (state.InProgress)
        {
            var name = char.ToUpperInvariant(state.Verb[0]) + state.Verb[1..];
            var go = Ui.TextButton($"Continue {name}", () => { if (ContinueMerge is { } c) _ = Ui.TryAsync(this, () => c(repo), $"Could not continue the {state.Verb}"); },
                done ? $"Commit the result and finish the {state.Verb}" : "Resolve the conflicts first");
            go.IsEnabled = done;
            if (done) go.Classes.Add("accent");
            buttons.Children.Add(go);
            buttons.Children.Add(Ui.TextButton($"Abort {name}…", () => { if (AbortMerge is { } a) _ = Ui.TryAsync(this, () => a(repo), $"Could not abort the {state.Verb}"); },
                $"Give up the {state.Verb} and put everything back the way it was"));
        }
        panel.Children.Add(buttons);
        _simple.Content = panel;
    }

    // ---- rows ----

    private static IReadOnlyList<Span>? Spans(List<List<Span>> hl, int i) => i >= 0 && i < hl.Count && hl[i].Count > 0 ? hl[i] : null;

    private (List<MRow> Ours, List<MRow> Result, List<MRow> Theirs) RowsFor(int index)
    {
        var c = _doc!.Chunks[index];
        List<MRow> ours = [], result = [], theirs = [];
        if (!c.IsChange)
        {
            for (var j = 0; j < c.Ours.Count; j++)
            {
                var spans = Spans(_hlOurs, c.OursStart + j);
                ours.Add(new MRow { Chunk = index, Offset = j, Line = true, Number = c.OursStart + j + 1, Text = c.Ours[j], Spans = spans });
                result.Add(new MRow { Chunk = index, Offset = j, Line = true, Text = c.Ours[j], Spans = spans });
                theirs.Add(new MRow { Chunk = index, Offset = j, Line = true, Number = c.TheirsStart + j + 1, Text = c.Theirs[j], Spans = Spans(_hlTheirs, c.TheirsStart + j) });
            }
            return (ours, result, theirs);
        }

        var lines = c.Resolved ? c.Result().ToList() : null;
        var custom = lines is not null && c.Custom is not null ? Syntax.Highlight(string.Join('\n', c.Custom), Syntax.LanguageOf(_path)) : null;
        var height = Math.Max(1, Math.Max(Math.Max(c.Ours.Count, c.Theirs.Count), lines?.Count ?? 1));
        for (var j = 0; j < height; j++)
        {
            bool first = j == 0, last = j == height - 1;
            MRow Side(bool isTheirs)
            {
                var own = isTheirs ? c.Theirs : c.Ours;
                var start = isTheirs ? c.TheirsStart : c.OursStart;
                if (j >= own.Count) return new MRow { Chunk = index, Offset = j, Look = Look.Filler, First = first, Last = last, Change = true };
                var look = !c.Has(isTheirs) ? Look.Plain : c.Kind switch
                {
                    MergeChunkKind.Same => Look.Same, MergeChunkKind.Conflict when !c.Resolved => Look.Conflict, _ => isTheirs ? Look.Theirs : Look.Ours,
                };
                var rejected = c.Has(isTheirs) && c.State(isTheirs) == SideState.Rejected;
                // A side that was left out, or that the hand-typed result replaced, is shown struck through.
                var dim = rejected || (c.Custom is not null && c.Has(isTheirs));
                return new MRow
                {
                    Chunk = index, Offset = j, Line = true, Number = start + j + 1, Text = own[j], Spans = Spans(isTheirs ? _hlTheirs : _hlOurs, start + j),
                    Look = dim ? Look.Filler : look, Dim = dim, First = first, Last = last, Change = true,
                    Marks = dim ? null : (isTheirs ? _wordTheirs : _wordOurs).GetValueOrDefault(start + j),
                };
            }
            ours.Add(Side(false));
            theirs.Add(Side(true));

            if (lines is null)
                result.Add(new MRow { Chunk = index, Offset = j, Look = Look.Open, First = first, Last = last, Change = true, Text = first ? "Unresolved conflict" : "" });
            else if (j >= lines.Count)
                result.Add(new MRow { Chunk = index, Offset = j, Look = Look.Filler, First = first, Last = last, Change = true });
            else
            {
                var l = lines[j];
                result.Add(new MRow
                {
                    Chunk = index, Offset = j, Line = true, Text = l.Text, First = first, Last = last, Change = true,
                    Marks = l.Source switch { MergeSource.Ours => _wordOurs.GetValueOrDefault(l.Index), MergeSource.Theirs => _wordTheirs.GetValueOrDefault(l.Index), _ => null },
                    Spans = l.Source switch { MergeSource.Ours => Spans(_hlOurs, l.Index), MergeSource.Theirs => Spans(_hlTheirs, l.Index), MergeSource.Base => Spans(_hlBase, l.Index), _ => custom is null ? null : Spans(custom, l.Index) },
                    Look = l.Source switch
                    {
                        MergeSource.Custom => Look.Custom, MergeSource.Base => Look.Plain,
                        _ when c.Kind == MergeChunkKind.Same => Look.Same, MergeSource.Ours => Look.Ours, _ => Look.Theirs,
                    },
                });
            }
        }
        return (ours, result, theirs);
    }

    /// <summary>Where each chunk's lines start in the result, so result rows can be numbered as they are drawn.</summary>
    private void Renumber()
    {
        var doc = _doc!;
        _resultStart = new int[doc.Chunks.Count];
        var n = 0;
        for (var i = 0; i < doc.Chunks.Count; i++)
        {
            _resultStart[i] = n;
            var c = doc.Chunks[i];
            n += !c.IsChange ? c.Ours.Count : c.Resolved ? c.Result().Count() : 0;
        }
    }

    private void RebuildAll(bool keepScroll)
    {
        if (_doc is not { } doc) return;
        var theirsToken = TheirsToken();
        _bOurs = Tint("accent", 0.17); _bTheirs = Tint(theirsToken, 0.19); _bSame = Tint("ok", 0.15); _bConflict = Tint("error", 0.22);
        _bCustom = Tint("warn", 0.17); _bFiller = Tint("line", 0.38);
        // On a light background the same tints are far heavier, and the text on them has to stay readable.
        var paper = Ui.Color("bgEditor");
        var light = (paper.R * 299 + paper.G * 587 + paper.B * 114) / 1000 > 140;
        _mOurs = Tint("accent", light ? 0.2 : 0.42); _mTheirs = Tint(theirsToken, light ? 0.22 : 0.45); _mSame = Tint("ok", light ? 0.22 : 0.4); _mConflict = Tint("error", light ? 0.3 : 0.5);
        if (light) { _bOurs = Tint("accent", 0.11); _bTheirs = Tint(theirsToken, 0.12); }
        FindWords();
        var swatches = _paneGrid.GetLogicalDescendants().OfType<Border>().Where(b => b.Tag as string == "swatch").ToList();
        if (swatches.Count == 3) { swatches[0].Background = Ui.Tk("accent"); swatches[1].Background = Ui.Tk("ok"); swatches[2].Background = Ui.Tk(theirsToken); }

        var y = _panes[1].Scroll.Offset.Y;
        Renumber();
        _heights = new int[doc.Chunks.Count];
        List<MRow> ours = [], result = [], theirs = [];
        for (var i = 0; i < doc.Chunks.Count; i++)
        {
            var (o, r, t) = RowsFor(i);
            _heights[i] = o.Count;
            ours.AddRange(o); result.AddRange(r); theirs.AddRange(t);
        }
        var lists = new[] { ours, result, theirs };
        for (var p = 0; p < 3; p++)
        {
            _panes[p].Rows = new AvaloniaList<MRow>(lists[p]);
            _panes[p].Items.ItemsSource = _panes[p].Rows;
        }
        UpdateStatus();
        if (keepScroll) Dispatcher.UIThread.Post(() => SetOffset(y), DispatcherPriority.Loaded);
        else SetOffset(0);
    }

    /// <summary>
    /// Works out, for every changed line of either side, which words differ from the line it replaced: the common
    /// ancestor's, or the other side's where both sides added lines that have no ancestor.
    /// </summary>
    private void FindWords()
    {
        _wordOurs = []; _wordTheirs = [];
        if (_doc is not { } doc) return;
        foreach (var c in doc.Chunks.Where(c => c.Kind != MergeChunkKind.Stable))
        {
            void Side(bool theirs)
            {
                if (!c.Has(theirs)) return;
                var own = theirs ? c.Theirs : c.Ours;
                var against = c.Base.Count > 0 ? c.Base : c.IsConflict ? (theirs ? c.Ours : c.Theirs) : [];
                var found = MergeDoc.WordChanges(against, own);
                for (var i = 0; i < found.Length; i++)
                    if (found[i] is { } marks) (theirs ? _wordTheirs : _wordOurs)[(theirs ? c.TheirsStart : c.OursStart) + i] = marks;
            }
            Side(false); Side(true);
        }
    }

    // ---- editing the result freely ----

    /// <summary>Swaps the Result pane for an editor holding the result as text; conflicts still open are between git's markers.</summary>
    private void BeginEdit()
    {
        if (_doc is not { } doc || _editing) return;
        var top = _panes[1].Scroll.Offset.Y;
        _editing = true;
        _editor.Text = doc.EditableText(_state.OursLabel, _state.TheirsLabel);
        _editor.IsVisible = true; _panes[1].Scroll.IsVisible = false;
        _editToggle.Content = "Done Editing";
        _editToggle.Classes.Set("accent", true);
        _panes[1].Label.Text = "RESULT · editing (Esc when done)";
        foreach (var b in new[] { _up, _down, _acceptOurs, _acceptTheirs, _reset, _editFile }) b.IsEnabled = false;
        var line = EditLineAt((int)(top / RowHeight));
        RebuildAll(true);
        // The editor opens where the Result pane was, once it has laid its text out.
        DispatcherTimer.RunOnce(() =>
        {
            if (!_editing) return;
            // Put the caret where the pane was looking and let the editor bring it to the top of its view.
            var target = Math.Min(line + 1, _editor.Document.LineCount);
            _editor.TextArea.Focus();
            _editor.TextArea.Caret.Line = target;
            _editor.TextArea.Caret.Column = 1;
            var visual = _editor.TextArea.TextView.GetVisualTopByDocumentLine(target);
            var scroller = _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroller is not null) scroller.Offset = new Vector(0, visual);
        }, TimeSpan.FromMilliseconds(120));
    }

    /// <summary>How many lines of the editable text each chunk takes: an open conflict is both sides and three markers.</summary>
    private int EditLines(MergeChunk c) => c.Resolved ? c.Result().Count() : c.Ours.Count + c.Theirs.Count + 3;

    /// <summary>The (zero-based) line of the editable text that a row of the panes shows.</summary>
    private int EditLineAt(int row)
    {
        if (_doc is not { } doc) return 0;
        var line = 0;
        for (var i = 0; i < doc.Chunks.Count && i < _heights.Length; i++)
        {
            if (row < _heights[i]) return line + Math.Min(row, Math.Max(0, EditLines(doc.Chunks[i]) - 1));
            row -= _heights[i];
            line += EditLines(doc.Chunks[i]);
        }
        return line;
    }

    /// <summary>The row of the panes that shows a (zero-based) line of the editable text.</summary>
    private int RowAtEditLine(int line)
    {
        if (_doc is not { } doc) return 0;
        var row = 0;
        for (var i = 0; i < doc.Chunks.Count && i < _heights.Length; i++)
        {
            var n = EditLines(doc.Chunks[i]);
            if (line < n) return row + Math.Min(line, _heights[i] - 1);
            line -= n;
            row += _heights[i];
        }
        return row;
    }

    /// <summary>Takes what was typed back into the merge: chunks that were not touched keep their decisions.</summary>
    private void EndEdit()
    {
        if (!_editing) return;
        _editing = false;
        var height = _editor.TextArea.TextView.DefaultLineHeight;
        var line = height > 0 ? (int)(_editor.VerticalOffset / height) : 0;
        _doc?.ApplyEdited(_editor.Text ?? "", _state.OursLabel, _state.TheirsLabel);
        _editor.IsVisible = false; _panes[1].Scroll.IsVisible = true;
        _editToggle.Content = "Edit Result";
        _editToggle.Classes.Set("accent", false);
        _panes[1].Label.Text = "RESULT";
        foreach (var b in new[] { _acceptOurs, _acceptTheirs, _reset, _editFile }) b.IsEnabled = true;
        _current = -1;
        RebuildAll(false);
        var top = RowAtEditLine(line) * RowHeight;
        Dispatcher.UIThread.Post(() => SetOffset(top), DispatcherPriority.Loaded);
        PushToFile();
    }

    private string? FullPath => _repo is { } r ? Path.Combine(r.Path, _path) : null;

    /// <summary>
    /// Opens the result in the configured editor and links the two: the file gets the result as it stands (open
    /// conflicts between markers), every save in the editor is taken back in, and every decision made here is
    /// written to the file.
    /// </summary>
    private void EditInEditor()
    {
        if (_doc is not { } doc || _repo is not { } repo || FullPath is not { } full) return;
        if (_editing) EndEdit();
        File.WriteAllText(full, doc.EditableText(_state.OursLabel, _state.TheirsLabel), new System.Text.UTF8Encoding(false));
        StopWatching();
        _watchedFile = full;
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime };
        void Changed(object? _, FileSystemEventArgs __) => Dispatcher.UIThread.Post(() => { _watchTimer.Stop(); _watchTimer.Start(); });
        _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Renamed += (o, e) => Changed(o, e);
        _watcher.EnableRaisingEvents = true;
        _sub.Text = (_state.InProgress ? _state.Title : "Conflict") + " · linked to the editor: what you save there shows up here";
        Ui.OpenInEditor(_path, repo.Path);
    }

    private void StopWatching()
    {
        _watchTimer.Stop();
        _watcher?.Dispose();
        _watcher = null; _watchedFile = null;
    }

    /// <summary>The editor saved the file: take its text in. Our own writes come back unchanged and are ignored.</summary>
    private void ReadFromFile()
    {
        if (_watcher is null || _watchedFile is not { } file || _doc is not { } doc || _editing) return;
        string text;
        try { if (!File.Exists(file)) return; text = File.ReadAllText(file); }
        catch (IOException) { _watchTimer.Start(); return; }   // still being written: look again shortly
        if (!doc.ApplyEdited(text, _state.OursLabel, _state.TheirsLabel)) return;
        _current = -1;
        RebuildAll(true);
        Ui.Toast(this, "Result updated from the editor", doc.Unresolved == 0 ? "No conflict is left open." : $"{doc.Unresolved} conflict{(doc.Unresolved == 1 ? "" : "s")} still open.");
    }

    /// <summary>While linked to an editor, a decision made here goes to the file, so the editor is not left with an old copy.</summary>
    private void PushToFile()
    {
        if (_watcher is null || _watchedFile is not { } file || _doc is not { } doc) return;
        try
        {
            var text = doc.EditableText(_state.OursLabel, _state.TheirsLabel);
            if (!File.Exists(file) || File.ReadAllText(file) != text) File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
        }
        catch (IOException ex) { Core.Logging.Log.Debug("merge", "Could not update the file for the editor: " + ex.Message); }
    }

    /// <summary>Redraws one chunk in all three panes after a decision about it, leaving everything else where it is.</summary>
    private void RefreshChunk(int index)
    {
        if (_doc is null || index < 0 || index >= _heights.Length) return;
        Renumber();
        var start = 0;
        for (var i = 0; i < index; i++) start += _heights[i];
        var (o, r, t) = RowsFor(index);
        var fresh = new[] { o, r, t };
        var old = _heights[index];
        for (var p = 0; p < 3; p++)
        {
            var rows = _panes[p].Rows;
            var shared = Math.Min(old, fresh[p].Count);
            for (var j = 0; j < shared; j++) rows[start + j] = fresh[p][j];
            if (fresh[p].Count > old) rows.InsertRange(start + old, fresh[p].Skip(old));
            else if (fresh[p].Count < old) rows.RemoveRange(start + fresh[p].Count, old - fresh[p].Count);
        }
        _heights[index] = o.Count;
        // Lines below moved up or down in the result: the numbers on screen follow.
        foreach (var container in _panes[1].Items.GetRealizedContainers())
            if (container.DataContext is MRow row && row.Chunk != index
                && container.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Tag as string == "number") is { } number)
                number.Text = ResultNumber(row);
        UpdateStatus();
    }

    private void Act(int index, Action<MergeDoc> change)
    {
        if (_doc is not { } doc) return;
        change(doc);
        var previous = _current;
        _current = index;
        if (previous != index && previous >= 0) RefreshChunk(previous);
        RefreshChunk(index);
        PushToFile();
    }

    // ---- moving around ----

    /// <summary>The chunks the arrows stop at: the conflicts, or every change in a file that has no real conflict.</summary>
    private List<int> Stops()
    {
        if (_doc is not { } doc) return [];
        var conflicts = Enumerable.Range(0, doc.Chunks.Count).Where(i => doc.Chunks[i].IsConflict).ToList();
        return conflicts.Count > 0 ? conflicts : Enumerable.Range(0, doc.Chunks.Count).Where(i => doc.Chunks[i].IsChange).ToList();
    }

    private void Jump(int step)
    {
        var stops = Stops();
        if (stops.Count == 0) return;
        int target;
        if (step > 0) { target = stops.FirstOrDefault(i => i > _current, -1); if (target < 0) target = stops[0]; }
        else { target = stops.LastOrDefault(i => i < _current, -1); if (target < 0 || _current < 0) target = stops[^1]; }
        SetCurrent(target, true);
    }

    private void SetCurrent(int index, bool scroll)
    {
        var previous = _current;
        _current = index;
        if (previous >= 0 && previous != index) RefreshChunk(previous);
        RefreshChunk(index);
        if (!scroll) return;
        var start = 0;
        for (var i = 0; i < index; i++) start += _heights[i];
        var view = _panes[1].Scroll.Viewport.Height;
        SetOffset(start * RowHeight - Math.Max(RowHeight * 2, view / 4));
    }

    // ---- status ----

    private void SetStatus(string text, bool good)
    {
        _statusText.Text = text;
        _statusText.Foreground = Ui.Tk(good ? "ok" : "errorHi");
        _status.Background = Tint(good ? "ok" : "error", 0.16);
        _status.BorderBrush = Ui.Tk(good ? "ok" : "error");
        _status.BorderThickness = new Thickness(1);
    }

    private void UpdateStatus()
    {
        if (_doc is not { } doc) return;
        int total = doc.Conflicts, open = doc.Unresolved;
        SetStatus(open > 0 ? $"⚠ {open} of {total} conflict{(total == 1 ? "" : "s")} left" : total == 0 ? "✓ No conflicting lines" : $"✓ All {total} conflict{(total == 1 ? "" : "s")} resolved", open == 0);
        _save.IsEnabled = open == 0 || _editing;
        _up.IsEnabled = _down.IsEnabled = !_editing;
        _save.Classes.Set("accent", open == 0);
        ToolTip.SetTip(_save, open == 0 ? "Write the result to the file and stage it" : $"Settle the {open} conflict{(open == 1 ? "" : "s")} still open first");
        var stops = Stops();
        var at = stops.IndexOf(_current);
        var noun = total > 0 ? "Conflict" : "Change";
        _navLabel.Text = stops.Count == 0 ? "No changes" : at < 0 ? $"{stops.Count} {noun.ToLowerInvariant()}{(stops.Count == 1 ? "" : "s")}" : $"{noun} {at + 1} of {stops.Count}";
        _up.IsEnabled = _down.IsEnabled = stops.Count > 0 && !_editing;
    }

    private async Task SaveAsync()
    {
        if (_editing) EndEdit();
        if (_doc is not { } doc || _repo is not { } repo) return;
        if (!doc.Done) { Ui.Toast(this, "Conflicts are still open", $"{doc.Unresolved} left to settle before the file can be marked resolved."); return; }
        var path = _path;
        await Ui.TryAsync(this, async () =>
        {
            await Repos.ResolveAsync(repo, path, doc.ResultText());
            Done(repo, path);
        }, "Could not save the result");
    }

    private void Done(RepoEntry repo, string path)
    {
        Sessions.Remove(repo.Id + "\n" + path);
        StopWatching();
        _shownKey = null;
        Ui.Toast(this, "Conflict resolved", path);
        Saved?.Invoke(repo, path);
    }
}
