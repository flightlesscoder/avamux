using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.Search;

namespace Avamux.Views;

/// <summary>One box that searches wiki pages, events, watches, terminals, repositories, logs and settings.</summary>
public sealed class SearchPage : UserControl, IPage
{
    private readonly TextBox _query = new() { PlaceholderText = "Search wiki pages, events, watches, terminals, repositories, logs, settings…", FontSize = 15, Padding = new Thickness(12, 9) };
    private readonly ListBox _results = new() { Classes = { "plain" } };
    private readonly TextBlock _status = new() { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, CheckBox> _sources = new();
    private readonly CheckBox _all = new() { Content = "All", FontSize = 12.5 };
    private readonly Action _searchSoon;
    private CancellationTokenSource? _running;
    private bool _syncing;

    private static UiStateStore State => App.Services.Ui;

    public SearchPage()
    {
        _searchSoon = Ui.Debounced(() => _ = SearchAsync(), 220);
        _query.TextChanged += (_, _) => _searchSoon();
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _results.ItemCount > 0) { e.Handled = true; FocusResult(0); }
            else if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _query.Text = ""; }
        };
        _results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (_results.SelectedItem as Control)?.Tag is SearchHit hit) { e.Handled = true; Open(hit); }
        };

        var sourceRow = new WrapPanel { Orientation = Orientation.Horizontal };
        _all.IsCheckedChanged += (_, _) =>
        {
            if (_syncing || _all.IsChecked is not { } on) return;
            _syncing = true;
            foreach (var box in _sources.Values) box.IsChecked = on;
            _syncing = false;
            SaveSources();
        };
        sourceRow.Children.Add(Ui.Apply(_all, c => c.Margin = new Thickness(0, 0, 18, 0)));
        foreach (var (key, label) in SearchService.Sources)
        {
            var box = new CheckBox { Content = label, FontSize = 12.5, IsChecked = !State.Data.SearchExcluded.Contains(key), Margin = new Thickness(0, 0, 14, 0) };
            box.IsCheckedChanged += (_, _) => { if (!_syncing) SaveSources(); };
            _sources[key] = box;
            sourceRow.Children.Add(box);
        }
        SyncAll();

        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("Search", "Everything avamux knows about, in one place. Click a result to open it.").Dock(Dock.Top),
                new StackPanel
                {
                    Margin = new Thickness(20, 0, 20, 10), Spacing = 8,
                    Children = { _query, new DockPanel { Children = { _status.Dock(Dock.Right), sourceRow } } },
                }.Dock(Dock.Top),
                new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = _results },
            },
        };
        ShowHint();
    }

    public void OnShown() => Dispatcher.UIThread.Post(() => { _query.Focus(); _query.SelectAll(); }, DispatcherPriority.Background);

    /// <summary>Puts a query in the box and searches (used by `avamux search --open`).</summary>
    public void Run(string query)
    {
        _query.Text = query;
        _ = SearchAsync();
    }

    private void SyncAll()
    {
        _syncing = true;
        var on = _sources.Values.Count(b => b.IsChecked == true);
        _all.IsThreeState = on > 0 && on < _sources.Count;
        _all.IsChecked = on == _sources.Count ? true : on == 0 ? false : null;
        _syncing = false;
    }

    private void SaveSources()
    {
        State.Data.SearchExcluded = _sources.Where(s => s.Value.IsChecked != true).Select(s => s.Key).ToList();
        State.SaveSoon();
        SyncAll();
        _ = SearchAsync();
    }

    private void FocusResult(int index)
    {
        var item = _results.Items.OfType<ListBoxItem>().Skip(index).FirstOrDefault(i => i.Tag is SearchHit);
        if (item is null) return;
        _results.SelectedItem = item;
        item.Focus();
    }

    private void ShowHint()
    {
        _results.Items.Clear();
        _status.Text = "";
        _results.Items.Add(new ListBoxItem
        {
            IsHitTestVisible = false, Focusable = false,
            Content = Ui.Apply(Ui.Text("Type to search. Use the boxes above to choose where to look; everything is included by default.", "dim", 12.5), t => t.Margin = new Thickness(12, 14)),
        });
    }

    private async Task SearchAsync()
    {
        _running?.Cancel();
        var query = (_query.Text ?? "").Trim();
        if (query.Length == 0) { ShowHint(); return; }
        var cts = _running = new CancellationTokenSource();
        var wanted = _sources.Where(s => s.Value.IsChecked == true).Select(s => s.Key).ToList();
        if (wanted.Count == 0) { _results.Items.Clear(); _status.Text = "No source is switched on."; return; }
        _status.Text = "Searching…";
        IReadOnlyList<SearchHit> hits;
        try { hits = await App.Services.Search.SearchAsync(query, wanted, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _status.Text = ex.GetBaseException().Message; return; }
        if (cts.IsCancellationRequested) return;

        _results.Items.Clear();
        _status.Text = hits.Count == 0 ? "Nothing found" : $"{hits.Count} result{(hits.Count == 1 ? "" : "s")}";
        foreach (var group in hits.GroupBy(h => h.Source))
        {
            var label = SearchService.Sources.FirstOrDefault(s => s.Key == group.Key).Label ?? group.Key;
            _results.Items.Add(new ListBoxItem
            {
                IsHitTestVisible = false, Focusable = false, Padding = new Thickness(12, 12, 8, 4),
                Content = Ui.Row(8, new PathIcon { Data = IconFor(group.Key), Width = 14, Height = 14, Foreground = Ui.Tk("accent") },
                    new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = group.Count().ToString(), Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }),
            });
            foreach (var hit in group)
            {
                var detail = hit.Detail + (hit.Line > 0 && hit.Source != "terminals" ? $"   line {hit.Line}" : "");
                var item = new ListBoxItem
                {
                    Tag = hit, Padding = new Thickness(34, 5, 12, 5), Cursor = new Cursor(StandardCursorType.Hand),
                    Content = Ui.Column(1, Highlighted(hit.Title, query, 13, null), Highlighted(detail, query, 12, Ui.Dim)),
                };
                item.Tapped += (_, _) => Open(hit);
                _results.Items.Add(item);
            }
        }
    }

    private static Geometry IconFor(string source) => source switch
    {
        "wiki" => Icons.BookOpenVariant, "events" => Icons.CalendarClock, "watches" => Icons.Eye, "terminals" => Icons.Console,
        "repositories" => Icons.SourceBranch, "logs" => Icons.TextBoxOutline, _ => Icons.Cog,
    };

    /// <summary>Text with every occurrence of the query marked.</summary>
    private static TextBlock Highlighted(string text, string query, double size, IBrush? foreground)
    {
        var block = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        if (foreground is not null) block.Foreground = foreground;
        text = text.ReplaceLineEndings(" ");
        var at = 0;
        while (at < text.Length)
        {
            var found = query.Length == 0 ? -1 : text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) { block.Inlines!.Add(new Run(text[at..])); break; }
            if (found > at) block.Inlines!.Add(new Run(text[at..found]));
            block.Inlines!.Add(new Run(text.Substring(found, query.Length)) { Foreground = Ui.Tk("warn"), FontWeight = FontWeight.SemiBold });
            at = found + query.Length;
        }
        return block;
    }

    private void Open(SearchHit hit)
    {
        var main = Ui.Main;
        if (main is null) return;
        Ui.Try(this, () =>
        {
            switch (hit.Source)
            {
                case "wiki":
                    main.Navigate("wiki");
                    main.Page<WikiPage>("wiki")?.Open(hit.Target, hit.Line);
                    break;
                case "events":
                    main.Navigate("events");
                    main.Page<EventsPage>("events")?.Reveal(hit.Target);
                    break;
                case "watches":
                    main.Navigate("watching");
                    main.Page<WatchingPage>("watching")?.Select(hit.Target);
                    break;
                case "terminals":
                    main.Navigate("terminals");
                    App.Services.Host.SelectTerminal(hit.Target);
                    break;
                case "repositories":
                    main.Navigate("repositories");
                    main.Page<RepositoriesPage>("repositories")?.Reveal(hit.Target, hit.Extra, hit.Line);
                    break;
                case "logs":
                    main.Navigate("logs");
                    main.Page<LogsPage>("logs")?.OpenFile(hit.Target, hit.Line > 0 ? (_query.Text ?? "").Trim() : null);
                    break;
                case "settings":
                    main.Navigate("settings");
                    main.Page<SettingsPage>("settings")?.Reveal(hit.Target);
                    break;
            }
        }, "Could not open the result");
    }
}
