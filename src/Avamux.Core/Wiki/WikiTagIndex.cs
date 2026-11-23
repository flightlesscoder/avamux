using System.Text.Json;
using Avamux.Core.Logging;

namespace Avamux.Core.Wiki;

/// <summary>A tag with how many pages carry it. <see cref="Name"/> is as first written; <see cref="Key"/> is its normalized form.</summary>
public sealed record TagCount(string Key, string Name, int Pages);

/// <summary>A page that carries a tag, with the first line it appears on.</summary>
public sealed record TagHit(string Page, string Tag, int Line, string Snippet, DateTime Modified);

/// <summary>
/// Which pages carry which #tags, for the wiki's Tags view, <c>avamux wiki tags</c> and search. Built in the background
/// from a cache in the data folder (only pages changed since are read again), then kept up to date from the wiki's
/// own writes and a file watcher, so edits made outside avamux (another editor, git) are picked up too. Queries never
/// wait for it: before the first build they answer from the cache, or with nothing.
/// </summary>
public sealed class WikiTagIndex : IDisposable
{
    /// <summary>Bumped whenever <see cref="WikiTags"/> changes what it finds, so an old cache is read again.</summary>
    private const int CacheVersion = 1;
    private const int SnippetLength = 200;

    private readonly WikiService _wiki;
    private readonly string _cacheFile;
    private readonly object _gate = new();
    private readonly Dictionary<string, PageEntry> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byTag = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileSystemWatcher? _watcher;
    private Timer? _flush, _save;
    private bool _fullPending, _dirty, _disposed;

    public WikiTagIndex(WikiService wiki, AppPaths paths)
    {
        _wiki = wiki;
        _cacheFile = paths.StateFile("wiki-tags");
        _wiki.PathChanged += path => { if (path is null) RefreshSoon(); else UpdateSoon(path); };
    }

    /// <summary>Raised (on a background thread) after tags were added to or taken from pages.</summary>
    public event Action? Changed;

