using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avamux.Core.Wiki;

namespace Avamux.Views;

/// <summary>
/// The wiki's Tags view: every #tag with its page count (nested tags indented under their parent), and for a chosen
/// tag the pages carrying it. Both lists are virtualised and filterable, so thousands of tags or of pages per tag stay
/// quick; only the rows in view have controls.
/// </summary>
public sealed class WikiTagsPane : UserControl
{
    /// <summary>A row of the tag list. <see cref="Count"/> is 0 for a parent that only exists through nested tags.</summary>
    public sealed record TagRow(string Key, string Label, int Depth, int Count);

    private readonly Action<string, int> _open;
    private readonly ListBox _tags = new() { Background = Brushes.Transparent };
    private readonly ListBox _pages = new() { Background = Brushes.Transparent };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter tags…", Margin = new Thickness(8, 6, 8, 4) };
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontSize = 11.5, Margin = new Thickness(10, 2, 10, 6), TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _nested = new() { Content = "Include nested tags", IsChecked = true, FontSize = 12, Margin = new Thickness(8, 0, 8, 2) };
    private readonly Button _sort;
    private readonly DockPanel _pageHeader;
    private bool _byModified;
    private string? _tag;
    private bool _choosing;

    private static WikiTagIndex Index => App.Services.Tags;

