using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Avamux.Core.GitActivity;

public sealed record ActivityProgress(bool Running, int Done, int Total, int Errors, string Phase, IReadOnlyList<string> Current, DateTimeOffset? Started, DateTimeOffset? Finished);

/// <summary>A source as last listed: how many repositories it gave, or what went wrong.</summary>
public sealed class SourceStatus
{
    public string Id { get; set; } = "";
    public int Repositories { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? Listed { get; set; }
}

public sealed class DiscoveredRepos
{
    public List<ActivityRepo> Repos { get; set; } = [];
    public List<SourceStatus> Sources { get; set; } = [];
}

/// <summary>
/// Git Activity: the repositories of whole organisations (and folders, and single repositories), read in the
/// background into a summary of who did what, where, when, in what language and framework, and with which AI.
/// Settings are in git-activity.json; what was read is kept in git-activity/ (one file per repository, plus
/// the mirrors) so a restart shows the figures at once and refreshes only what moved.
/// </summary>
public sealed class GitActivityService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private GitActivityConfig _config;
    private DiscoveredRepos _discovered;
    private readonly Dictionary<string, RepoActivity> _activity = new(StringComparer.Ordinal);
    private bool _loaded;
    private CancellationTokenSource? _scan;
    private ActivityProgress _progress = new(false, 0, 0, 0, "idle", [], null, null);
    private FileSystemWatcher? _watcher;
    private string _lastWritten = "";
    private Timer? _auto;
    private ActivityEngine? _engine;

    public GitActivityService(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(CacheDir);
        _config = LoadConfig();
        _discovered = JsonUtil.LoadFile<DiscoveredRepos>(Path.Combine(Dir, "repos.json"));
        ScheduleAuto();
    }

    public string ConfigFile => Path.Combine(_paths.DataDir, "git-activity.json");
    public string Dir => Path.Combine(_paths.DataDir, "git-activity");
    private string CacheDir => Path.Combine(Dir, "cache");
    public string MirrorDir => _config.MirrorFolder is { Length: > 0 } m ? ActivitySources.Expand(m, _paths.HomeDir) : Path.Combine(Dir, "mirrors");

    /// <summary>The settings changed (in the app or in the file).</summary>
    public event Action? ConfigChanged;
    /// <summary>New figures are available.</summary>
    public event Action? DataChanged;
    public event Action<ActivityProgress>? ProgressChanged;

    public GitActivityConfig Config { get { lock (_gate) return _config; } }
    public ActivityProgress Progress { get { lock (_gate) return _progress; } }
    public IReadOnlyList<ActivityRepo> Repos { get { lock (_gate) return _discovered.Repos.ToList(); } }
    public IReadOnlyList<SourceStatus> SourceStatuses { get { lock (_gate) return _discovered.Sources.ToList(); } }
    public RepoActivity? ActivityOf(string key) { lock (_gate) return _activity.GetValueOrDefault(key); }
    public ActivityRepo? FindRepo(string key) { lock (_gate) return _discovered.Repos.FirstOrDefault(r => r.Key == key); }
    public ActivitySource? FindSource(string id) { lock (_gate) return _config.Sources.FirstOrDefault(s => s.Id == id); }

    // ---- settings ----

    private GitActivityConfig LoadConfig()
    {
        if (!File.Exists(ConfigFile))
        {
            // A first start: the repositories already on the Repositories page, so there is something to see.
            var fresh = new GitActivityConfig { Sources = [new ActivitySource { Id = "repos", Kind = ActivitySource.RepositoriesPage, Name = "Repositories page" }] };
            try { Write(fresh); } catch (Exception ex) { Logging.Log.Warn("activity", "Could not write git-activity.json: " + ex.Message); }
            return fresh;
        }
        try
        {
            var text = File.ReadAllText(ConfigFile);
            var c = JsonSerializer.Deserialize<GitActivityConfig>(text, JsonUtil.Options) ?? new GitActivityConfig();
            foreach (var s in c.Sources.Where(s => s.Id.Length == 0)) s.Id = NewId(c);
            return c;
        }
        catch (Exception ex)
        {
            Logging.Log.Error("activity", $"Could not read {ConfigFile}: {ex.Message}");
            ConfigError = ex.Message;
            return _config ?? new GitActivityConfig();
        }
    }

    /// <summary>Why the file could not be read the last time, or null.</summary>
    public string? ConfigError { get; private set; }