    /// <summary>Completes once the first full pass over the wiki has finished.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Loads the cache, checks every page against it in the background and starts watching the folder.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                LoadCache();
                await RefreshAsync();
                Diagnostics.Timeline.Note("wiki tags indexed", $"{_pages.Count} pages");
            }
            catch (Exception ex) { Log.Warn("wiki", "Tag index: " + ex.Message); }
            finally { _ready.TrySetResult(); }
            Watch();
        });
    }

    // ---- queries ----

    /// <summary>Every tag with its page count, sorted by name.</summary>
    public List<TagCount> Tags()
    {
        lock (_gate)
            return _byTag.Where(kv => kv.Value.Count > 0)
                .Select(kv => new TagCount(kv.Key, _names.GetValueOrDefault(kv.Key, kv.Key), kv.Value.Count))
                .OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>The pages carrying a tag (and, with <paramref name="nested"/>, tags under it: <c>a/b</c> under <c>a</c>).</summary>
    public List<TagHit> Pages(string tag, bool nested = true)
    {
        var key = WikiTags.Normalize(tag);
        lock (_gate)
        {
            var keys = nested ? _byTag.Keys.Where(k => WikiTags.IsUnder(k, key)).ToList() : _byTag.ContainsKey(key) ? [key] : [];
            var pages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in keys) pages.UnionWith(_byTag[k]);
            var hits = new List<TagHit>(pages.Count);
            foreach (var page in pages)
            {
                if (!_pages.TryGetValue(page, out var entry)) continue;
                var first = entry.Tags.FirstOrDefault(t => keys.Contains(t.Key));
                if (first is null) continue;
                hits.Add(new TagHit(page, _names.GetValueOrDefault(first.Key, first.Key), first.Line, first.Snippet, new DateTime(entry.Ticks, DateTimeKind.Utc).ToLocalTime()));
            }
            hits.Sort((a, b) => string.Compare(a.Page, b.Page, StringComparison.OrdinalIgnoreCase));
            return hits;
        }
    }

    /// <summary>The tags of one page, normalized, in the order they first appear.</summary>
    public List<string> TagsOf(string page)
    {
        lock (_gate) return _pages.TryGetValue(page, out var e) ? e.Tags.Select(t => t.Key).Distinct().ToList() : [];
    }

    public int PageCount { get { lock (_gate) return _pages.Count; } }

    // ---- keeping up ----

    /// <summary>Checks every page again (cheap for pages that did not change: only their size and time are looked at).</summary>
    public async Task RefreshAsync()
    {
        await _work.WaitAsync();
        try { RefreshAll(); }
        finally { _work.Release(); }
    }

    /// <summary>Reads one page (or a folder's pages) again now. Used by the wiki's own writes and by tests.</summary>
    public async Task UpdateAsync(string path)
    {
        await _work.WaitAsync();
        try { UpdatePaths([path]); }
        finally { _work.Release(); }
    }

    private void RefreshSoon()
    {
        lock (_gate) { _fullPending = true; Flush().Change(300, Timeout.Infinite); }
    }

    private void UpdateSoon(string path)
    {
        lock (_gate) { _pending.Add(path); Flush().Change(150, Timeout.Infinite); }
    }

    private Timer Flush() => _flush ??= new Timer(_ => _ = FlushAsync());

    private async Task FlushAsync()
    {
        if (_disposed) return;
        bool full;
        List<string> paths;
        lock (_gate)
        {
            full = _fullPending;
            paths = [.. _pending];
            _fullPending = false;
            _pending.Clear();
        }
        await _work.WaitAsync();
        try
        {
            if (full) RefreshAll();
            else if (paths.Count > 0) UpdatePaths(paths);
        }
        catch (Exception ex) { Log.Warn("wiki", "Tag index update: " + ex.Message); }
        finally { _work.Release(); }
    }

    private void RefreshAll()
    {
        var root = Path.GetFullPath(_wiki.Root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var file in Walk(new DirectoryInfo(root)))
        {
            var rel = PathSafety.Relative(root, file.FullName);
            seen.Add(rel);
            changed |= Index(rel, file);
        }
        lock (_gate)
            foreach (var gone in _pages.Keys.Where(p => !seen.Contains(p)).ToList()) { Remove(gone); changed = true; }
        Finish(changed);
    }

    private void UpdatePaths(IEnumerable<string> paths)
    {
        var root = Path.GetFullPath(_wiki.Root);
        var changed = false;
        foreach (var path in paths)
        {
            string full;
            try { full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : _wiki.Resolve(path); } catch { continue; }
            if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
            var rel = PathSafety.Relative(root, full);
            if (Directory.Exists(full))
            {
                // A folder came, went or moved: its pages are checked as in a full pass, limited to it.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in Walk(new DirectoryInfo(full)))
                {
                    var r = PathSafety.Relative(root, file.FullName);
                    seen.Add(r);
                    changed |= Index(r, file);
                }
                lock (_gate)
                    foreach (var gone in _pages.Keys.Where(p => p.StartsWith(rel + "/", StringComparison.Ordinal) && !seen.Contains(p)).ToList()) { Remove(gone); changed = true; }
            }
            else if (File.Exists(full)) { if (WikiService.IsMarkdown(full)) changed |= Index(rel, new FileInfo(full)); }
            else
            {
                lock (_gate)
                {
                    // A deleted file, or a deleted or moved folder (its old name no longer exists).
                    foreach (var gone in _pages.Keys.Where(p => p == rel || p.StartsWith(rel + "/", StringComparison.Ordinal)).ToList()) { Remove(gone); changed = true; }
                }
            }
        }
        Finish(changed);
    }

    private static IEnumerable<FileInfo> Walk(DirectoryInfo dir)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = d.EnumerateFileSystemInfos().ToList(); } catch { continue; }
            foreach (var e in entries)
            {
                if (e.Name.StartsWith('.')) continue;
                if (e is DirectoryInfo sub) { if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(sub); }
                else if (e is FileInfo f && WikiService.IsMarkdown(f.Name)) yield return f;
            }
        }
    }

    /// <summary>Reads a page if it changed since it was last read; true when its tags changed.</summary>
    private bool Index(string rel, FileInfo file)
    {
        long ticks, size;
        try { ticks = file.LastWriteTimeUtc.Ticks; size = file.Length; } catch { return false; }
        lock (_gate)
            if (_pages.TryGetValue(rel, out var known) && known.Ticks == ticks && known.Size == size) return false;
        List<TagEntry> tags;
        if (size > WikiService.MaxEditableBytes) tags = [];
        else
        {
            string text;
            try { text = File.ReadAllText(file.FullName); } catch { return false; }
            var lines = text.Replace("\r\n", "\n").Split('\n');
            tags = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in WikiTags.Find(text))
            {
                var key = WikiTags.Normalize(m.Tag);
                if (!seen.Add(key)) continue;
                var line = m.Line - 1 < lines.Length ? lines[m.Line - 1].Trim() : "";
                tags.Add(new TagEntry(key, m.Tag, m.Line, line.Length > SnippetLength ? line[..SnippetLength] + "…" : line));
            }
        }
        lock (_gate)
        {
            var before = _pages.TryGetValue(rel, out var old) ? old.Tags : null;
            if (old is not null) Unlink(rel, old);
            var entry = new PageEntry(ticks, size, tags);
            _pages[rel] = entry;
            Link(rel, entry);
            _dirty = true;
            return before is null ? tags.Count > 0 : !before.SequenceEqual(tags);
        }
    }

    private void Remove(string rel)
    {
        if (!_pages.Remove(rel, out var old)) return;
        Unlink(rel, old);
        _dirty = true;
    }

    private void Link(string rel, PageEntry entry)
    {
        foreach (var t in entry.Tags)
        {
            if (!_byTag.TryGetValue(t.Key, out var set)) _byTag[t.Key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(rel);
            _names.TryAdd(t.Key, t.Name);
        }
    }

    private void Unlink(string rel, PageEntry entry)
    {
        foreach (var t in entry.Tags)
            if (_byTag.TryGetValue(t.Key, out var set) && set.Remove(rel) && set.Count == 0) { _byTag.Remove(t.Key); _names.Remove(t.Key); }
    }

    private void Finish(bool changed)
    {
        bool dirty;
        lock (_gate) dirty = _dirty;
        if (dirty) lock (_gate) (_save ??= new Timer(_ => SaveCache())).Change(2000, Timeout.Infinite);
        if (changed) Changed?.Invoke();
    }

    private void Watch()
    {
        if (_disposed) return;
        try
        {
            var w = new FileSystemWatcher(_wiki.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            void Touched(string? path)
            {
                if (path is null) return;
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || path.Contains("/.git/", StringComparison.Ordinal)) return;
                UpdateSoon(path);
            }
            w.Changed += (_, e) => Touched(e.FullPath);
            w.Created += (_, e) => Touched(e.FullPath);
            w.Deleted += (_, e) => Touched(e.FullPath);
            w.Renamed += (_, e) => { Touched(e.OldFullPath); Touched(e.FullPath); };
            w.Error += (_, _) => RefreshSoon(); // too many changes at once: check everything
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) { Log.Warn("wiki", "Tag index cannot watch the wiki folder: " + ex.Message); }
    }

    // ---- cache ----

    private sealed record TagEntry(string Key, string Name, int Line, string Snippet);
    private sealed record PageEntry(long Ticks, long Size, List<TagEntry> Tags);

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public string Root { get; set; } = "";
        public Dictionary<string, CachedPage> Pages { get; set; } = [];
    }

    private sealed class CachedPage
    {
        public long T { get; set; }
        public long S { get; set; }
        public List<string[]> Tags { get; set; } = []; // [key, name, line, snippet]
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cacheFile), JsonUtil.Compact);
            if (cache is null || cache.Version != CacheVersion || cache.Root != Path.GetFullPath(_wiki.Root)) return;
            lock (_gate)
                foreach (var (path, p) in cache.Pages)
                {
                    var tags = p.Tags.Where(t => t.Length == 4).Select(t => new TagEntry(t[0], t[1], int.TryParse(t[2], out var l) ? l : 1, t[3])).ToList();
                    var entry = new PageEntry(p.T, p.S, tags);
                    _pages[path] = entry;
                    Link(path, entry);
                }
            if (_pages.Count > 0) Changed?.Invoke();
        }
        catch (Exception ex) { Log.Warn("wiki", "Tag cache unreadable, reading the wiki again: " + ex.Message); }
    }

    private void SaveCache()
    {
        try
        {
            CacheFile cache;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                cache = new CacheFile
                {
                    Version = CacheVersion, Root = Path.GetFullPath(_wiki.Root),
                    Pages = _pages.ToDictionary(kv => kv.Key, kv => new CachedPage
                    {
                        T = kv.Value.Ticks, S = kv.Value.Size,
                        Tags = kv.Value.Tags.Select(t => new[] { t.Key, t.Name, t.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), t.Snippet }).ToList(),
                    }),
                };
            }
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, JsonUtil.Compact));
            File.Move(tmp, _cacheFile, true);
        }
        catch (Exception ex) { Log.Warn("wiki", "Could not save the tag cache: " + ex.Message); }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _flush?.Dispose();
        _save?.Dispose();
        SaveCache();
    }
}
