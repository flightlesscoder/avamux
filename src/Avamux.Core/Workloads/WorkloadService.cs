using System.Collections.Concurrent;
using Avamux.Core.Logging;
using Avamux.Plugins;

namespace Avamux.Core.Workloads;

/// <summary>A workload with the source it came from. <see cref="Id"/> is "&lt;source&gt;:&lt;the source's id&gt;".</summary>
public sealed record WorkloadEntry(string SourceId, string SourceName, string Machine, WorkloadInfo Info)
{
    public string Id => SourceId + ":" + Info.Id;
}

/// <summary>Everything the sources had at one moment.</summary>
public sealed record WorkloadOverview(IReadOnlyList<WorkloadEntry> Items, IReadOnlyDictionary<string, string> Problems, GpuSnapshot Gpu, DateTime Taken);

/// <summary>What starting a profile would get in the way of, asked before starting it.</summary>
/// <param name="Conflicts">Running workloads that would be stopped first (same port or group).</param>
/// <param name="Busy">Of those, the ones answering a request right now.</param>
/// <param name="GpuHolders">Other programs holding a lot of GPU memory (a game), which may make the start fail.</param>
public sealed record StartCheck(WorkloadProfile Profile, IReadOnlyList<WorkloadEntry> Conflicts, IReadOnlyList<WorkloadEntry> Busy,
    IReadOnlyList<(int Pid, string Name, long MiB)> GpuHolders, long? FreeVramMiB, IReadOnlyList<string> Notes);

public sealed class StartOptions
{
    /// <summary>A context size for this start (llama.cpp profiles, and scripts that take one).</summary>
    public long? Context { get; init; }
    /// <summary>Stop what is in the way (same port or group) without asking. Off: refuse when something is.</summary>
    public bool StopOthers { get; init; } = true;
    /// <summary>Stop what is in the way even when it is answering a request.</summary>
    public bool Force { get; init; }
}

/// <summary>
/// AI Workloads: the model servers, queue jobs, agents and tasks on this computer and on whatever plugin sources
/// reach, the profiles that start servers, and the models on disk. avamux is the admin here: it can stop anything
/// it can see, asking first (in the UI) when it did not start it.
/// </summary>
public sealed class WorkloadService : IAsyncDisposable
{
    public const string LocalSourceId = "local";
    /// <summary>Below this much free GPU memory after a start, avamux warns: a server this close to full can die on its next allocation.</summary>
    public const long LowVramMiB = 800;
    /// <summary>Other programs holding more than this much GPU memory are named before a start.</summary>
    public const long GpuHolderMiB = 1024;

    private readonly AppServices _app;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Operation> _ops = new();
    private readonly ConcurrentDictionary<string, IWorkloadLease> _leases = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastActive = new();
    private readonly ConcurrentDictionary<int, long> _contexts = new();
    private readonly List<IWorkloadSource> _extra = [];
    private readonly CancellationTokenSource _stop = new();
    private WorkloadFile _state;
    private WorkloadOverview? _last;
    private Task? _loop;

    public WorkloadService(AppServices app)
    {
        _app = app;
        _file = app.Paths.StateFile("ai-workloads");
        _state = File.Exists(_file) ? JsonUtil.LoadFile<WorkloadFile>(_file) : new WorkloadFile();
        Local = new LocalSource(this);
        Tasks = new TasksSource(app);
        Containers = new ContainerSource(ContainerPatterns);
        Models = new ModelLibrary(Path.Combine(Dir, "models-cache.json"));
        app.Plugins.Changed += () => Changed?.Invoke();
    }

    // ---- what plugins add ----

    /// <summary>Conventions for reading start scripts (see <see cref="ScriptConvention"/>).</summary>
    public HookList<ScriptConvention> ScriptConventions { get; } = new();

    /// <summary>More container names and images that count as AI things.</summary>
    public HookList<System.Text.RegularExpressions.Regex> ContainerPatterns { get; } = new();

    /// <summary>Run after avamux started a server and it answers; they may add notes to what the start reports.</summary>
    public HookList<Func<ServerStarted, Task>> StartHooks { get; } = new();

    /// <summary>
    /// The server on this computer an AI provider (and model) talks to, when a plugin knows: AI Chat then offers to
    /// start a profile for that port when nothing answers there.
    /// </summary>
    public HookList<Func<AiProviderConfig, string?, LocalEndpoint?>> EndpointResolvers { get; } = new();

    /// <summary>Notes what context the server on a port of this computer was seen with.</summary>
    public void SeenContext(int port, long context) => _contexts[port] = context;