    /// <param name="open">Opens a page at a line (1-based; 0 for the remembered position).</param>
    public WikiTagsPane(Action<string, int> open)
    {
        _open = open;
        _status.Foreground = Ui.Dim;
        _tags.ItemTemplate = new FuncDataTemplate<TagRow>((row, _) => row is null ? new Panel() : new DockPanel
        {
            Margin = new Thickness(row.Depth * 14, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = row.Count > 0 ? row.Count.ToString("N0") : "", Foreground = Ui.Dim, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }.Dock(Dock.Right),
                Ui.Row(5,
                    new PathIcon { Data = Icons.Tag, Width = 11, Height = 11, Foreground = Ui.Tk("accent") },
                    Ui.Apply(new TextBlock { Text = row.Label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis }, t => { if (row.Count == 0) t.Foreground = Ui.Dim; })),
            },
        });
        _pages.ItemTemplate = new FuncDataTemplate<TagHit>((hit, _) => hit is null ? new Panel() : PageRow(hit));
                _tags.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && !_choosing && _tags.SelectedItem is TagRow row) Show(row.Key);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _tags.KeyDown += (_, e) => { if (e.Key == Key.Enter && _tags.SelectedItem is TagRow row) { e.Handled = true; Show(row.Key); } };
        // Clicking a page opens it; arrow keys move without opening, and Enter opens.
        _pages.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && _pages.SelectedItem is TagHit hit) _open(hit.Page, hit.Line);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _pages.KeyDown += (_, e) => { if (e.Key == Key.Enter && _pages.SelectedItem is TagHit hit) { e.Handled = true; _open(hit.Page, hit.Line); } };
        _filter.TextChanged += (_, _) => Refresh();
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { var list = _tag is null ? _tags : _pages; if (list.ItemCount > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex); list.Focus(); e.Handled = true; } }
            else if (e.Key == Key.Escape && _tag is not null) { e.Handled = true; Back(); }
        };
        _nested.IsCheckedChanged += (_, _) => Refresh();
        _sort = Ui.TextButton("Name", () => { _byModified = !_byModified; Refresh(); }, "Sort the pages by name or by when they last changed");
        _sort.Padding = new Thickness(8, 3);
        _sort.FontSize = 11.5;
        _pageHeader = new DockPanel
        {
            Margin = new Thickness(4, 4, 8, 0), IsVisible = false,
            Children =
            {
                _sort.Dock(Dock.Right),
                Ui.IconButton(Icons.ArrowLeft, "All tags (Esc)", Back).Dock(Dock.Left),
                _heading,
            },
        };
        Content = new DockPanel
        {
            Children =
            {
                _pageHeader.Dock(Dock.Top),
                _filter.Dock(Dock.Top),
                _nested.Dock(Dock.Top),
                _status.Dock(Dock.Top),
                new Panel { Children = { _tags, _pages } },
            },
        };
        _nested.IsVisible = false;
        _pages.IsVisible = false;
        var changed = Ui.Debounced(() => { if (IsVisible) Refresh(); }, 250);
        Index.Changed += changed;
        DetachedFromVisualTree += (_, _) => Index.Changed -= changed;
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Refresh(); };
        // The index may have been read before this pane existed: show what it has now.
        AttachedToVisualTree += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>The tag whose pages are listed, or null when the tag list is shown.</summary>
    public string? CurrentTag => _tag;

    private Control PageRow(TagHit hit)
    {
        var name = WikiService.IsMarkdown(hit.Page) ? Path.GetFileNameWithoutExtension(hit.Page) : Path.GetFileName(hit.Page);
        var folder = hit.Page.Contains('/') ? hit.Page[..hit.Page.LastIndexOf('/')] : "";
        var detail = _byModified ? Ui.Ago(hit.Modified) + (folder.Length > 0 ? " · " + folder : "") : folder;
        var col = Ui.Column(1,
            new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = hit.Snippet, FontSize = 11.5, Foreground = Ui.Dim, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        if (detail.Length > 0) col.Children.Insert(1, new TextBlock { Text = detail, FontSize = 11, Foreground = Ui.Tk("faint"), TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(col, hit.Page + (hit.Line > 0 ? $" (line {hit.Line})" : ""));
        return col;
    }

    /// <summary>Lists the pages carrying a tag.</summary>
    public void Show(string tag)
    {
        _tag = WikiTags.Normalize(tag);
        _filter.Text = "";
        Refresh();
    }

    /// <summary>Back to the list of tags, with the tag just left selected.</summary>
    public void Back()
    {
        var was = _tag;
        _tag = null;
        _filter.Text = "";
        Refresh();
        if (was is not null && _tags.ItemsSource is IList<TagRow> rows && rows.FirstOrDefault(r => r.Key == was) is { } row)
        {
            _choosing = true;
            try { _tags.SelectedItem = row; _tags.ScrollIntoView(row); } finally { _choosing = false; }
        }
        _tags.Focus();
    }

    public void Refresh()
    {
        var filter = WikiTags.Normalize(_filter.Text ?? "");
        var showingPages = _tag is not null;
        _tags.IsVisible = !showingPages;
        _pages.IsVisible = showingPages;
        _pageHeader.IsVisible = showingPages;
        _nested.IsVisible = showingPages && Index.Tags().Any(t => t.Key.StartsWith(_tag + "/", StringComparison.Ordinal));
        _filter.PlaceholderText = showingPages ? "Filter pages…" : "Filter tags…";
        if (!showingPages)
        {
            var rows = Rows(Index.Tags(), filter);
            _choosing = true;
            try { _tags.ItemsSource = rows; _tags.SelectedItem = null; } finally { _choosing = false; }
            _status.Text = Index.Ready.IsCompleted
                ? rows.Count == 0 ? (filter.Length > 0 ? "No tag matches." : "No tags yet. Write #tag in a page to tag it.") : $"{rows.Count(r => r.Count > 0):N0} tags"
                : "Reading the wiki for tags…";
            return;
        }
        var hits = Index.Pages(_tag!, _nested.IsChecked == true);
        _heading.Text = "#" + (Index.Tags().FirstOrDefault(t => t.Key == _tag)?.Name ?? _tag);
        if (filter.Length > 0)
            hits = hits.Where(h => h.Page.Contains(filter, StringComparison.OrdinalIgnoreCase) || h.Snippet.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_byModified) hits = hits.OrderByDescending(h => h.Modified).ToList();
        _sort.Content = _byModified ? "Newest" : "Name";
        _pages.ItemsSource = hits;
        _status.Text = hits.Count == 1 ? "1 page" : $"{hits.Count:N0} pages";
    }

    /// <summary>Tags as rows, each nested tag under its parent (a parent without pages of its own shows dimmed).</summary>
    public static List<TagRow> Rows(IReadOnlyList<TagCount> tags, string filter = "")
    {
        var byKey = tags.ToDictionary(t => t.Key);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            if (filter.Length > 0 && !t.Key.Contains(filter, StringComparison.Ordinal)) continue;
            var parts = t.Key.Split('/');
            for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join('/', parts.Take(i)));
        }
        // Sorted by key, a parent comes right before its children ("a" < "a/b" < "a-c" would split them, so sort by parts).
        return keys.OrderBy(k => k.Replace('/', '\u0001'), StringComparer.Ordinal).Select(k =>
        {
            var depth = k.Count(c => c == '/');
            var label = depth == 0 ? k : k[(k.LastIndexOf('/') + 1)..];
            if (byKey.TryGetValue(k, out var t)) label = depth == 0 ? t.Name : t.Name[(t.Name.LastIndexOf('/') + 1)..];
            return new TagRow(k, label, depth, byKey.TryGetValue(k, out var c) ? c.Pages : 0);
        }).ToList();
    }
}