    private void Write(GitActivityConfig c)
    {
        var text = JsonSerializer.Serialize(c, JsonUtil.Options);
        _lastWritten = text;
        var tmp = ConfigFile + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, ConfigFile, true);
    }

    private static string NewId(GitActivityConfig c)
    {
        for (var i = 1; ; i++) if (c.Sources.All(s => s.Id != "s" + i)) return "s" + i;
    }

    /// <summary>Changes the settings and saves them.</summary>
    public void Update(Action<GitActivityConfig> change)
    {
        lock (_gate)
        {
            var copy = JsonSerializer.Deserialize<GitActivityConfig>(JsonSerializer.Serialize(_config, JsonUtil.Options), JsonUtil.Options)!;
            change(copy);
            foreach (var s in copy.Sources.Where(s => s.Id.Length == 0)) s.Id = NewId(copy);
            Write(copy);
            _config = copy;
            ConfigError = null;
            _engine = null;
        }
        ScheduleAuto();
        ConfigChanged?.Invoke();
        DataChanged?.Invoke();
    }

    /// <summary>Re-reads the file when it is changed by something else (an editor, a script).</summary>
    public void Watch()
    {
        try
        {
            _watcher = new FileSystemWatcher(_paths.DataDir, "git-activity.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            var pending = 0;
            void Changed()
            {
                if (Interlocked.Exchange(ref pending, 1) == 1) return;
                Task.Delay(400).ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref pending, 0);
                    try
                    {
                        var text = File.Exists(ConfigFile) ? File.ReadAllText(ConfigFile) : "";
                        if (text == _lastWritten) return;
                        _lastWritten = text;
                        ConfigError = null;
                        var c = LoadConfig();
                        lock (_gate) { _config = c; _engine = null; }
                        ScheduleAuto();
                        ConfigChanged?.Invoke();
                        DataChanged?.Invoke();
                    }
                    catch (Exception ex) { Logging.Log.Warn("activity", "Reloading git-activity.json: " + ex.Message); }
                });
            }
            _watcher.Changed += (_, _) => Changed();
            _watcher.Created += (_, _) => Changed();
            _watcher.Renamed += (_, _) => Changed();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Logging.Log.Warn("activity", "Not watching git-activity.json: " + ex.Message); }
    }

    private void ScheduleAuto()
    {
        var minutes = Config.RefreshMinutes;
        _auto?.Dispose();
        _auto = minutes <= 0 ? null : new Timer(_ => { if (!Progress.Running) _ = ScanAsync(discover: true); }, null, TimeSpan.FromMinutes(minutes), TimeSpan.FromMinutes(minutes));
    }

    // ---- what was read ----

    private string CacheFile(string key) => Path.Combine(CacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant() + ".json");

    /// <summary>Loads what was read before (once). Call off the UI thread.</summary>
    public void EnsureLoaded()
    {
        lock (_gate) if (_loaded) return;
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string s) { if (pool.TryGetValue(s, out var hit)) return hit; pool[s] = s; return s; }
        var loaded = new Dictionary<string, RepoActivity>(StringComparer.Ordinal);
        foreach (var repo in Repos)
        {
            var file = CacheFile(repo.Key);
            if (!File.Exists(file)) continue;
            try
            {
                using var stream = File.OpenRead(file);
                var a = JsonSerializer.Deserialize<RepoActivity>(stream, JsonUtil.Options);
                if (a is null) continue;
                foreach (var c in a.Commits)
                {
                    c.RepoKey = repo.Key;
                    c.AuthorName = Intern(c.AuthorName); c.AuthorEmail = Intern(c.AuthorEmail); c.PersonName = Intern(c.PersonName); c.PersonEmail = Intern(c.PersonEmail);
                    c.Branch = Intern(c.Branch);
                    foreach (var p in c.Parts) { p.Key = Intern(p.Key); p.Frameworks = Intern(p.Frameworks); }
                }
                loaded[repo.Key] = a;
            }
            catch (Exception ex) { Logging.Log.Warn("activity", $"Could not read what was saved for {repo.Name}: {ex.Message}"); }
        }
        lock (_gate)
        {
            if (_loaded) return;
            foreach (var (k, v) in loaded) _activity[k] = v;
            _loaded = true;
            _engine = null;
        }
        DataChanged?.Invoke();
    }

    /// <summary>The figures as they stand: every commit of every enabled repository.</summary>
    public ActivityEngine Engine()
    {
        lock (_gate)
        {
            if (_engine is not null) return _engine;
            var disabled = _config.DisabledRepositories.ToHashSet(StringComparer.Ordinal);
            var repos = _discovered.Repos.Where(r => !disabled.Contains(r.Key)).ToDictionary(r => r.Key);
            var commits = _activity.Where(kv => repos.ContainsKey(kv.Key)).SelectMany(kv => kv.Value.Commits).ToList();
            var sources = _config.Sources.ToDictionary(s => s.Id, s => s.Title);
            return _engine = new ActivityEngine(commits, repos, _config, id => sources.GetValueOrDefault(id, id));
        }
    }

    /// <summary>The start of what is read: <see cref="DateTimeOffset.MinValue"/> for all of history.</summary>
    public DateTimeOffset Since => SinceOf(Config);

    public static DateTimeOffset SinceOf(GitActivityConfig c)
    {
        var from = c.ReadFrom.Trim();
        if (from.Equals("all", StringComparison.OrdinalIgnoreCase)) return DateTimeOffset.MinValue;
        if (DateTime.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return new DateTimeOffset(d.Date);
        return new DateTimeOffset(DateTime.Today.AddDays(-Math.Clamp(c.Days, 1, 36600)));
    }

    /// <summary>
    /// Makes sure what is read starts no later than <paramref name="from"/> (null: the beginning), reading
    /// further back if it does not. Returns true when it had to.
    /// </summary>
    public bool ReadBackTo(DateTimeOffset? from)
    {
        var since = Since;
        if (ActivityScanner.AllTime(since) || from is { } f && f >= since) return false;
        Update(c => c.ReadFrom = from is { } d ? d.ToString("yyyy-MM-dd") : "all");
        _ = ScanAsync(discover: false);
        return true;
    }

    // ---- reading ----

    /// <summary>Lists the repositories of every enabled source (or of one).</summary>
    public async Task DiscoverAsync(CancellationToken ct = default, string? onlySource = null)
    {
        var config = Config;
        var statuses = SourceStatuses.ToDictionary(s => s.Id);
        var previous = Repos.GroupBy(r => r.SourceId).ToDictionary(g => g.Key, g => g.ToList());
        var bySource = new Dictionary<string, List<ActivityRepo>>();
        foreach (var source in config.Sources)
        {
            if (!source.Enabled || onlySource is not null && source.Id != onlySource) { bySource[source.Id] = source.Enabled ? previous.GetValueOrDefault(source.Id) ?? [] : []; continue; }
            var status = new SourceStatus { Id = source.Id, Listed = DateTimeOffset.Now };
            try
            {
                var found = await ActivitySources.For(source.Kind).DiscoverAsync(source, ct);
                bySource[source.Id] = found;
                status.Repositories = found.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.Error = ex.GetBaseException().Message;
                // Keep what it listed last time rather than losing every figure on a network hiccup.
                bySource[source.Id] = previous.GetValueOrDefault(source.Id) ?? [];
                status.Repositories = bySource[source.Id].Count;
                Logging.Log.Warn("activity", $"Listing {source.Title}: {status.Error}");
            }
            statuses[source.Id] = status;
        }
        var all = ReadOnce(config.Sources.SelectMany(s => bySource.GetValueOrDefault(s.Id) ?? []));
        lock (_gate)
        {
            _discovered = new DiscoveredRepos { Repos = all, Sources = config.Sources.Select(s => statuses.GetValueOrDefault(s.Id) ?? new SourceStatus { Id = s.Id }).ToList() };
            JsonUtil.SaveFile(Path.Combine(Dir, "repos.json"), _discovered);
            _engine = null;
        }
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// The same repository from two sources (a clone in a folder and the organisation's copy) is read once: the first
    /// one listed, matched by its web address or its key. A repository with no remote has its key as its identity.
    /// </summary>
    public static List<ActivityRepo> ReadOnce(IEnumerable<ActivityRepo> repos)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<ActivityRepo>();
        foreach (var r in repos)
        {
            if (seen.Contains(r.Identity) || seen.Contains(r.Key)) continue;
            seen.Add(r.Identity);
            seen.Add(r.Key);
            all.Add(r);
        }
        return all;
    }

    public void Cancel() { lock (_gate) _scan?.Cancel(); }

    private void Report(ActivityProgress p)
    {
        lock (_gate) _progress = p;
        ProgressChanged?.Invoke(p);
    }

    /// <summary>Lists the sources (when asked) and brings every enabled repository, or the given ones, up to date.</summary>
    public async Task ScanAsync(bool discover = true, IReadOnlyCollection<string>? only = null, bool force = false)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_scan is not null) return;
            _scan = cts = new CancellationTokenSource();
        }
        var started = DateTimeOffset.Now;
        var current = new List<string>();
        int done = 0, errors = 0, total = 0;
        void Tell(string phase, bool running = true) { lock (current) Report(new ActivityProgress(running, done, total, errors, phase, current.ToList(), started, running ? null : DateTimeOffset.Now)); }
        try
        {
            await Task.Run(EnsureLoaded);
            if (discover)
            {
                Tell("listing repositories");
                await DiscoverAsync(cts.Token);
            }
            var config = Config;
            var disabled = config.DisabledRepositories.ToHashSet(StringComparer.Ordinal);
            var repos = Repos.Where(r => !disabled.Contains(r.Key) && (only is null || only.Contains(r.Key))).ToList();
            // Never read before, first; then the ones read longest ago.
            repos = repos.OrderBy(r => ActivityOf(r.Key)?.ScannedAt ?? DateTimeOffset.MinValue).ToList();
            total = repos.Count;
            var since = Since;
            var scanner = new ActivityScanner(config, MirrorDir);
            Directory.CreateDirectory(MirrorDir);
            Tell("reading");
            using var gate = new SemaphoreSlim(Math.Clamp(config.Parallel, 1, 32));
            var lastPublish = DateTime.UtcNow;
            var tasks = repos.Select(async repo =>
            {
                await gate.WaitAsync(cts.Token);
                lock (current) current.Add(repo.Name);
                Tell("reading");
                try
                {
                    var source = FindSource(repo.SourceId);
                    var env = source is null ? new Dictionary<string, string>() : await ActivitySources.For(source.Kind).GitEnvAsync(source, repo, cts.Token);
                    if (source?.Fetch == true) env["AVAMUX_FETCH"] = "1";
                    var previous = force ? null : ActivityOf(repo.Key);
                    RepoActivity result;
                    try { result = await scanner.ScanAsync(repo, env, since, previous, null, cts.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref errors);
                        Logging.Log.Warn("activity", $"{repo.Name}: {ex.GetBaseException().Message}");
                        // Keep the last good figures; say what went wrong.
                        result = previous ?? new RepoActivity { Key = repo.Key, Since = since };
                        result.Status = "error";
                        result.Error = ex.GetBaseException().Message;
                        result.ScannedAt = DateTimeOffset.Now;
                    }
                    if (result.Status != "error") result.Error = null;
                    await Task.Run(() => JsonUtil.SaveFile(CacheFile(repo.Key), result), CancellationToken.None);
                    lock (_gate) { _activity[repo.Key] = result; _engine = null; }
                }
                finally
                {
                    lock (current) current.Remove(repo.Name);
                    Interlocked.Increment(ref done);
                    gate.Release();
                    Tell("reading");
                    // Show figures as they come in, without redrawing for every one of hundreds of repositories.
                    if (DateTime.UtcNow - lastPublish > TimeSpan.FromSeconds(4)) { lastPublish = DateTime.UtcNow; DataChanged?.Invoke(); }
                }
            }).ToList();
            await Task.WhenAll(tasks);
            Tell("done", false);
        }
        catch (OperationCanceledException) { Tell("stopped", false); }
        catch (Exception ex)
        {
            Logging.Log.Error("activity", "Reading git activity failed", ex);
            Tell("failed: " + ex.GetBaseException().Message, false);
        }
        finally
        {
            lock (_gate) { _scan = null; _engine = null; }
            cts.Dispose();
            DataChanged?.Invoke();
        }
    }

    /// <summary>Forgets what was read and the mirrors of repositories that are no longer listed.</summary>
    public int Prune()
    {
        var keep = Repos.Select(r => CacheFile(r.Key)).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var f in Directory.GetFiles(CacheDir, "*.json")) if (!keep.Contains(f)) { File.Delete(f); removed++; }
        if (Directory.Exists(MirrorDir))
        {
            var scanner = new ActivityScanner(Config, MirrorDir);
            var mirrors = Repos.Where(r => r.LocalPath is null).Select(scanner.MirrorPath).ToHashSet(StringComparer.Ordinal);
            foreach (var d in Directory.GetDirectories(MirrorDir, "*.git")) if (!mirrors.Contains(d)) { try { Directory.Delete(d, true); removed++; } catch { } }
        }
        return removed;
    }

    public void Dispose()
    {
        Cancel();
        _auto?.Dispose();
        _watcher?.Dispose();
    }
}