    /// <summary>The context of whatever server answers on a port of this computer, as last seen (null when not known).</summary>
    public long? KnownContext(int port) => _contexts.TryGetValue(port, out var c) ? c : null;

    public string Dir => Path.Combine(_app.Paths.DataDir, "ai", "workloads");
    public string RunDir => Path.Combine(Dir, "run");
    public AppServices App => _app;
    internal LocalSource Local { get; }
    internal TasksSource Tasks { get; }
    internal ContainerSource Containers { get; }
    public ModelLibrary Models { get; }

    /// <summary>Raised when something changed that the page should show now (an operation began or ended, a profile changed).</summary>
    public event Action? Changed;
    /// <summary>Something worth telling the user happened in the background (a lease was taken back, a server stopped for idleness).</summary>
    public event Action<string, string>? Notice;

    // ---- sources ----

    public IReadOnlyList<IWorkloadSource> Sources
    {
        get
        {
            List<IWorkloadSource> extra;
            lock (_gate) extra = [.. _extra];
            return [Local, Tasks, Containers, .. _app.Plugins.WorkloadSources(), .. extra];
        }
    }

    /// <summary>Adds a source directly (tests, and code that is not a plugin).</summary>
    public void AddSource(IWorkloadSource source) { lock (_gate) _extra.Add(source); Changed?.Invoke(); }

    private readonly List<IWorkloadLeaseProvider> _extraLeases = [];
    public void AddLeaseProvider(IWorkloadLeaseProvider provider) { lock (_gate) _extraLeases.Add(provider); }
    public IReadOnlyList<IWorkloadLeaseProvider> LeaseProviders
    {
        get { List<IWorkloadLeaseProvider> extra; lock (_gate) extra = [.. _extraLeases]; return [.. _app.Plugins.LeaseProviders(), .. extra]; }
    }

