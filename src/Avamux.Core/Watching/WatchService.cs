using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avamux.Core.Config;
using Avamux.Core.Logging;
using Avamux.Plugins;

namespace Avamux.Core.Watching;

/// <summary>Stores watches, checks them on their interval, and records what changed.</summary>
public sealed class WatchService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly object _gate = new();
    private readonly Dictionary<string, IWatchProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = [];
    private WatchState _state;
    private Timer? _timer;

    public WatchService(AppPaths paths, ConfigService config)
    {
        _paths = paths; _config = config;
        _state = JsonUtil.LoadFile<WatchState>(paths.StateFile("watches"));
        foreach (var p in new IWatchProvider[] { new CommandWatchProvider(), new GitHubPrWatchProvider(), new AdoPrWatchProvider(), new AdoWorkItemWatchProvider(), new AdoPipelineWatchProvider(), new RepoPullRequestWatchProvider(() => _config.Settings.Repositories.PullRequestCommand) })
            _providers[p.Kind] = p;
    }

    /// <summary>Adds a watch kind from a plugin (the plugins folder is read by <see cref="Plugins.PluginService"/>).</summary>
    public void AddProvider(IWatchProvider provider)
    {
        lock (_gate) _providers[provider.Kind] = provider;
        Changed?.Invoke();
    }

    /// <summary>Takes a plugin's watch kind away again (the plugin was switched off).</summary>
    public void RemoveProvider(IWatchProvider provider)
    {
        lock (_gate) if (_providers.TryGetValue(provider.Kind, out var p) && ReferenceEquals(p, provider)) _providers.Remove(provider.Kind);
        Changed?.Invoke();
    }

    public event Action? Changed;
    /// <summary>Raised when a check produced a different result than the previous one.</summary>
    public event Action<WatchItem>? ResultChanged;
    public IAppHost Host { get; set; } = new NullAppHost();

    public IReadOnlyList<WatchItem> Items { get { lock (_gate) return _state.Items.ToList(); } }
    public IReadOnlyList<IWatchProvider> Providers { get { lock (_gate) return _providers.Values.OrderBy(p => p.Kind).ToList(); } }
    /// <summary>"plugin" is a pseudo-kind that loads a provider from the watch's own "assembly" setting.</summary>
    public IEnumerable<string> Kinds => Providers.Select(p => p.Kind).Append("plugin");

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(_config.Settings.Watching.CheckOnStartup ? 3 : 30), TimeSpan.FromSeconds(10));

    private static readonly Dictionary<string, List<IWatchProvider>> AssemblyCache = new();

    public static List<IWatchProvider> LoadProviders(string assemblyPath)
    {
        var full = Path.GetFullPath(assemblyPath);
        lock (AssemblyCache)
        {
            if (AssemblyCache.TryGetValue(full, out var cached)) return cached;
            if (!File.Exists(full)) throw new FileNotFoundException($"Plugin assembly not found: {full}");
            var list = Plugins.PluginLoader.Implementing<IWatchProvider>(Plugins.PluginLoader.Types(Plugins.PluginLoader.Load(full)))
                .Select(t => (IWatchProvider)Activator.CreateInstance(t)!).ToList();
            if (list.Count == 0) throw new InvalidOperationException($"{Path.GetFileName(full)} contains no IWatchProvider implementation.");
            Log.Info("watch", $"Loaded plugin {Path.GetFileName(full)}: {string.Join(", ", list.Select(p => p.Kind))}");
            return AssemblyCache[full] = list;
        }
    }

    private IWatchProvider Resolve(WatchItem item)
    {
        if (string.Equals(item.Kind, "plugin", StringComparison.OrdinalIgnoreCase))
        {
            var path = item.Settings.GetValueOrDefault("assembly") ?? throw new InvalidOperationException("A plugin watch needs the 'assembly' setting (path to a DLL).");
            if (!Path.IsPathRooted(path)) path = Path.Combine(_paths.PluginsDir, path);
            var list = LoadProviders(path);
            var type = item.Settings.GetValueOrDefault("type");
            return type is null ? list[0]
                : list.FirstOrDefault(p => p.Kind == type || p.GetType().Name == type || p.GetType().FullName == type)
                  ?? throw new InvalidOperationException($"No provider '{type}' in {Path.GetFileName(path)}.");
        }
        lock (_gate)
            return _providers.TryGetValue(item.Kind, out var p) ? p
                : throw new InvalidOperationException($"Unknown watch kind '{item.Kind}'. Known kinds: {string.Join(", ", Kinds)}.");
    }

    public WatchItem? Find(string idOrName)
    {
        lock (_gate)
            return _state.Items.FirstOrDefault(i => i.Id == idOrName)
                ?? _state.Items.FirstOrDefault(i => string.Equals(i.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public WatchItem Get(string idOrName) => Find(idOrName) ?? throw new KeyNotFoundException($"No watch '{idOrName}'.");

    public WatchItem Add(WatchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("A watch needs a name.");
        lock (_gate)
        {
            item.Id = NewId(_state.Items.Select(i => i.Id));
            _state.Items.Add(item);
        }
        Save();
        return item;
    }

    internal static string NewId(IEnumerable<string> existing)
    {
        var set = existing.ToHashSet();
        while (true)
        {
            var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            if (!set.Contains(id)) return id;
        }
    }

    public void Update(WatchItem item) => Save();

    private static bool SamePath(string? a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(Path.GetFullPath(a).TrimEnd('/'), Path.GetFullPath(b).TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>The watch that lists a repository's pull requests, if one has been set up.</summary>
    public WatchItem? FindRepoPullRequests(string repoPath)
    {
        lock (_gate)
            return _state.Items.FirstOrDefault(i => i.Kind == RepoPullRequestWatchProvider.KindName && SamePath(i.Settings.GetValueOrDefault("path"), repoPath));
    }

    /// <summary>Creates the pull request watch for a repository (or returns the existing one).</summary>
    public WatchItem EnsureRepoPullRequests(string repoName, string repoPath, string? command = null)
    {
        if (FindRepoPullRequests(repoPath) is { } existing) return existing;
        var item = new WatchItem { Name = $"{repoName} pull requests", Kind = RepoPullRequestWatchProvider.KindName, Settings = { ["path"] = repoPath } };
        if (!string.IsNullOrWhiteSpace(command)) item.Settings["command"] = command.Trim();
        return Add(item);
    }

    public static bool IsPullRequestKind(string kind) => kind is RepoPullRequestWatchProvider.KindName or "github-pr" or "ado-pr";

    public void Remove(string idOrName)
    {
        var item = Get(idOrName);
        lock (_gate) _state.Items.Remove(item);
        Save();
    }

    public void Save()
    {
        lock (_gate) JsonUtil.SaveFile(_paths.StateFile("watches"), _state);
        Changed?.Invoke();
    }

    private void Tick()
    {
        var s = _config.Settings.Watching;
        if (!s.Enabled) return;
        foreach (var item in Items.Where(i => i.Enabled))
        {
            var interval = TimeSpan.FromMinutes(Math.Max(0.1, item.IntervalMinutes ?? s.DefaultIntervalMinutes));
            if (item.LastChecked is { } last && DateTime.Now - last < interval) continue;
            _ = CheckAsync(item);
        }
    }

    public async Task CheckAllAsync() => await Task.WhenAll(Items.Where(i => i.Enabled).Select(CheckAsync));

    public async Task<WatchItem> CheckAsync(WatchItem item)
    {
        lock (_gate) if (!_running.Add(item.Id)) return item;
        var before = Signature(item);
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.Settings.Watching.TimeoutSeconds));
            using var cts = new CancellationTokenSource(timeout + TimeSpan.FromSeconds(2));
            var provider = Resolve(item);
            var ctx = new WatchContext { Name = item.Name, Settings = item.Settings, Link = item.Link, Timeout = timeout };
            var result = await Task.Run(() => provider.CheckAsync(ctx, cts.Token), cts.Token).WaitAsync(timeout + TimeSpan.FromSeconds(3));
            result.Status = CommandWatchProvider.NormalizeStatus(result.Status);
            item.LastResult = result;
            item.LastError = null;
        }
        catch (Exception ex)
        {
            item.LastError = ex is TimeoutException or OperationCanceledException ? "Timed out" : ex.GetBaseException().Message;
        }
        finally { lock (_gate) _running.Remove(item.Id); }
        var first = item.LastChecked is null;
        item.LastChecked = DateTime.Now;
        if (Signature(item) != before)
        {
            item.LastChanged = DateTime.Now;
            Log.Info("watch", $"{item.Name}: [{item.Status}] {item.Summary}");
            item.Notified = !first;
            if (!first && _config.Settings.Watching.NotifyOnChange) Host.Notify("Watch: " + item.Name, item.Summary);
            try { ResultChanged?.Invoke(item); } catch { }
        }
        Save();
        return item;
    }

    private static string Signature(WatchItem i) => JsonSerializer.Serialize(new { i.LastError, i.LastResult }, JsonUtil.Compact);

    public void Dispose() => _timer?.Dispose();
}
