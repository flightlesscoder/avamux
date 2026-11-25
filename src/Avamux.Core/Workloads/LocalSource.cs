using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avamux.Plugins;

namespace Avamux.Core.Workloads;

/// <summary>
/// This computer: the profiles (running or not), every other AI program that is running (servers, RPC servers,
/// coding agents, avamux's Kokoro helper), the models Ollama has loaded, RPC servers on other machines that profiles
/// use, and hosted APIs. A running process that a profile describes (by its pid file, its --alias, or its port) is
/// shown as that profile, so it is stopped the profile's way (its own stop script).
/// </summary>
internal sealed partial class LocalSource(WorkloadService svc) : IWorkloadSource
{
    public const string ProfilePrefix = "profile:", PidPrefix = "pid:", OllamaPrefix = "ollama:";
    private const string OllamaUrl = "http://127.0.0.1:11434";

    private readonly ConcurrentDictionary<string, long> _activity = new();
    private readonly ConcurrentDictionary<string, (DateTime At, bool Up, string? Detail)> _remote = new();

    public string Id => WorkloadService.LocalSourceId;
    public string Name => "This computer";
    public string Machine => "This computer";
#pragma warning disable CS0067 // the page polls; nothing pushes
    public event Action? Changed;
#pragma warning restore CS0067

    private sealed record Found(ProcInfo Proc, ProcKind Kind, List<int> Ports, long VramMiB, long RssKiB);

