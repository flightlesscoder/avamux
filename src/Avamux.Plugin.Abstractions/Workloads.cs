namespace Avamux.Plugins;

/// <summary>
/// Something that knows about AI workloads: model servers, queue jobs, long-running tasks. avamux has built-in ones
/// (this computer's servers and processes, its own AI tasks, containers); a plugin adds more with
/// <c>IPluginContext.AddWorkloadSource</c>, for example a queue on another machine. Everything a source lists shows on
/// the AI Workloads page and in <c>avamux ai workloads</c>.
/// </summary>
public interface IWorkloadSource
{
    /// <summary>A stable id, lower case with dashes ("local", "gpu-box"). Workload ids are prefixed with it.</summary>
    string Id { get; }
    string Name { get; }
    /// <summary>The machine it is about, for grouping ("This computer", "GPU box").</summary>
    string Machine { get; }
    /// <summary>What the source has now. Called every few seconds while someone is looking; keep it quick.</summary>
    Task<WorkloadSnapshot> ListAsync(CancellationToken ct = default);
    /// <summary>Does one of the actions a workload offers (<see cref="WorkloadInfo.Actions"/>); returns a line saying what happened.</summary>
    Task<string> ActAsync(string workloadId, string action, CancellationToken ct = default);
    /// <summary>The last lines of a workload's log or output, oldest first; empty when it has none.</summary>
    Task<string> LogAsync(string workloadId, int lines = 200, CancellationToken ct = default);
    /// <summary>Raised when something changed that should be shown before the next poll (optional).</summary>
    event Action? Changed;
}

/// <param name="Problem">Why the source could not say everything (unreachable, not set up), shown above its list.</param>
public sealed record WorkloadSnapshot(IReadOnlyList<WorkloadInfo> Items, string? Problem = null);

public enum WorkloadKind
{
    /// <summary>Serves a model over HTTP (llama.cpp, Ollama, whisper …).</summary>
    Server,
    /// <summary>Lends a GPU to another machine's server (llama.cpp's rpc-server).</summary>
    RpcServer,
    /// <summary>A job in a queue.</summary>
    Job,
    /// <summary>Work avamux itself is doing (a chat reply, an AI Action).</summary>
    Task,
    /// <summary>A coding agent (Claude Code, pi).</summary>
    Agent,
    /// <summary>A container or distrobox.</summary>
    Container,
    /// <summary>A hosted API.</summary>
    Endpoint,
    Other,
}

public enum WorkloadState { Stopped, Queued, Waiting, Starting, Loading, Running, Busy, Stopping, Done, Cancelled, Failed, Unknown }

/// <summary>Well-known action ids. A source may offer others; the page shows each with its label.</summary>
public static class WorkloadActions
{
    public const string Start = "start", Stop = "stop", Restart = "restart", Cancel = "cancel", Force = "force", Unload = "unload", Release = "release";
}

/// <param name="Confirm">Asked before the action runs (null: no question).</param>
/// <param name="Danger">Shown as a destructive action.</param>
public sealed record WorkloadAction(string Id, string Label, string? Confirm = null, bool Danger = false);

/// <summary>One workload as a source sees it. Only <see cref="Id"/>, <see cref="Name"/> and <see cref="State"/> are needed.</summary>
public sealed class WorkloadInfo
{
    /// <summary>Unique within the source.</summary>
    public string Id { get; init; } = "";
    public WorkloadKind Kind { get; init; } = WorkloadKind.Other;
    public string Name { get; init; } = "";
    public WorkloadState State { get; init; } = WorkloadState.Unknown;
    /// <summary>A line about what it is doing now ("Waiting: a game is using the graphics card").</summary>
    public string? Detail { get; init; }
    /// <summary>What it is ("llama.cpp server", "Claude Code", "queue job").</summary>
    public string? Type { get; init; }
    public string? Model { get; init; }
    /// <summary>The base URL it answers on.</summary>
    public string? Endpoint { get; init; }
    public int? Port { get; init; }
    public int? Pid { get; init; }
    public long? Context { get; init; }
    public long? VramMiB { get; init; }
    public long? RamMiB { get; init; }
    public DateTime? Started { get; init; }
    public DateTime? Finished { get; init; }
    /// <summary>0 to 1 when known.</summary>
    public double? Progress { get; init; }
    /// <summary>Who started it ("avamux", "a script", "a queue" …); null when unknown.</summary>
    public string? StartedBy { get; init; }
    /// <summary>avamux started it (it offers to stop these when it quits).</summary>
    public bool ByAvamux { get; init; }
    /// <summary>Why it failed, in plain words.</summary>
    public string? Error { get; init; }
    /// <summary>Its folder (an agent's working folder, a profile's script folder).</summary>
    public string? Folder { get; init; }
    /// <summary>The profile it was started from, when it was.</summary>
    public string? ProfileId { get; init; }
    public IReadOnlyList<WorkloadAction> Actions { get; init; } = [];
    /// <summary>Anything else worth showing, label → value.</summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Something that has to agree before a workload may use another machine's resources: a queue that must know its GPU
/// is lent out while a llama.cpp RPC server on it is in use. A profile lists the lease providers it needs.
/// </summary>
public interface IWorkloadLeaseProvider
{
    /// <summary>The id profiles name in their leases ("gpu-box").</summary>
    string Id { get; }
    string Name { get; }
    /// <summary>Waits until the lease is granted (or throws, saying why not).</summary>
    Task<IWorkloadLease> AcquireAsync(WorkloadLeaseRequest request, CancellationToken ct = default);
}

/// <param name="Label">What it is for, shown in the other machine's queue.</param>
/// <param name="Needs">How much of the machine: "exclusive" (the whole GPU) or "light".</param>
public sealed record WorkloadLeaseRequest(string Label, string Needs = "exclusive", string? Detail = null);

/// <summary>A granted lease. Dispose to release it.</summary>
public interface IWorkloadLease : IAsyncDisposable
{
    string Id { get; }
    /// <summary>Completes (with the reason) when the other side took the lease back, such as someone pressing Stop there.</summary>
    Task<string> Lost { get; }
}
