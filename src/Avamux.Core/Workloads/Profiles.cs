using System.Text.Json.Serialization;

namespace Avamux.Core.Workloads;

/// <summary>
/// A way to start something: a llama.cpp server avamux launches itself, a start/stop script pair, any command line,
/// a remote llama.cpp RPC server, or a hosted API. Kept in ai-workloads.json in the data folder (script profiles are
/// found in the script folders each time; only what the user changed about them is kept).
/// </summary>
public sealed class WorkloadProfile
{
    public const string LlamaCpp = "llama.cpp", ScriptKind = "script", CommandKind = "command", Rpc = "rpc", Api = "api";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>llama.cpp, script, command, rpc or api.</summary>
    public string Kind { get; set; } = LlamaCpp;
    public bool Hidden { get; set; }
    public string Notes { get; set; } = "";

    // ---- llama.cpp ----
    /// <summary>The llama-server program.</summary>
    public string Binary { get; set; } = "";
    /// <summary>The model file (the first shard of a split model).</summary>
    public string Model { get; set; } = "";
    /// <summary>The name the server answers to (--alias). Empty: the profile's id.</summary>
    public string Alias { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    /// <summary>Context size (--ctx-size); 0 leaves the model's own.</summary>
    public long Context { get; set; }
    /// <summary>More arguments, one per item, added as they are.</summary>
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];

    // ---- script ----
    public string StartScript { get; set; } = "";
    public string StopScript { get; set; } = "";
    /// <summary>Arguments for the start script.</summary>
    public List<string> StartArgs { get; set; } = [];
    /// <summary>The script's first argument is the context size, so a context can be chosen when starting it.</summary>
    public bool ContextArg { get; set; }
    /// <summary>The script's pid file, when known.</summary>
    public string PidFile { get; set; } = "";
    /// <summary>The server's log file, when known.</summary>
    public string LogFile { get; set; } = "";
    /// <summary>What the script starts, for display. Empty: a llama.cpp server.</summary>
    public string ServerType { get; set; } = "";

    // ---- command ----
    /// <summary>The command line that starts it (run by sh; it should stay in the foreground).</summary>
    public string Command { get; set; } = "";
    /// <summary>The command line that stops it; empty ends the started process.</summary>
    public string StopCommand { get; set; } = "";
    /// <summary>Where it answers (http://127.0.0.1:8080). Used for health and "is it running".</summary>
    public string Url { get; set; } = "";
    public string Folder { get; set; } = "";

    // ---- rpc ----
    /// <summary>The RPC server's address (host:port) that llama-server's --rpc gets.</summary>
    public string RpcAddress { get; set; } = "";
    /// <summary>Command lines (usually ssh …) that check, start, stop and restart the remote RPC server.</summary>
    public string StatusCommand { get; set; } = "";
    public string StartCommand { get; set; } = "";
    public string RestartCommand { get; set; } = "";
    /// <summary>Prints the llama.cpp commit the remote side was built from, to compare with the local build.</summary>
    public string VersionCommand { get; set; } = "";

    // ---- api ----
    /// <summary>The environment variable holding the API key (keys are never stored by avamux).</summary>
    public string ApiKeyEnv { get; set; } = "";
    /// <summary>openai, anthropic or ollama.</summary>
    public string ApiStyle { get; set; } = "openai";

    // ---- shared ----
    /// <summary>
    /// Profiles in the same group cannot run together (they share a GPU or a port): starting one stops the others.
    /// Empty: no group. Servers on this computer default to "gpu".
    /// </summary>
    public string Group { get; set; } = "gpu";
    /// <summary>RPC profiles this one needs running first (their ids).</summary>
    public List<string> NeedsRpc { get; set; } = [];
    /// <summary>Lease providers that must agree first (see IWorkloadLeaseProvider), such as another machine's queue.</summary>
    public List<string> Leases { get; set; } = [];
    /// <summary>Stop it after this many minutes without requests, when avamux started it. 0: never.</summary>
    public double IdleMinutes { get; set; }
    /// <summary>How long to wait for it to be ready.</summary>
    public double StartMinutes { get; set; } = 15;

    /// <summary>Found in a script folder rather than made by the user.</summary>
    [JsonIgnore] public bool Discovered { get; set; }

    [JsonIgnore] public string EffectiveAlias => Alias.Length > 0 ? Alias : Id;

    /// <summary>The base URL it answers on, when it is a server here.</summary>
    [JsonIgnore]
    public string? BaseUrl => Kind switch
    {
        CommandKind or Api => Url.Length > 0 ? Url.TrimEnd('/') : null,
        Rpc => null,
        _ => Port > 0 ? $"http://{(Host is "0.0.0.0" or "" ? "127.0.0.1" : Host)}:{Port}" : null,
    };

    public WorkloadProfile Clone() => (WorkloadProfile)MemberwiseClone() is var c ? Fix(c) : null!;

    private WorkloadProfile Fix(WorkloadProfile c)
    {
        c.Args = [.. Args]; c.Env = new(Env); c.StartArgs = [.. StartArgs]; c.NeedsRpc = [.. NeedsRpc]; c.Leases = [.. Leases];
        return c;
    }
}

/// <summary>What the user changed about a discovered script profile.</summary>
public sealed class ScriptOverride
{
    public string? Name { get; set; }
    public bool? Hidden { get; set; }
    public List<string>? StartArgs { get; set; }
    public string? Group { get; set; }
    public double? IdleMinutes { get; set; }
    public List<string>? NeedsRpc { get; set; }
    public List<string>? Leases { get; set; }
    public string? Notes { get; set; }
}

/// <summary>ai-workloads.json.</summary>
public sealed class WorkloadFile
{
    public List<WorkloadProfile> Profiles { get; set; } = [];
    /// <summary>Folders searched for start-*.sh / stop-*.sh pairs. Null: the setting ai.workloadScripts.</summary>
    public List<string>? ScriptFolders { get; set; }
    public Dictionary<string, ScriptOverride> Scripts { get; set; } = [];
    /// <summary>Folders searched for model files. Null: the usual places (see ModelLibrary.DefaultFolders) and the setting ai.workloadModels.</summary>
    public List<string>? ModelFolders { get; set; }
    /// <summary>Workloads avamux started and has not seen stop (workload id → when), so it can offer to stop them when it quits.</summary>
    public Dictionary<string, DateTime> Started { get; set; } = [];
    /// <summary>The profile last started on each port, offered first when something needs that port again.</summary>
    public Dictionary<int, string> LastOnPort { get; set; } = [];
}
