namespace Avamux.Core;

/// <summary>Where a wiki file was last scrolled to, in the editor and in the preview.</summary>
public sealed class WikiPosition
{
    public string Path { get; set; } = "";
    public double Editor { get; set; }
    public double Preview { get; set; }
    public int Caret { get; set; }
}

public sealed class UiStateData
{
    /// <summary>Whether the event dialog shows the rendered description. Applies to the dialog, not to one event.</summary>
    public bool EventDescriptionPreview { get; set; } = true;
    /// <summary>Which calendar the Events page shows: month, week or day.</summary>
    public string EventsView { get; set; } = "month";
    public bool PullRequestsMineOnly { get; set; }
    public bool PullRequestsActiveOnly { get; set; } = true;
    /// <summary>Search sources that are switched off. Empty means everything is searched.</summary>
    public List<string> SearchExcluded { get; set; } = [];
    /// <summary>Repository ids whose commits are folded away in the History tab.</summary>
    public List<string> HistoryCollapsed { get; set; } = [];
    /// <summary>Repositories folded away in the Changes tab of the Repositories page.</summary>
    public List<string> ChangesCollapsed { get; set; } = [];
    /// <summary>Whether the three panes of the merge tool scroll together.</summary>
    public bool MergeSyncScroll { get; set; } = true;
    /// <summary>Most recently used last.</summary>
    public List<WikiPosition> WikiPositions { get; set; } = [];
}

/// <summary>
/// Small things the window remembers between runs, kept in ui-state.json in the data folder rather than in the
/// settings files: they are not choices worth sharing or overriding per folder.
/// </summary>
public sealed class UiStateStore
{
    private readonly string _file;
    private readonly Func<int> _positionLimit;
    private readonly object _gate = new();
    private Timer? _flush;

    public UiStateStore(AppPaths paths, Func<int>? positionLimit = null)
    {
        _file = paths.StateFile("ui-state");
        _positionLimit = positionLimit ?? (() => 500);
        Data = JsonUtil.LoadFile<UiStateData>(_file);
    }

    public UiStateData Data { get; }

    public WikiPosition? WikiPosition(string path)
    {
        lock (_gate) return Data.WikiPositions.FirstOrDefault(p => p.Path == path);
    }

    /// <summary>Records a file's position as the most recent, dropping the oldest entries beyond the limit.</summary>
    public void RememberWiki(string path, double? editor = null, double? preview = null, int? caret = null)
    {
        lock (_gate)
        {
            var list = Data.WikiPositions;
            var p = list.FirstOrDefault(x => x.Path == path) ?? new WikiPosition { Path = path };
            list.Remove(p);
            if (editor is { } e) p.Editor = e;
            if (preview is { } v) p.Preview = v;
            if (caret is { } c) p.Caret = c;
            list.Add(p);
            var limit = Math.Max(0, _positionLimit());
            if (list.Count > limit) list.RemoveRange(0, list.Count - limit);
        }
        SaveSoon();
    }

    public void ForgetWiki(string path)
    {
        lock (_gate) Data.WikiPositions.RemoveAll(p => p.Path == path || p.Path.StartsWith(path + "/", StringComparison.Ordinal));
        SaveSoon();
    }

    public void RenameWiki(string from, string to)
    {
        lock (_gate)
            foreach (var p in Data.WikiPositions)
            {
                if (p.Path == from) p.Path = to;
                else if (p.Path.StartsWith(from + "/", StringComparison.Ordinal)) p.Path = to + p.Path[from.Length..];
            }
        SaveSoon();
    }

    /// <summary>Scrolling produces many updates; write at most about once a second.</summary>
    public void SaveSoon()
    {
        lock (_gate) (_flush ??= new Timer(_ => Save())).Change(900, Timeout.Infinite);
    }

    public void Save()
    {
        try { lock (_gate) JsonUtil.SaveFile(_file, Data); }
        catch (Exception ex) { Logging.Log.Warn("state", "Could not save ui-state.json: " + ex.Message); }
    }
}