    /// <summary>The AI programs running now, each counted once (a program's helpers are folded into it).</summary>
    private static async Task<(List<Found> Found, Dictionary<int, ProcInfo> All, GpuSnapshot Gpu)> ScanAsync(CancellationToken ct)
    {
        var gpuTask = Gpu.ReadAsync(ct);
        var procs = ProcessScanner.List();
        var all = procs.ToDictionary(p => p.Pid);
        var kinds = new Dictionary<int, ProcKind>();
        foreach (var p in procs) if (ProcessScanner.Classify(p) is { } k) kinds[p.Pid] = k;
        var children = procs.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.Select(p => p.Pid).ToList());
        var top = kinds.Where(kv => !HasAncestor(kv.Key, kv.Value.Program)).ToList();
        bool HasAncestor(int pid, string program)
        {
            var cur = all.TryGetValue(pid, out var p) ? p.ParentPid : 0;
            for (var depth = 0; cur > 1 && depth < 40; depth++)
            {
                if (kinds.TryGetValue(cur, out var k) && k.Program == program) return true;
                cur = all.TryGetValue(cur, out var parent) ? parent.ParentPid : 0;
            }
            return false;
        }
        List<int> Family(int pid)
        {
            var list = new List<int> { pid };
            for (var i = 0; i < list.Count && list.Count < 200; i++)
                if (children.TryGetValue(list[i], out var c)) list.AddRange(c.Where(x => !kinds.ContainsKey(x) || kinds[x].Program == kinds[pid].Program));
            return list;
        }
        var families = top.ToDictionary(t => t.Key, t => t.Value.Kind is WorkloadKind.Agent ? [t.Key] : Family(t.Key));
        var ports = ProcessScanner.ListeningPorts(families.Values.SelectMany(f => f).Distinct());
        var gpu = await gpuTask;
        var found = top.Select(t =>
        {
            var family = families[t.Key];
            var vram = family.Sum(pid => gpu.UsedByPid.GetValueOrDefault(pid));
            var rss = family.Sum(pid => all.TryGetValue(pid, out var p) ? p.RssKiB : 0);
            var listening = family.SelectMany(pid => ports.GetValueOrDefault(pid) ?? []).Distinct().Order().ToList();
            return new Found(all[t.Key], t.Value, listening, vram, rss);
        }).ToList();
        return (found, all, gpu);
    }

    /// <summary>
    /// The running process a profile describes: its pid file, else a server started with its alias, else (a script
    /// whose alias names a program, such as one a plugin recognises) that program on the profile's port.
    /// </summary>
    private Found? Match(WorkloadProfile p, List<Found> found, HashSet<int> claimed)
    {
        if (Launcher.ReadPid(svc.PidFileOf(p)) is { } pid && found.FirstOrDefault(f => f.Proc.Pid == pid || IsAncestor(f, pid)) is { } byPid && !claimed.Contains(byPid.Proc.Pid))
            return byPid;
        if (p.Kind == WorkloadProfile.CommandKind) return null;
        var alias = p.Kind == WorkloadProfile.LlamaCpp ? p.EffectiveAlias : p.Alias;
        foreach (var f in found.Where(f => f.Kind.Kind == WorkloadKind.Server && !claimed.Contains(f.Proc.Pid)))
        {
            if (alias.Length == 0) continue;
            if (f.Proc.Arg("--alias", "-a") == alias) return f;
            if (p.Kind == WorkloadProfile.ScriptKind && f.Kind.Program == alias && (p.Port == 0 || f.Ports.Contains(p.Port))) return f;
        }
        return null;
    }

    private static bool IsAncestor(Found f, int pid) => f.Proc.ParentPid == pid;

    public async Task<WorkloadSnapshot> ListAsync(CancellationToken ct = default)
    {
        var (found, all, _) = await ScanAsync(ct);
        var claimed = new HashSet<int>();
        var items = new List<Task<WorkloadInfo?>>();
        foreach (var p in svc.Profiles)
        {
            switch (p.Kind)
            {
                case WorkloadProfile.Rpc: items.Add(RpcAsync(p, found, claimed, ct)!); continue;
                case WorkloadProfile.Api: items.Add(ApiAsync(p, ct)!); continue;
            }
            var match = Match(p, found, claimed);
            if (match is not null) claimed.Add(match.Proc.Pid);
            items.Add(ProfileAsync(p, match, ct));
        }
        var me = Environment.ProcessId;
        foreach (var f in found.Where(f => !claimed.Contains(f.Proc.Pid)))
            items.Add(ProcessAsync(f, all, me, ct)!);
        var list = (await Task.WhenAll(items)).OfType<WorkloadInfo>().ToList();
        if (found.Any(f => f.Kind.Program == "ollama") && await ServerProbe.OllamaLoadedAsync(OllamaUrl, ct) is { } loaded)
            list.AddRange(loaded.Select(m => new WorkloadInfo
            {
                Id = OllamaPrefix + m.Name, Kind = WorkloadKind.Server, Name = m.Name, Type = "Ollama model", State = WorkloadState.Running, Model = m.Name,
                Endpoint = OllamaUrl, Port = 11434, Context = m.Context, VramMiB = m.VramBytes / 1048576, StartedBy = "Ollama",
                Detail = m.ExpiresAt is { } e ? $"Unloads by itself {Ago(e)}" : null,
                Actions = [new WorkloadAction(WorkloadActions.Unload, "Unload")],
            }));
        return new WorkloadSnapshot(list);
    }

    private static string Ago(DateTime when)
    {
        var d = when - DateTime.Now;
        return d.TotalMinutes < 1 ? "in under a minute" : d.TotalHours < 1 ? $"in {d.TotalMinutes:0} minutes" : d.TotalDays < 2 ? $"in {d.TotalHours:0.#} hours" : "much later";
    }

    private async Task<WorkloadInfo?> ProfileAsync(WorkloadProfile p, Found? f, CancellationToken ct)
    {
        var op = svc.OperationOf(p.Id);
        if (f is null && op is null && p.Hidden) return null;
        var id = ProfilePrefix + p.Id;
        var wid = WorkloadService.LocalSourceId + ":" + id;
        var url = p.BaseUrl;
        ServerStatus? status = null;
        if (f is not null && url is not null) status = await ServerProbe.ProbeAsync(url, ct);
        else if (f is null && op is null && p.Kind == WorkloadProfile.CommandKind && url is not null)
        {
            // A command profile started some other way still answers on its URL.
            var s = await ServerProbe.ProbeAsync(url, ct);
            if (s.Answering) status = s;
        }
        var running = f is not null || status is { Answering: true };
        if (!running && op is null && svc.StartedByAvamux(wid)) svc.Forget(wid); // it ended by itself
        if (status?.Context is { } seenCtx && p.Port > 0) svc.SeenContext(p.Port, seenCtx);
        if (status?.Activity is { } act && (!_activity.TryGetValue(p.Id, out var before) || before != act)) { _activity[p.Id] = act; svc.Touch(p.Id); }

        WorkloadState state;
        string? detail = null, error = null;
        if (op is { Finished: null }) { state = op.State; detail = op.Detail; }
        else if (running) { state = status is null ? WorkloadState.Running : !status.Ready ? WorkloadState.Loading : status.Busy ? WorkloadState.Busy : WorkloadState.Running; detail = status?.Detail; }
        else if (op is { Error: { } e }) { state = WorkloadState.Failed; error = e; }
        else state = WorkloadState.Stopped;

        var actions = new List<WorkloadAction>();
        switch (state)
        {
            case WorkloadState.Starting or WorkloadState.Loading or WorkloadState.Waiting when op is { Finished: null }:
                actions.Add(new(WorkloadActions.Cancel, "Cancel start")); break;
            case WorkloadState.Stopped or WorkloadState.Failed:
                actions.Add(new(WorkloadActions.Start, p.Group.Length > 0 ? "Start (swap)" : "Start"));
                if (state == WorkloadState.Failed) actions.Add(new("dismiss", "Dismiss"));
                break;
            case WorkloadState.Stopping: break;
            default:
                actions.Add(new(WorkloadActions.Stop, "Stop", svc.StartedByAvamux(wid) ? null : $"avamux did not start {p.Name}. Stop it anyway?"));
                actions.Add(new(WorkloadActions.Restart, "Restart"));
                break;
        }
        var facts = new Dictionary<string, string> { ["Profile"] = p.Kind == WorkloadProfile.ScriptKind ? "start/stop scripts" : p.Kind };
        if (p.StartScript.Length > 0) facts["Start script"] = p.StartScript;
        if (p.Model.Length > 0) facts["Model file"] = p.Model;
        if (p.RpcAddress.Length > 0) facts["RPC server"] = p.RpcAddress;
        if (p.NeedsRpc.Count > 0) facts["Uses"] = string.Join(", ", p.NeedsRpc);
        if (p.Leases.Count > 0) facts["Leases"] = string.Join(", ", p.Leases);
        if (p.IdleMinutes > 0) facts["Idle stop"] = $"after {p.IdleMinutes:0.#} minutes";
        if (status?.Slots > 0) facts["Slots"] = status.Slots.ToString();
        if (p.Notes.Length > 0) facts["Notes"] = p.Notes;
        return new WorkloadInfo
        {
            Id = id, Kind = WorkloadKind.Server, Name = p.Name, State = state, Detail = detail, Error = error,
            Type = p.Kind == WorkloadProfile.ScriptKind ? $"{(p.ServerType.Length > 0 ? p.ServerType : "llama.cpp server")} (script)" : p.Kind == WorkloadProfile.LlamaCpp ? "llama.cpp server" : "command",
            Model = status?.Model ?? (p.Alias.Length > 0 ? p.Alias : null), Endpoint = url, Port = f?.Ports.FirstOrDefault(x => x == p.Port) is > 0 ? p.Port : f?.Ports.FirstOrDefault() is > 0 and var port ? port : p.Port > 0 ? p.Port : null,
            Pid = f?.Proc.Pid, Context = status?.Context ?? (p.Context > 0 ? p.Context : null),
            VramMiB = f?.VramMiB > 0 ? f.VramMiB : null, RamMiB = f is null ? null : f.RssKiB / 1024, Started = op is { Finished: null } ? op.Started : f?.Proc.Started,
            Finished = op?.Finished, StartedBy = svc.StartedByAvamux(wid) ? "avamux" : running ? "a script or by hand" : null,
            ByAvamux = running && svc.StartedByAvamux(wid), Folder = p.Folder.Length > 0 ? p.Folder : null, ProfileId = p.Id, Actions = actions, Facts = facts,
        };
    }

    private async Task<WorkloadInfo?> ProcessAsync(Found f, Dictionary<int, ProcInfo> all, int me, CancellationToken ct)
    {
        var p = f.Proc;
        var parentIsMe = p.ParentPid == me;
        var facts = new Dictionary<string, string> { ["Command"] = p.CommandLine.Length > 400 ? p.CommandLine[..400] + "…" : p.CommandLine };
        string? model = p.Arg("--alias", "-a") ?? (p.Arg("--model", "-m") is { } m ? Path.GetFileName(m) : null);
        string name;
        string? startedBy = parentIsMe ? "avamux" : null;
        ServerStatus? status = null;
        int? port = f.Ports.Count > 0 ? f.Ports[0] : null;
        if (f.Kind.Kind == WorkloadKind.Agent)
        {
            var folder = p.Cwd is { } cwd ? Path.GetFileName(cwd.TrimEnd('/')) : null;
            name = f.Kind.Type + (folder is { Length: > 0 } ? " in " + folder : "");
            if (p.Arg("--session-id", "--resume") is { } session) facts["Session"] = session;
            if (p.Args.Contains("--print") || p.Args.Contains("-p")) facts["Mode"] = "one-off (--print)";
            if (p.Args.Contains("--mode") && p.Arg("--mode") == "rpc") facts["Mode"] = "driven by a program (rpc)";
            if (ProcessScanner.EnvironmentVariable(p.Pid, "AVAMUX_TERMINAL_ID") is { Length: > 0 } tab) { facts["Terminal"] = "an avamux terminal"; startedBy ??= "you, in an avamux terminal"; facts["Terminal id"] = tab; }
            if (parentIsMe) startedBy = "avamux (AI Chat or an AI Action)";
            if (p.Arg("--model") is { } am) model = am;
        }
        else
        {
            name = f.Kind.Type + (model is { Length: > 0 } ? " · " + model : port is { } x ? $" on :{x}" : "");
            if (f.Kind.Kind == WorkloadKind.Server && port is { } sp)
            {
                status = await ServerProbe.ProbeAsync(ServerProbe.LocalUrl(sp), ct);
                if (status.Context is { } seen) svc.SeenContext(sp, seen);
            }
            if (f.Kind.Program == "kokoro" && parentIsMe) name = "Kokoro (avamux's speech)";
            if (ProcessScanner.OutputFile(p.Pid) is { } log) facts["Log"] = log;
            if (all.TryGetValue(p.ParentPid, out var parent) && parent.Name is "bash" or "sh" && parent.Args.Skip(1).FirstOrDefault() is { } script && script.EndsWith(".sh", StringComparison.Ordinal))
                startedBy = Path.GetFileName(script);
        }
        var state = status is null ? WorkloadState.Running : !status.Answering ? WorkloadState.Running : !status.Ready ? WorkloadState.Loading : status.Busy ? WorkloadState.Busy : WorkloadState.Running;
        var what = f.Kind.Kind == WorkloadKind.Agent ? "this agent" : "this program";
        var confirm = parentIsMe ? null : $"avamux did not start {what} ({p.Name}, pid {p.Pid}). Stop it anyway?" + (f.Kind.Kind == WorkloadKind.Agent ? " Anything it is doing is lost." : "");
        return new WorkloadInfo
        {
            Id = PidPrefix + p.Pid, Kind = f.Kind.Kind, Name = name, Type = f.Kind.Type, State = state, Detail = status?.Detail,
            Model = status?.Model ?? model, Endpoint = port is { } u ? ServerProbe.LocalUrl(u) : null, Port = port, Pid = p.Pid, Context = status?.Context ?? (long.TryParse(p.Arg("--ctx-size", "-c"), out var c) ? c : null),
            VramMiB = f.VramMiB > 0 ? f.VramMiB : null, RamMiB = f.RssKiB / 1024, Started = p.Started, StartedBy = startedBy, Folder = p.Cwd,
            Actions = [new WorkloadAction(WorkloadActions.Stop, "Stop", confirm, Danger: !parentIsMe)], Facts = facts,
        };
    }

    // ---- RPC servers and APIs ----

    private async Task<WorkloadInfo?> RpcAsync(WorkloadProfile p, List<Found> found, HashSet<int> claimed, CancellationToken ct)
    {
        var (up, detail) = await RpcStatusAsync(p, ct, maxAge: TimeSpan.FromSeconds(20));
        var op = svc.OperationOf(p.Id);
        var state = op is { Finished: null } ? op.State : up ? WorkloadState.Running : op is { Error: not null } ? WorkloadState.Failed : WorkloadState.Stopped;
        var actions = new List<WorkloadAction>();
        if (!up && p.StartCommand.Length > 0) actions.Add(new(WorkloadActions.Start, "Start"));
        if (up && p.StopCommand.Length > 0) actions.Add(new(WorkloadActions.Stop, "Stop", "Stop the RPC server? Servers here that use it will fail."));
        if (p.RestartCommand.Length > 0) actions.Add(new(WorkloadActions.Restart, "Restart"));
        if (p.VersionCommand.Length > 0 || p.StatusCommand.Length > 0) actions.Add(new("check", "Check"));
        _ = found; _ = claimed;
        return new WorkloadInfo
        {
            Id = ProfilePrefix + p.Id, Kind = WorkloadKind.RpcServer, Name = p.Name, State = state, Type = "llama.cpp RPC server (remote)",
            Detail = op is { Finished: null } ? op.Detail : detail, Error = op?.Error, Endpoint = p.RpcAddress, ProfileId = p.Id, Actions = actions,
            Facts = new Dictionary<string, string> { ["Address"] = p.RpcAddress, ["Leases"] = p.Leases.Count > 0 ? string.Join(", ", p.Leases) : "none" },
        };
    }

    private async Task<(bool Up, string? Detail)> RpcStatusAsync(WorkloadProfile p, CancellationToken ct, TimeSpan maxAge)
    {
        if (_remote.TryGetValue(p.Id, out var c) && DateTime.UtcNow - c.At < maxAge) return (c.Up, c.Detail);
        // Only the port is tried here: this runs every few seconds while the page is open, and a status command (ssh into
        // another machine, WSL) can wake what it asks about. "Check" runs it.
        var up = await ReachableAsync(p.RpcAddress, ct);
        string? detail = up ? "Answering" : "Not answering on " + p.RpcAddress;
        _remote[p.Id] = (DateTime.UtcNow, up, detail);
        return (up, detail);
    }

    private static string? Last(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    internal static async Task<bool> ReachableAsync(string hostPort, CancellationToken ct)
    {
        var i = hostPort.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(hostPort[(i + 1)..], out var port)) return false;
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(hostPort[..i], port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly ConcurrentDictionary<string, (DateTime At, WorkloadInfo Info)> _apis = new();

    private async Task<WorkloadInfo?> ApiAsync(WorkloadProfile p, CancellationToken ct)
    {
        if (p.Hidden) return null;
        if (_apis.TryGetValue(p.Id, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Info;
        var key = p.ApiKeyEnv.Length > 0 ? Environment.GetEnvironmentVariable(p.ApiKeyEnv) : null;
        WorkloadState state;
        string detail;
        var models = 0;
        if (p.ApiKeyEnv.Length > 0 && string.IsNullOrEmpty(key)) { state = WorkloadState.Stopped; detail = $"${p.ApiKeyEnv} is not set in avamux's environment"; }
        else
        {
            try
            {
                var url = p.Url.TrimEnd('/') + (p.ApiStyle == "ollama" ? "/api/tags" : "/models");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (key is not null)
                {
                    if (p.ApiStyle == "anthropic") { req.Headers.Add("x-api-key", key); req.Headers.Add("anthropic-version", "2023-06-01"); }
                    else req.Headers.Authorization = new("Bearer", key);
                }
                using var r = await Http.SendAsync(req, ct);
                var body = await r.Content.ReadAsStringAsync(ct);
                if (r.IsSuccessStatusCode && JsonUtil.Parse(body) is { } node && (node["data"] ?? node["models"]) is System.Text.Json.Nodes.JsonArray a) models = a.Count;
                state = r.IsSuccessStatusCode ? WorkloadState.Running : WorkloadState.Failed;
                detail = r.IsSuccessStatusCode ? $"Answering · {models} models" : $"HTTP {(int)r.StatusCode}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { state = WorkloadState.Failed; detail = "Not reachable: " + ex.Message; }
        }
        var info = new WorkloadInfo
        {
            Id = ProfilePrefix + p.Id, Kind = WorkloadKind.Endpoint, Name = p.Name, State = state, Type = $"API ({p.ApiStyle})", Detail = detail, Endpoint = p.Url, ProfileId = p.Id,
            Actions = [new("check", "Check now")], Facts = new Dictionary<string, string> { ["Key"] = p.ApiKeyEnv.Length > 0 ? "$" + p.ApiKeyEnv : "none" },
        };
        _apis[p.Id] = (DateTime.UtcNow, info);
        return info;
    }

    // ---- actions ----

    public async Task<string> ActAsync(string workloadId, string action, CancellationToken ct = default)
    {
        if (workloadId.StartsWith(ProfilePrefix, StringComparison.Ordinal))
        {
            var p = svc.GetProfile(workloadId[ProfilePrefix.Length..]);
            if (p.Kind == WorkloadProfile.Rpc) return await RpcActAsync(p, action, ct);
            if (p.Kind == WorkloadProfile.Api) { _apis.TryRemove(p.Id, out _); return "Checked."; }
            switch (action)
            {
                case WorkloadActions.Start: return await svc.StartAsync(p.Id, ct: ct);
                case WorkloadActions.Stop: return await svc.StopProfileAsync(p.Id, ct);
                case WorkloadActions.Restart: await svc.StopProfileAsync(p.Id, ct); return await svc.StartAsync(p.Id, new StartOptions { StopOthers = true }, ct);
                case WorkloadActions.Cancel: return svc.CancelStart(p.Id) ? "Cancelling the start." : "It is not starting.";
                case "dismiss": svc.Dismiss(p.Id); return "Dismissed.";
            }
        }
        else if (workloadId.StartsWith(PidPrefix, StringComparison.Ordinal) && int.TryParse(workloadId[PidPrefix.Length..], out var pid) && action == WorkloadActions.Stop)
        {
            var proc = ProcessScanner.List().FirstOrDefault(x => x.Pid == pid) ?? throw new InvalidOperationException($"pid {pid} has already ended.");
            if (ProcessScanner.Classify(proc) is { Program: "kokoro" } && proc.ParentPid == Environment.ProcessId)
            {
                await svc.App.Speech.ShutdownAsync();
                return "Kokoro is stopped (it starts again the next time something is spoken).";
            }
            return await ProcessScanner.StopAsync(pid, TimeSpan.FromSeconds(30), ct) ? $"{proc.Name} (pid {pid}) is stopped." : $"{proc.Name} (pid {pid}) did not stop.";
        }
        else if (workloadId.StartsWith(OllamaPrefix, StringComparison.Ordinal) && action == WorkloadActions.Unload)
        {
            await ServerProbe.OllamaUnloadAsync(OllamaUrl, workloadId[OllamaPrefix.Length..], ct);
            return "Unloaded.";
        }
        throw new InvalidOperationException($"Cannot {action} {workloadId}.");
    }

    private async Task<string> RpcActAsync(WorkloadProfile p, string action, CancellationToken ct)
    {
        var command = action switch
        {
            WorkloadActions.Start => p.StartCommand, WorkloadActions.Stop => p.StopCommand, WorkloadActions.Restart => p.RestartCommand, "check" => "",
            _ => throw new InvalidOperationException($"Cannot {action} an RPC server."),
        };
        _remote.TryRemove(p.Id, out _);
        if (action == "check")
        {
            var lines = new List<string> { await ReachableAsync(p.RpcAddress, ct) ? $"It answers on {p.RpcAddress}." : $"Nothing answers on {p.RpcAddress}." };
            if (p.StatusCommand.Length > 0)
            {
                var st = await Shell.RunAsync(p.StatusCommand, timeout: TimeSpan.FromSeconds(30), ct: ct);
                lines.Add(st.ExitCode == 0 ? "Its service is running." : "Its service is not running" + (Last(st.Stdout + st.Stderr) is { } l ? $" ({l})." : "."));
            }
            if (p.VersionCommand.Length > 0)
            {
                var user = svc.Profiles.FirstOrDefault(x => x.NeedsRpc.Contains(p.Id) && x.Binary.Length > 0);
                var comparable = user is not null && File.Exists(WorkloadService.AiPath(user.Binary));
                lines.Add(await VersionCheckAsync(p, user, ct) ?? (comparable ? $"It is built from the same llama.cpp commit as {user!.Name}'s llama-server." : "No server here that uses it names a llama-server to compare its version with."));
            }
            return string.Join(" ", lines);
        }
        if (command.Length == 0) throw new InvalidOperationException($"{p.Name} has no {action} command (set one in its profile).");
        var r = await Shell.RunAsync(command, timeout: TimeSpan.FromMinutes(2), ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException($"{action} failed: {Last(r.Stderr) ?? Last(r.Stdout) ?? "exit code " + r.ExitCode}");
        if (action is WorkloadActions.Start or WorkloadActions.Restart)
            for (var i = 0; i < 30 && !await ReachableAsync(p.RpcAddress, ct); i++) await Task.Delay(1000, ct);
        _remote.TryRemove(p.Id, out _);
        return action switch { WorkloadActions.Stop => $"{p.Name} is stopped.", _ => await ReachableAsync(p.RpcAddress, ct) ? $"{p.Name} is answering." : $"{p.Name} was started but does not answer yet." };
    }

    [GeneratedRegex(@"\b([0-9a-f]{7,40})\b")]
    private static partial Regex Commit();

    /// <summary>Compares the llama.cpp commit the RPC server was built from with the local llama-server's. Null when they match (or cannot be compared).</summary>
    internal static async Task<string?> VersionCheckAsync(WorkloadProfile rpc, WorkloadProfile? user, CancellationToken ct)
    {
        if (rpc.VersionCommand.Length == 0) return null;
        var remote = await Shell.RunAsync(rpc.VersionCommand, timeout: TimeSpan.FromSeconds(30), ct: ct);
        var remoteCommit = Commit().Match(remote.Stdout + remote.Stderr) is { Success: true } rm ? rm.Groups[1].Value : null;
        if (remoteCommit is null) return $"Could not read the RPC server's version: {Last(remote.Stderr) ?? Last(remote.Stdout) ?? "no output"}";
        var binary = user?.Binary is { Length: > 0 } b ? WorkloadService.AiPath(b) : null;
        if (binary is null || !File.Exists(binary)) return null;
        var local = await Shell.RunAsync(binary, ["--version"], env: new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = Path.GetDirectoryName(binary)! }, timeout: TimeSpan.FromSeconds(10), ct: ct);
        var localCommit = Commit().Match(local.Stdout + local.Stderr) is { Success: true } lm ? lm.Groups[1].Value : null;
        if (localCommit is null) return null;
        var same = localCommit.StartsWith(remoteCommit, StringComparison.OrdinalIgnoreCase) || remoteCommit.StartsWith(localCommit, StringComparison.OrdinalIgnoreCase);
        return same ? null : $"The RPC server was built from llama.cpp {remoteCommit} and the local llama-server from {localCommit}. RPC needs both from the same commit: rebuild one side.";
    }

    /// <summary>Makes sure an RPC server is up (starting it when it has a start command) and built from the same commit. Null when ready; else why not.</summary>
    internal async Task<string?> EnsureRpcAsync(WorkloadProfile rpc, WorkloadProfile user, CancellationToken ct)
    {
        _remote.TryRemove(rpc.Id, out _);
        if (!await ReachableAsync(rpc.RpcAddress, ct))
        {
            if (rpc.StartCommand.Length == 0) return $"{rpc.Name} does not answer on {rpc.RpcAddress}, and its profile has no start command.";
            try { await RpcActAsync(rpc, WorkloadActions.Start, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return $"{rpc.Name} could not be started: {ex.Message}"; }
            if (!await ReachableAsync(rpc.RpcAddress, ct)) return $"{rpc.Name} was started but does not answer on {rpc.RpcAddress}.";
        }
        return await VersionCheckAsync(rpc, user, ct);
    }

    // ---- logs ----

    public async Task<string> LogAsync(string workloadId, int lines = 200, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (workloadId.StartsWith(ProfilePrefix, StringComparison.Ordinal) && svc.Profile(workloadId[ProfilePrefix.Length..]) is { } p)
        {
            var server = Diagnosis.Tail(svc.LogFileOf(p), lines);
            var launch = Diagnosis.Tail(svc.LaunchLogOf(p), Math.Min(lines, 60));
            if (p.Kind == WorkloadProfile.ScriptKind && launch.Length > 0)
                return (server.Length > 0 ? server + "\n\n" : "") + "──── start/stop script output ────\n" + launch;
            return server.Length > 0 ? server : launch;
        }
        if (workloadId.StartsWith(PidPrefix, StringComparison.Ordinal) && int.TryParse(workloadId[PidPrefix.Length..], out var pid))
            return ProcessScanner.OutputFile(pid) is { } file ? Diagnosis.Tail(file, lines) : "";
        return "";
    }

    // ---- starting and stopping profiles ----

    /// <summary>Starts a profile and waits until it answers; returns its URL. Throws with the reason when it does not come up.</summary>
    internal async Task<string?> LaunchAsync(WorkloadProfile p, StartOptions options, WorkloadService.Operation op, CancellationToken ct)
    {
        var url = p.BaseUrl;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, p.StartMinutes));
        void Loading(ServerStatus s) { op.State = WorkloadState.Loading; op.Detail = s.Detail ?? (s.Answering ? "Loading the model" : "Starting"); }
        switch (p.Kind)
        {
            case WorkloadProfile.LlamaCpp:
            {
                var binary = WorkloadService.AiPath(p.Binary);
                var args = new List<string> { "--model", WorkloadService.AiPath(p.Model), "--alias", p.EffectiveAlias, "--host", p.Host.Length > 0 ? p.Host : "127.0.0.1", "--port", p.Port.ToString() };
                var ctx = options.Context ?? (p.Context > 0 ? p.Context : null);
                if (ctx is { } c) args.AddRange(["--ctx-size", c.ToString()]);
                var rpcs = p.NeedsRpc.Select(svc.Profile).OfType<WorkloadProfile>().Where(r => r.RpcAddress.Length > 0).Select(r => r.RpcAddress).ToList();
                if (rpcs.Count > 0 && !p.Args.Contains("--rpc")) args.AddRange(["--rpc", string.Join(',', rpcs)]);
                args.AddRange(p.Args);
                var env = new Dictionary<string, string>(p.Env);
                // llama.cpp builds often keep their CUDA libraries beside the program.
                if (!env.ContainsKey("LD_LIBRARY_PATH") && OperatingSystem.IsLinux()) env["LD_LIBRARY_PATH"] = Path.GetDirectoryName(binary)!;
                var log = svc.LogFileOf(p);
                var pid = await Launcher.StartDetachedAsync(binary, args, log, Path.GetDirectoryName(binary), env);
                Launcher.WritePid(svc.PidFileOf(p), pid);
                op.State = WorkloadState.Loading; op.Detail = "Loading the model";
                var problem = await ServerProbe.WaitReadyAsync(url!, () => ProcessScanner.Alive(pid), timeout, Loading, ct);
                if (problem is not null) Fail(p, problem, Diagnosis.Tail(log, 150), ProcessScanner.Alive(pid));
                return url;
            }
            case WorkloadProfile.ScriptKind:
            {
                var args = new List<string> { p.StartScript };
                if (options.Context is { } c && p.ContextArg) args.Add(c.ToString());
                args.AddRange(p.StartArgs);
                var launchLog = svc.LaunchLogOf(p);
                var launcher = await Launcher.StartDetachedAsync("/bin/bash", args, launchLog, p.Folder, null, noteExit: true);
                var until = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < until)
                {
                    await Task.Delay(1000, ct);
                    var tail = Diagnosis.Tail(launchLog, 3).Split('\n').LastOrDefault(l => l.Length > 0 && !l.StartsWith(Launcher.ExitMarker, StringComparison.Ordinal));
                    if (tail is { Length: > 0 }) { op.State = WorkloadState.Loading; op.Detail = tail.Length > 160 ? tail[..160] + "…" : tail; }
                    if (Launcher.ExitCode(launchLog) is { } code)
                    {
                        if (code != 0) Fail(p, $"its start script ended with code {code}", Diagnosis.Tail(launchLog, 60) + "\n" + Diagnosis.Tail(svc.LogFileOf(p), 80), false);
                        if (url is null) return null;
                        var left = until - DateTime.UtcNow;
                        var problem = await ServerProbe.WaitReadyAsync(url, null, left > TimeSpan.FromSeconds(30) ? left : TimeSpan.FromSeconds(30), Loading, ct);
                        if (problem is not null) Fail(p, problem, Diagnosis.Tail(svc.LogFileOf(p), 150), true);
                        return url;
                    }
                    _ = launcher;
                }
                Fail(p, $"its start script was still running after {timeout.TotalMinutes:0} minutes", Diagnosis.Tail(launchLog, 60), true);
                return url;
            }
            case WorkloadProfile.CommandKind:
            {
                var log = svc.LogFileOf(p);
                var pid = await Launcher.StartDetachedAsync("/bin/sh", ["-c", p.Command], log, p.Folder.Length > 0 ? WorkloadService.AiPath(p.Folder) : null, p.Env);
                Launcher.WritePid(svc.PidFileOf(p), pid);
                if (url is null)
                {
                    await Task.Delay(2000, ct);
                    if (!ProcessScanner.Alive(pid)) Fail(p, "it ended straight away", Diagnosis.Tail(log, 100), false);
                    return null;
                }
                var problem = await ServerProbe.WaitReadyAsync(url, () => ProcessScanner.Alive(pid), timeout, Loading, ct);
                if (problem is not null) Fail(p, problem, Diagnosis.Tail(log, 150), ProcessScanner.Alive(pid));
                return url;
            }
            default: throw new InvalidOperationException($"A {p.Kind} profile cannot be started here.");
        }
    }

    private static void Fail(WorkloadProfile p, string what, string log, bool stillRunning)
    {
        var why = Diagnosis.Explain(log) ?? Diagnosis.LastErrorLine(log);
        var message = $"{p.Name} did not start: {what}." + (why is null ? "" : " " + why) + (stillRunning ? " (It is still running; see its log.)" : "");
        throw new InvalidOperationException(message);
    }

    /// <summary>Stops a profile's server: its stop script, its stop command, or (for servers avamux launched) the process itself.</summary>
    internal async Task<string> StopProfileAsync(WorkloadProfile p, CancellationToken ct)
    {
        if (p.Kind == WorkloadProfile.Rpc) return await RpcActAsync(p, WorkloadActions.Stop, ct);
        if (p.Kind == WorkloadProfile.ScriptKind)
        {
            var r = await Shell.RunAsync("/bin/bash", [p.StopScript], p.Folder, timeout: TimeSpan.FromMinutes(2), ct: ct);
            await File.AppendAllTextAsync(svc.LaunchLogOf(p), $"\n[avamux] {DateTime.Now:yyyy-MM-dd HH:mm:ss} stopping\n{r.Stdout}{r.Stderr}", ct);
            if (r.ExitCode != 0) throw new InvalidOperationException($"Its stop script ended with code {r.ExitCode}: {Last(r.Stderr) ?? Last(r.Stdout)}");
            return Last(r.Stdout) ?? $"{p.Name} is stopped.";
        }
        if (p.StopCommand.Length > 0)
        {
            var r = await Shell.RunAsync(p.StopCommand, p.Folder.Length > 0 ? WorkloadService.AiPath(p.Folder) : null, timeout: TimeSpan.FromMinutes(2), ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException($"Its stop command ended with code {r.ExitCode}: {Last(r.Stderr) ?? Last(r.Stdout)}");
        }
        var (found, _, _) = await ScanAsync(ct);
        var match = Match(p, found, []);
        var pid = match?.Proc.Pid ?? Launcher.ReadPid(svc.PidFileOf(p));
        if (pid is { } x && !await ProcessScanner.StopAsync(x, TimeSpan.FromSeconds(30), ct)) throw new InvalidOperationException($"{p.Name} (pid {x}) did not stop.");
        try { File.Delete(svc.PidFileOf(p)); } catch { }
        return pid is null && p.StopCommand.Length == 0 ? $"{p.Name} was not running." : $"{p.Name} is stopped.";
    }
}