    /// <summary>How long a source is given to say what it has (tests shorten it).</summary>
    internal TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Asks every source what it has (all at once; a slow or failing source only costs its own part).</summary>
    public async Task<WorkloadOverview> ListAsync(CancellationToken ct = default)
    {
        var gpuTask = Gpu.ReadAsync(ct);
        var sources = Sources;
        var results = await Task.WhenAll(sources.Select(async s =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SourceTimeout);
            try { return (s, snap: await s.ListAsync(cts.Token)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (s, snap: new WorkloadSnapshot([], ex is OperationCanceledException ? "It did not answer in time." : ex.Message));
            }
        }));
        var items = new List<WorkloadEntry>();
        var problems = new Dictionary<string, string>();
        foreach (var (s, snap) in results)
        {
            items.AddRange(snap.Items.Select(i => new WorkloadEntry(s.Id, s.Name, s.Machine, i)));
            if (snap.Problem is { Length: > 0 } p) problems[s.Id] = p;
        }
        // A process another source on this computer describes (a queue's model server) is shown once, as that source has it.
        var described = items.Where(e => e.SourceId != LocalSourceId && e.Machine == Local.Machine && e.Info.Pid is not null).Select(e => e.Info.Pid!.Value).ToHashSet();
        if (described.Count > 0) items.RemoveAll(e => e.SourceId == LocalSourceId && e.Info.Id.StartsWith(LocalSource.PidPrefix, StringComparison.Ordinal) && described.Contains(e.Info.Pid ?? 0));
        var overview = new WorkloadOverview(items, problems, await gpuTask, DateTime.Now);
        _last = overview;
        return overview;
    }

    /// <summary>The last overview, if one was taken in the last few seconds; else a fresh one.</summary>
    public async Task<WorkloadOverview> RecentAsync(TimeSpan maxAge, CancellationToken ct = default) =>
        _last is { } l && DateTime.Now - l.Taken < maxAge ? l : await ListAsync(ct);

    public async Task<WorkloadEntry> FindAsync(string id, CancellationToken ct = default)
    {
        // "<source>:<id>": ask only that source, so a slow one (podman, another machine) cannot hold up acting on this
        // one. Its answer is the answer: asking every source after it failed would wait for it (and the others) again.
        if (id.IndexOf(':') is var colon and > 0 && Sources.FirstOrDefault(s => s.Id == id[..colon]) is { } source)
        {
            var mine = id[(colon + 1)..];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SourceTimeout);
            WorkloadSnapshot snap;
            try { snap = await source.ListAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"{source.Name} did not answer in {SourceTimeout.TotalSeconds:0.#} seconds, so avamux cannot find '{mine}' there.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{source.Name} could not be asked for '{mine}': {ex.GetBaseException().Message}", ex);
            }
            return snap.Items.FirstOrDefault(i => i.Id == mine) is { } found
                ? new WorkloadEntry(source.Id, source.Name, source.Machine, found)
                : throw new KeyNotFoundException($"No workload '{mine}' in {source.Name}" + (snap.Problem is { Length: > 0 } problem ? $" ({problem})" : "") + ". See `avamux ai workloads list`.");
        }
        var all = (await ListAsync(ct)).Items;
        return all.FirstOrDefault(e => e.Id == id)
            ?? all.FirstOrDefault(e => e.Info.Id == id)
            ?? all.FirstOrDefault(e => string.Equals(e.Info.Name, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No workload '{id}'. See `avamux ai workloads list`.");
    }

    /// <summary>Does an action on a workload (its id as listed, "&lt;source&gt;:&lt;id&gt;").</summary>
    public async Task<string> ActAsync(string id, string action, CancellationToken ct = default)
    {
        var entry = await FindAsync(id, ct);
        if (entry.Info.Actions.All(a => a.Id != action))
            throw new InvalidOperationException($"{entry.Info.Name} cannot {action} (it offers: {string.Join(", ", entry.Info.Actions.Select(a => a.Id))}).");
        var source = Sources.First(s => s.Id == entry.SourceId);
        Log.Info("workloads", $"{action}: {entry.Info.Name} ({entry.Id})");
        try
        {
            var result = await source.ActAsync(entry.Info.Id, action, ct);
            Log.Info("workloads", $"{action}: {entry.Info.Name}: {result}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("workloads", $"{action}: {entry.Info.Name} failed: {ex.GetBaseException().Message}");
            throw;
        }
        finally { Changed?.Invoke(); }
    }

    public async Task<string> LogAsync(string id, int lines = 200, CancellationToken ct = default)
    {
        var entry = await FindAsync(id, ct);
        return await Sources.First(s => s.Id == entry.SourceId).LogAsync(entry.Info.Id, lines, ct);
    }

    // ---- profiles ----

    /// <summary>Folders searched for start/stop script pairs.</summary>
    public IReadOnlyList<string> ScriptFolders { get { lock (_gate) return _state.ScriptFolders?.ToList() ?? SettingFolders(_app.Config.Settings.Ai.WorkloadScripts); } }
    public IReadOnlyList<string> ModelFolders
    {
        get { lock (_gate) return _state.ModelFolders?.ToList() ?? [.. ModelLibrary.DefaultFolders(), .. SettingFolders(_app.Config.Settings.Ai.WorkloadModels)]; }
    }

    private static List<string> SettingFolders(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(AiPath).Distinct().ToList();

    public void SetScriptFolders(IEnumerable<string> folders) { lock (_gate) { _state.ScriptFolders = folders.ToList(); Save(); } Changed?.Invoke(); }
    public void SetModelFolders(IEnumerable<string> folders) { lock (_gate) { _state.ModelFolders = folders.ToList(); Save(); } Changed?.Invoke(); }

    /// <summary>Every profile: the user's own, then those found in the script folders (with the user's changes to them).</summary>
    public IReadOnlyList<WorkloadProfile> Profiles
    {
        get
        {
            List<WorkloadProfile> own;
            Dictionary<string, ScriptOverride> overrides;
            lock (_gate) { own = _state.Profiles.Select(p => p.Clone()).ToList(); overrides = new(_state.Scripts); }
            var scripts = ScriptCatalog.Discover(ScriptFolders, ScriptConventions.Items);
            foreach (var s in scripts)
            {
                if (!overrides.TryGetValue(s.Id, out var o)) continue;
                if (o.Name is { Length: > 0 } n) s.Name = n;
                if (o.Hidden is { } h) s.Hidden = h;
                if (o.StartArgs is { } a) s.StartArgs = a;
                if (o.Group is { } g) s.Group = g;
                if (o.IdleMinutes is { } i) s.IdleMinutes = i;
                if (o.NeedsRpc is { } r) s.NeedsRpc = r;
                if (o.Leases is { } l) s.Leases = l;
                if (o.Notes is { } t) s.Notes = t;
            }
            // A script that talks to an RPC server needs the rpc profile with that address, when there is one.
            foreach (var s in scripts.Where(s => s.RpcAddress.Length > 0 && s.NeedsRpc.Count == 0))
                if (own.FirstOrDefault(p => p.Kind == WorkloadProfile.Rpc && p.RpcAddress == s.RpcAddress) is { } rpc) s.NeedsRpc = [rpc.Id];
            return [.. own, .. scripts.Where(s => own.All(o => o.Id != s.Id))];
        }
    }

    public WorkloadProfile? Profile(string idOrName) =>
        Profiles.FirstOrDefault(p => p.Id == idOrName) ?? Profiles.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault(p => p.Id == "script:" + idOrName);

    public WorkloadProfile GetProfile(string idOrName) => Profile(idOrName) ?? throw new KeyNotFoundException($"No profile '{idOrName}'. See `avamux ai workloads profiles`.");

    /// <summary>Adds or replaces a profile. A discovered script profile keeps only what can be changed about it.</summary>
    public WorkloadProfile PutProfile(WorkloadProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("A profile needs a name.");
        if (profile.Kind is not (WorkloadProfile.LlamaCpp or WorkloadProfile.ScriptKind or WorkloadProfile.CommandKind or WorkloadProfile.Rpc or WorkloadProfile.Api))
            throw new ArgumentException($"Unknown profile kind '{profile.Kind}' (llama.cpp, script, command, rpc or api).");
        lock (_gate)
        {
            if (profile.Id.StartsWith("script:", StringComparison.Ordinal) && _state.Profiles.All(p => p.Id != profile.Id))
            {
                _state.Scripts[profile.Id] = new ScriptOverride
                {
                    Name = profile.Name, Hidden = profile.Hidden, StartArgs = profile.StartArgs, Group = profile.Group, IdleMinutes = profile.IdleMinutes,
                    NeedsRpc = profile.NeedsRpc, Leases = profile.Leases, Notes = profile.Notes,
                };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(profile.Id))
                {
                    var stem = string.Concat(profile.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
                    if (stem.Length == 0) stem = profile.Kind.Replace(".", "");
                    var id = stem;
                    for (var n = 2; _state.Profiles.Any(p => p.Id == id); n++) id = $"{stem}-{n}";
                    profile.Id = id;
                }
                var i = _state.Profiles.FindIndex(p => p.Id == profile.Id);
                if (i >= 0) _state.Profiles[i] = profile.Clone(); else _state.Profiles.Add(profile.Clone());
            }
            Save();
        }
        Changed?.Invoke();
        return GetProfile(profile.Id);
    }

    public void RemoveProfile(string idOrName)
    {
        var p = GetProfile(idOrName);
        lock (_gate)
        {
            if (p.Discovered) _state.Scripts[p.Id] = new ScriptOverride { Hidden = true };
            else _state.Profiles.RemoveAll(x => x.Id == p.Id);
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        try { JsonUtil.SaveFile(_file, _state); }
        catch (Exception ex) { Log.Warn("workloads", "Could not save ai-workloads.json: " + ex.Message); }
    }

    /// <summary>Model file → the names of the profiles that load it.</summary>
    public Dictionary<string, List<string>> ModelUse()
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var p in Profiles.Where(p => p.Model.Length > 0))
        {
            string path;
            try { path = RealPath(AiPath(p.Model)); }
            catch { continue; }
            if (!map.TryGetValue(path, out var list)) map[path] = list = [];
            list.Add(p.Name);
        }
        return map;
    }

    internal string PidFileOf(WorkloadProfile p) => p.PidFile.Length > 0 ? p.PidFile : Path.Combine(RunDir, Safe(p.Id) + ".pid");
    internal string LogFileOf(WorkloadProfile p) => p.LogFile.Length > 0 ? p.LogFile : Path.Combine(RunDir, Safe(p.Id) + ".log");
    internal string LaunchLogOf(WorkloadProfile p) => Path.Combine(RunDir, Safe(p.Id) + ".launch.log");
    private static string Safe(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));

    // ---- operations (starting, stopping) ----

    /// <summary>A start or stop in progress, or the last one that failed (until the next).</summary>
    internal sealed class Operation
    {
        public required string ProfileId { get; init; }
        public WorkloadState State { get; set; }
        public string Detail { get; set; } = "";
        public string? Error { get; set; }
        public DateTime Started { get; } = DateTime.Now;
        public DateTime? Finished { get; set; }
        public CancellationTokenSource Cts { get; } = new();
    }

    internal Operation? OperationOf(string profileId) => _ops.TryGetValue(profileId, out var op) ? op : null;

    private Operation Begin(string profileId, WorkloadState state, string detail)
    {
        var op = new Operation { ProfileId = profileId, State = state, Detail = detail };
        _ops[profileId] = op;
        Changed?.Invoke();
        return op;
    }

    private void End(Operation op, string? error)
    {
        op.Finished = DateTime.Now;
        op.Error = error;
        if (error is null) _ops.TryRemove(op.ProfileId, out _);
        else op.State = WorkloadState.Failed;
        Changed?.Invoke();
    }

    /// <summary>Forgets a failed start, so the profile shows as stopped again.</summary>
    public void Dismiss(string profileId) { if (_ops.TryGetValue(profileId, out var op) && op.Finished is not null) _ops.TryRemove(profileId, out _); Changed?.Invoke(); }

    /// <summary>What starting a profile would stop or run into. Nothing changes.</summary>
    public async Task<StartCheck> CheckStartAsync(string profileId, CancellationToken ct = default)
    {
        var p = GetProfile(profileId);
        var overview = await ListAsync(ct);
        var mine = overview.Items.FirstOrDefault(e => e.SourceId == LocalSourceId && e.Info.ProfileId == p.Id && e.Info.Kind == WorkloadKind.Server);
        var conflicts = new List<WorkloadEntry>();
        if (p.Kind is not (WorkloadProfile.Api or WorkloadProfile.Rpc))
        {
            var profiles = Profiles.ToDictionary(x => x.Id);
            foreach (var e in overview.Items.Where(e => e.SourceId == LocalSourceId && e.Info.Kind == WorkloadKind.Server && e != mine))
            {
                if (e.Info.State is WorkloadState.Stopped or WorkloadState.Failed or WorkloadState.Done) continue;
                var samePort = p.Port > 0 && e.Info.Port == p.Port;
                var group = e.Info.ProfileId is { } pid && profiles.TryGetValue(pid, out var other) ? other.Group
                    : (e.Info.VramMiB ?? 0) > 0 || e.Info.Type?.StartsWith("llama.cpp", StringComparison.Ordinal) == true ? "gpu" : "";
                var sameGroup = p.Group.Length > 0 && group == p.Group;
                if (samePort || sameGroup) conflicts.Add(e);
            }
        }
        var busy = conflicts.Where(c => c.Info.State == WorkloadState.Busy).ToList();
        var conflictPids = conflicts.Select(c => c.Info.Pid).OfType<int>().ToHashSet();
        var processes = ProcessScanner.List().ToDictionary(x => x.Pid);
        var holders = overview.Gpu.UsedByPid.Where(kv => kv.Value >= GpuHolderMiB && !conflictPids.Contains(kv.Key))
            .Select(kv => (kv.Key, processes.TryGetValue(kv.Key, out var proc) ? proc.Name : $"pid {kv.Key}", kv.Value)).ToList();
        var notes = new List<string>();
        if (mine is { Info.State: not WorkloadState.Stopped and not WorkloadState.Failed }) notes.Add($"{p.Name} is already running.");
        if (p.Model.Length > 0 && p.Kind == WorkloadProfile.LlamaCpp && !File.Exists(AiPath(p.Model))) notes.Add($"The model file is not there: {p.Model}");
        if (p.Kind == WorkloadProfile.LlamaCpp && !File.Exists(AiPath(p.Binary))) notes.Add($"llama-server is not there: {(p.Binary.Length > 0 ? p.Binary : "(not set)")}");
        var freed = conflicts.Sum(c => c.Info.VramMiB ?? 0);
        long? free = overview.Gpu.Gpus.Count > 0 ? overview.Gpu.FreeMiB + freed : null;
        return new StartCheck(p, conflicts, busy, holders, free, notes);
    }

    /// <summary>
    /// Starts a profile: stops what is in its way (same port or group), takes the leases it needs, starts the RPC servers
    /// it uses, starts it and waits until it answers. Returns what happened, including warnings (little GPU memory left).
    /// </summary>
    public async Task<string> StartAsync(string profileId, StartOptions? options = null, CancellationToken ct = default)
    {
        options ??= new StartOptions();
        var p = GetProfile(profileId);
        if (p.Kind == WorkloadProfile.Api) return $"{p.Name} is a hosted API; there is nothing to start.";
        await _startGate.WaitAsync(ct);
        Operation? op = null;
        try
        {
            var check = await CheckStartAsync(p.Id, ct);
            if (check.Notes.FirstOrDefault(n => n.EndsWith("already running.", StringComparison.Ordinal)) is { } running) return running;
            if (check.Notes.FirstOrDefault(n => n.Contains(" is not there", StringComparison.Ordinal)) is { } missing) throw new InvalidOperationException(missing);
            if (check.Conflicts.Count > 0 && !options.StopOthers)
                throw new InvalidOperationException($"In the way: {string.Join(", ", check.Conflicts.Select(c => c.Info.Name))}. Stop it first, or swap.");
            if (check.Busy.Count > 0 && !options.Force)
                throw new InvalidOperationException($"{string.Join(", ", check.Busy.Select(c => c.Info.Name))} is answering a request right now. Wait, or force the swap.");

            op = Begin(p.Id, WorkloadState.Starting, "Starting");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, op.Cts.Token);
            var token = linked.Token;
            foreach (var c in check.Conflicts)
            {
                op.Detail = $"Stopping {c.Info.Name}";
                Changed?.Invoke();
                await StopEntryAsync(c, token);
            }

            // Leases: this profile's, and those of the RPC servers it uses.
            var rpcs = p.NeedsRpc.Select(Profile).OfType<WorkloadProfile>().ToList();
            foreach (var leaseId in p.Leases.Concat(rpcs.SelectMany(r => r.Leases)).Distinct())
            {
                var provider = LeaseProviders.FirstOrDefault(l => l.Id == leaseId)
                    ?? throw new InvalidOperationException($"{p.Name} needs a lease from '{leaseId}', and nothing provides one (is its plugin switched on?).");
                op.Detail = $"Asking {provider.Name}";
                op.State = WorkloadState.Waiting;
                Changed?.Invoke();
                var lease = await provider.AcquireAsync(new WorkloadLeaseRequest($"avamux: {p.Name}", "exclusive", rpcs.Count > 0 ? "A llama.cpp server here uses this machine's GPU over RPC" : null), token);
                _leases[p.Id + "|" + leaseId] = lease;
                _ = WatchLeaseAsync(p.Id, provider.Name, lease);
            }
            foreach (var rpc in rpcs)
            {
                op.Detail = $"Checking {rpc.Name}";
                op.State = WorkloadState.Starting;
                Changed?.Invoke();
                var rpcProblem = await Local.EnsureRpcAsync(rpc, p, token);
                if (rpcProblem is not null) throw new InvalidOperationException(rpcProblem);
            }

            op.Detail = "Starting";
            op.State = WorkloadState.Starting;
            Changed?.Invoke();
            var url = await Local.LaunchAsync(p, options, op, token);
            lock (_gate)
            {
                _state.Started[LocalSourceId + ":" + LocalSource.ProfilePrefix + p.Id] = DateTime.Now;
                if (p.Port > 0) _state.LastOnPort[p.Port] = p.Id;
                Save();
            }
            _lastActive[p.Id] = DateTime.Now;

            var notes = new List<string> { $"{p.Name} is running" + (url is null ? "." : $" on {url}.") };
            if (url is not null)
            {
                var status = await ServerProbe.ProbeAsync(url, token);
                if (status.Context is { } ctx)
                {
                    notes.Add($"Context: {ctx:N0} tokens.");
                    if (p.Port > 0) SeenContext(p.Port, ctx);
                }
                var started = new ServerStarted(p.Clone(), url, status.Context, notes, token);
                foreach (var hook in StartHooks.Items)
                {
                    try { await hook(started); }
                    catch (Exception ex) { Log.Warn("workloads", $"A plugin's start hook failed: {ex.Message}"); }
                }
            }
            var after = await Gpu.ReadAsync(token);
            if (after.Gpus.Count > 0 && after.Gpus.Min(g => g.FreeMiB) < LowVramMiB)
                notes.Add($"Warning: only {after.Gpus.Min(g => g.FreeMiB):N0} MiB of GPU memory is left. A server this close to full can die on its next allocation; free some, or use fewer GPU layers or a smaller context.");
            End(op, null);
            return string.Join(' ', notes);
        }
        catch (Exception ex)
        {
            await ReleaseLeasesAsync(p.Id);
            if (op is not null)
            {
                var why = ex is OperationCanceledException ? "The start was cancelled." : ex.Message;
                End(op, why);
                Log.Warn("workloads", $"Starting {p.Name} failed: {why}");
                if (ex is OperationCanceledException) throw;
                throw new InvalidOperationException(why, ex);
            }
            throw;
        }
        finally { _startGate.Release(); }
    }

    /// <summary>Cancels a start in progress.</summary>
    public bool CancelStart(string profileId)
    {
        if (!_ops.TryGetValue(profileId, out var op) || op.Finished is not null) return false;
        op.Cts.Cancel();
        return true;
    }

    /// <summary>Stops a profile's server (by its own stop script when it has one).</summary>
    public async Task<string> StopProfileAsync(string profileId, CancellationToken ct = default)
    {
        var p = GetProfile(profileId);
        CancelStart(p.Id);
        var op = Begin(p.Id, WorkloadState.Stopping, "Stopping");
        try
        {
            var result = await Local.StopProfileAsync(p, ct);
            End(op, null);
            return result;
        }
        catch (Exception ex) { End(op, ex.Message); throw; }
        finally
        {
            lock (_gate) { if (_state.Started.Remove(LocalSourceId + ":" + LocalSource.ProfilePrefix + p.Id)) Save(); }
            await ReleaseLeasesAsync(p.Id);
        }
    }

    /// <summary>Stops whatever a listed workload is, the way its source does it.</summary>
    private async Task StopEntryAsync(WorkloadEntry e, CancellationToken ct)
    {
        if (e.Info.ProfileId is { } pid && e.SourceId == LocalSourceId) { await StopProfileAsync(pid, ct); return; }
        var source = Sources.First(s => s.Id == e.SourceId);
        var action = e.Info.Actions.FirstOrDefault(a => a.Id is WorkloadActions.Stop or WorkloadActions.Unload or WorkloadActions.Cancel)
            ?? throw new InvalidOperationException($"avamux cannot stop {e.Info.Name}.");
        await source.ActAsync(e.Info.Id, action.Id, ct);
    }

    internal void Forget(string workloadId) { lock (_gate) { if (_state.Started.Remove(workloadId)) Save(); } }

    private readonly ConcurrentDictionary<string, int> _stopLeft = new();

    /// <summary>A stop of this profile ended without its process ending: the profile then offers to end that process.</summary>
    internal void NoteStopLeft(string profileId, int pid) => _stopLeft[profileId] = pid;

    internal int? StopLeft(string profileId) => _stopLeft.TryGetValue(profileId, out var pid) ? pid : null;

    internal void ForgetStopLeft(string profileId) => _stopLeft.TryRemove(profileId, out _);

    internal bool StartedByAvamux(string workloadId) { lock (_gate) return _state.Started.ContainsKey(workloadId); }

    /// <summary>avamux has started servers it has not seen stop (quick: no process is looked at).</summary>
    public bool MayHaveStarted { get { lock (_gate) return _state.Started.Count > 0; } }

    /// <summary>Servers avamux started that are still running (for "Close AI servers?" when avamux quits).</summary>
    public async Task<IReadOnlyList<WorkloadEntry>> RunningStartedByAvamuxAsync(CancellationToken ct = default)
    {
        Dictionary<string, DateTime> started;
        lock (_gate) started = new(_state.Started);
        if (started.Count == 0) return [];
        var local = await Local.ListAsync(ct);
        return local.Items.Where(i => i.ByAvamux && i.State is not (WorkloadState.Stopped or WorkloadState.Failed or WorkloadState.Done))
            .Select(i => new WorkloadEntry(Local.Id, Local.Name, Local.Machine, i)).ToList();
    }

    /// <summary>Stops every server avamux started.</summary>
    public async Task StopAllStartedAsync(CancellationToken ct = default)
    {
        foreach (var e in await RunningStartedByAvamuxAsync(ct))
        {
            try { await StopEntryAsync(e, ct); }
            catch (Exception ex) { Log.Warn("workloads", $"Could not stop {e.Info.Name}: {ex.Message}"); }
        }
    }

    // ---- what an AI provider needs ----

    /// <summary>A local model server a provider talks to, whether it answers, and the profiles that can serve that port (the last one used first).</summary>
    public sealed record EndpointCheck(int Port, bool Answering, IReadOnlyList<WorkloadProfile> Profiles, string? Name);

    /// <summary>
    /// For a provider (and model) that talks to a server on this computer, as a plugin's
    /// <see cref="EndpointResolvers"/> tell: whether that server answers, and what could be started there. Null for
    /// providers that need no local server, or when no plugin knows.
    /// </summary>
    public async Task<EndpointCheck?> CheckEndpointAsync(AiProviderConfig provider, string? model, CancellationToken ct = default)
    {
        model = string.IsNullOrWhiteSpace(model) ? provider.Model : model;
        if (EndpointResolvers.Each(r => r(provider, model), "workloads").FirstOrDefault(e => e is not null) is not { } endpoint) return null;
        var port = endpoint.Port;
        var status = await ServerProbe.ProbeAsync(ServerProbe.LocalUrl(port), ct);
        string? last;
        lock (_gate) last = _state.LastOnPort.GetValueOrDefault(port);
        var profiles = Profiles.Where(p => !p.Hidden && p.Port == port && p.Kind is WorkloadProfile.LlamaCpp or WorkloadProfile.ScriptKind or WorkloadProfile.CommandKind)
            .OrderBy(p => p.Id != last).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new EndpointCheck(port, status.Answering, profiles, endpoint.Name);
    }

    // ---- leases ----

    private async Task WatchLeaseAsync(string profileId, string providerName, IWorkloadLease lease)
    {
        string reason;
        try { reason = await lease.Lost.WaitAsync(_stop.Token); }
        catch { return; }
        if (!_leases.Values.Contains(lease)) return; // released by us
        var p = Profile(profileId);
        Notice?.Invoke($"{providerName} took its lease back", $"{reason} avamux is stopping {p?.Name ?? profileId}.");
        try { await StopProfileAsync(profileId); }
        catch (Exception ex) { Log.Warn("workloads", $"Could not stop {profileId} after losing its lease: {ex.Message}"); }
    }

    private async Task ReleaseLeasesAsync(string profileId)
    {
        foreach (var key in _leases.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal)).ToList())
            if (_leases.TryRemove(key, out var lease))
            {
                try { await lease.DisposeAsync(); }
                catch (Exception ex) { Log.Warn("workloads", $"Could not release the lease {key}: {ex.Message}"); }
            }
    }

    public IReadOnlyList<string> HeldLeases => _leases.Keys.ToList();

    // ---- the background loop: idle unload, leases whose server stopped ----

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("workloads", "Background check failed: " + ex.Message); }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var profiles = Profiles.Where(p => p.IdleMinutes > 0).ToList();
        var leased = _leases.Keys.Select(k => k.Split('|')[0]).Distinct().ToList();
        if (profiles.Count == 0 && leased.Count == 0) return;
        var local = (await Local.ListAsync(ct)).Items;
        foreach (var id in leased)
        {
            var w = local.FirstOrDefault(i => i.ProfileId == id && i.Kind == WorkloadKind.Server);
            if (_ops.ContainsKey(id)) continue;
            if (w is null || w.State is WorkloadState.Stopped or WorkloadState.Failed) await ReleaseLeasesAsync(id);
        }
        foreach (var p in profiles)
        {
            var w = local.FirstOrDefault(i => i.ProfileId == p.Id && i.Kind == WorkloadKind.Server);
            if (w is null || !w.ByAvamux || w.State is WorkloadState.Stopped or WorkloadState.Failed) { _lastActive.TryRemove(p.Id, out _); continue; }
            if (w.State is WorkloadState.Busy or WorkloadState.Loading or WorkloadState.Starting || !_lastActive.ContainsKey(p.Id)) { _lastActive[p.Id] = DateTime.Now; continue; }
            if (DateTime.Now - _lastActive[p.Id] < TimeSpan.FromMinutes(p.IdleMinutes)) continue;
            Log.Info("workloads", $"Stopping {p.Name}: idle for {p.IdleMinutes:0.#} minutes");
            await StopProfileAsync(p.Id, ct);
            Notice?.Invoke($"{p.Name} stopped", $"It had nothing to do for {p.IdleMinutes:0.#} minutes.");
        }
    }

    /// <summary>Notes that a server was used (a request was seen), for idle unloading.</summary>
    internal void Touch(string profileId) => _lastActive[profileId] = DateTime.Now;

    internal static string AiPath(string path) => Ai.AiService.ExpandHome(path);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr SysRealPath(string path, IntPtr resolved);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "free")]
    private static extern void SysFree(IntPtr p);

    /// <summary>A path with every link in it resolved (/home/x → /var/home/x where home is a link), so two spellings of one file compare equal.</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return full;
        try
        {
            var p = SysRealPath(full, IntPtr.Zero);
            if (p == IntPtr.Zero) return full;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p) ?? full; }
            finally { SysFree(p); }
        }
        catch { return full; }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) { try { await _loop; } catch { } }
        // Leases are kept: the servers they cover keep running when avamux quits, and the other side expires leases nobody renews.
    }
}

/// <summary>A server avamux has just started and that answers, for <see cref="WorkloadService.StartHooks"/>.</summary>
/// <param name="Profile">A copy of the profile it was started from.</param>
/// <param name="Url">Where it answers.</param>
/// <param name="Context">Its context, when it says.</param>
/// <param name="Notes">What the start reports; a hook may add to it.</param>
public sealed record ServerStarted(WorkloadProfile Profile, string Url, long? Context, List<string> Notes, CancellationToken Cancel);

/// <summary>A server on this computer that an AI provider talks to.</summary>
/// <param name="Port">Its port.</param>
/// <param name="Name">What the provider calls it, for messages; null for none.</param>
public sealed record LocalEndpoint(int Port, string? Name = null);
