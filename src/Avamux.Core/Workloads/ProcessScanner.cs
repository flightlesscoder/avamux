using System.Globalization;
using System.Runtime.InteropServices;
using Avamux.Core.Logging;

namespace Avamux.Core.Workloads;

/// <summary>A process on this computer, as far as AI Workloads cares.</summary>
public sealed record ProcInfo(int Pid, int ParentPid, string Exe, IReadOnlyList<string> Args, long RssKiB, DateTime? Started, string? Cwd)
{
    public string Name => Path.GetFileName(Args.Count > 0 ? Args[0] : Exe);
    public string CommandLine => string.Join(' ', Args);

    /// <summary>The value after <paramref name="flag"/> (<c>--port 8080</c> or <c>--port=8080</c>).</summary>
    public string? Arg(params string[] flags)
    {
        for (var i = 0; i < Args.Count; i++)
            foreach (var flag in flags)
            {
                if (Args[i] == flag && i + 1 < Args.Count) return Args[i + 1];
                if (Args[i].StartsWith(flag + "=", StringComparison.Ordinal)) return Args[i][(flag.Length + 1)..];
            }
        return null;
    }
}

/// <summary>What a process is, when it is an AI program.</summary>
/// <param name="Program">"llama-server", "claude" …: what to call it.</param>
public sealed record ProcKind(string Program, string Type, global::Avamux.Plugins.WorkloadKind Kind, int? DefaultPort = null);

/// <summary>Lists processes (/proc on Linux, ps elsewhere), recognises AI programs, and finds the ports they listen on.</summary>
public static class ProcessScanner
{
    private static readonly long BootTicks = ReadBootTime();
    private static readonly double TicksPerSecond = 100; // USER_HZ is 100 on every Linux avamux runs on

    public static IReadOnlyList<ProcInfo> List()
    {
        try { return OperatingSystem.IsLinux() ? ListProc() : ListPs(); }
        catch (Exception ex) { Log.Warn("workloads", "Could not list processes: " + ex.Message); return []; }
    }

    private static List<ProcInfo> ListProc()
    {
        var list = new List<ProcInfo>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var raw = File.ReadAllText(Path.Combine(dir, "cmdline"));
                if (raw.Length == 0) continue; // kernel threads
                var args = raw.TrimEnd('\0').Split('\0');
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                // The command name is in parentheses and may contain spaces; the fields after it are space separated.
                var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var ppid = int.Parse(rest[1], CultureInfo.InvariantCulture);
                var startTicks = long.Parse(rest[19], CultureInfo.InvariantCulture);
                var rssPages = long.Parse(rest[21], CultureInfo.InvariantCulture);
                DateTime? started = BootTicks > 0 ? DateTimeOffset.FromUnixTimeSeconds(BootTicks).LocalDateTime.AddSeconds(startTicks / TicksPerSecond) : null;
                string exe = "", cwd = "";
                try { exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget ?? ""; } catch { /* another user's process */ }
                try { cwd = new DirectoryInfo(Path.Combine(dir, "cwd")).LinkTarget ?? ""; } catch { }
                list.Add(new ProcInfo(pid, ppid, exe, args, rssPages * Environment.SystemPageSize / 1024, started, cwd.Length > 0 ? cwd : null));
            }
            catch { /* it ended while we looked */ }
        }
        return list;
    }

    private static long ReadBootTime()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return 0;
            foreach (var line in File.ReadLines("/proc/stat"))
                if (line.StartsWith("btime ", StringComparison.Ordinal)) return long.Parse(line[6..].Trim(), CultureInfo.InvariantCulture);
        }
        catch { }
        return 0;
    }

    private static List<ProcInfo> ListPs()
    {
        // ps prints the command last so it can hold spaces; arguments that themselves contain spaces are split wrongly, which is fine here.
        var psi = new System.Diagnostics.ProcessStartInfo("ps", "-axo pid=,ppid=,rss=,lstart=,command=") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        var list = new List<ProcInfo>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var ppid)) continue;
            long.TryParse(parts[2], out var rss);
            DateTime? started = DateTime.TryParseExact(string.Join(' ', parts[3..8]), "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t) ? t : null;
            var args = parts[8..];
            list.Add(new ProcInfo(pid, ppid, args[0], args, rss, started, null));
        }
        return list;
    }

    // ---- recognising AI programs ----

    private static readonly string[] AgentHelpers = ["daemon", "bg-pty-host", "bg-spare", "mcp", "--bg-spare"];

    /// <summary>
    /// Programs plugins know about, asked before the built-in list: each returns what a process is, or null when it
    /// does not know it.
    /// </summary>
    public static HookList<Func<ProcInfo, ProcKind?>> Recognisers { get; } = new();

    /// <summary>A script's interpreter (node, bun, deno, python …): the program that runs is then named by its first argument.</summary>
    public static bool IsInterpreter(string name) =>
        name is "node" or "bun" or "deno" || name.StartsWith("python", StringComparison.Ordinal);

    /// <summary>What an AI program is, or null for everything else.</summary>
    public static ProcKind? Classify(ProcInfo p)
    {
        foreach (var kind in Recognisers.Each(r => r(p), "workloads"))
            if (kind is not null) return kind;
        var name = p.Args.Count > 0 && p.Args[0].Contains(' ') && !File.Exists(p.Args[0]) ? Path.GetFileName(p.Args[0].Split(' ')[0]) : p.Name;
        var exeName = Path.GetFileName(p.Exe);
        string? second = p.Args.Count > 1 ? Path.GetFileName(p.Args[1]) : null;
        // A program that is a script (#!/usr/bin/env python3) shows as its interpreter running it.
        var scriptName = IsInterpreter(name) && second is { Length: > 0 } && !second.StartsWith('-') ? second : null;
        bool Is(params string[] names) => names.Any(n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase) || string.Equals(exeName, n, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scriptName, n, StringComparison.OrdinalIgnoreCase));
        bool Script(params string[] names) => IsInterpreter(name) && p.Args.Skip(1).Any(a => !a.StartsWith('-') && names.Any(n => Path.GetFileName(a).Equals(n, StringComparison.OrdinalIgnoreCase)));
        bool ArgsContain(string text) => p.Args.Any(a => a.Contains(text, StringComparison.OrdinalIgnoreCase));

        if (Is("llama-server", "llama-server.exe")) return new("llama-server", "llama.cpp server", global::Avamux.Plugins.WorkloadKind.Server, 8080);
        if (Is("rpc-server", "ggml-rpc-server")) return new("rpc-server", "llama.cpp RPC server", global::Avamux.Plugins.WorkloadKind.RpcServer, 50052);
        if (Is("llama-cli", "llama-mtmd-cli", "llama-bench", "llama-perplexity", "llama-quantize", "llama-embedding")) return new(name, "llama.cpp tool", global::Avamux.Plugins.WorkloadKind.Task);
        if (Is("whisper-server")) return new("whisper-server", "whisper.cpp server", global::Avamux.Plugins.WorkloadKind.Server, 8080);
        if (Is("whisper-cli", "whisper-stream")) return new(name, "whisper.cpp", global::Avamux.Plugins.WorkloadKind.Task);
        if (Is("sd-server", "sd")) return ArgsContain(".gguf") || ArgsContain("safetensors") || name == "sd-server" ? new("sd", "stable-diffusion.cpp", global::Avamux.Plugins.WorkloadKind.Server) : null;
        if (Is("ollama")) return p.Args.Contains("runner") ? null : new("ollama", "Ollama", global::Avamux.Plugins.WorkloadKind.Server, 11434);
        if (Is("koboldcpp", "llamafile", "tabbyAPI")) return new(name, name, global::Avamux.Plugins.WorkloadKind.Server, 5001);
        if (Is("llmster", "lms") && ArgsContain("server") || Is("LM Studio", "lm-studio") ) return new("lm-studio", "LM Studio", global::Avamux.Plugins.WorkloadKind.Server, 1234);
        if (Script("kokoro_helper.py")) return new("kokoro", "Kokoro (avamux's speech)", global::Avamux.Plugins.WorkloadKind.Server);
        if (IsInterpreter(name) && ArgsContain("kokoro")) return new("kokoro", "Kokoro speech", global::Avamux.Plugins.WorkloadKind.Server);
        if (IsInterpreter(name) && (ArgsContain("vllm.entrypoints") || second == "vllm")) return new("vllm", "vLLM", global::Avamux.Plugins.WorkloadKind.Server, 8000);
        if (IsInterpreter(name) && ArgsContain("mlx_lm.server")) return new("mlx", "MLX server", global::Avamux.Plugins.WorkloadKind.Server, 8080);
        if (IsInterpreter(name) && ArgsContain("ComfyUI")) return new("comfyui", "ComfyUI", global::Avamux.Plugins.WorkloadKind.Server, 8188);

        // Coding agents: Claude Code's native build runs as versions/<x.y.z>, called "claude"; pi is a node script.
        var claudeNative = p.Exe.Contains("/claude/versions/", StringComparison.Ordinal) || name == "claude";
        // Its helpers rename themselves ("claude bg-pty-host"), so the first argument can hold the subcommand too.
        var words = p.Args.Count > 0 ? p.Args[0].Split(' ').Skip(1).Concat(p.Args.Skip(1)) : [];
        if (claudeNative && !words.Any(a => AgentHelpers.Contains(a))) return new("claude", "Claude Code", global::Avamux.Plugins.WorkloadKind.Agent);
        if (IsInterpreter(name) && second is "claude" or "cli.js" && ArgsContain("claude-code")) return new("claude", "Claude Code", global::Avamux.Plugins.WorkloadKind.Agent);
        if (IsInterpreter(name) && second == "pi" || name == "pi") return new("pi", "pi coding agent", global::Avamux.Plugins.WorkloadKind.Agent);
        return null;
    }

    // ---- ports ----

    /// <summary>The TCP ports each of the given processes listens on (Linux; elsewhere nothing).</summary>
    public static Dictionary<int, List<int>> ListeningPorts(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, List<int>>();
        if (!OperatingSystem.IsLinux()) return result;
        var byInode = new Dictionary<string, int>();
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 10 || f[3] != "0A") continue; // LISTEN
                    var local = f[1];
                    var port = int.Parse(local[(local.LastIndexOf(':') + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byInode[f[9]] = port;
                }
            }
            catch { }
        }
        foreach (var pid in pids)
        {
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
                {
                    string? target;
                    try { target = new FileInfo(fd).LinkTarget; } catch { continue; }
                    if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                    if (byInode.TryGetValue(target[8..^1], out var port))
                    {
                        if (!result.TryGetValue(pid, out var ports)) result[pid] = ports = [];
                        if (!ports.Contains(port)) ports.Add(port);
                    }
                }
            }
            catch { /* not ours to look at, or gone */ }
        }
        return result;
    }

    /// <summary>Where a process writes its output, when that is a file (a server started with its output sent to a log).</summary>
    public static string? OutputFile(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var fd in new[] { 1, 2 })
        {
            try
            {
                var target = new FileInfo($"/proc/{pid}/fd/{fd}").LinkTarget;
                if (target is { Length: > 0 } && target.StartsWith('/') && !target.StartsWith("/dev/", StringComparison.Ordinal) && File.Exists(target)) return target;
            }
            catch { }
        }
        return null;
    }

    /// <summary>A variable from a process's environment (Linux, own processes only).</summary>
    public static string? EnvironmentVariable(int pid, string name)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            foreach (var entry in File.ReadAllText($"/proc/{pid}/environ").Split('\0'))
                if (entry.StartsWith(name + "=", StringComparison.Ordinal)) return entry[(name.Length + 1)..];
        }
        catch { }
        return null;
    }

    // ---- signals ----

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int SysKill(int pid, int sig);

    public const int SIGTERM = 15, SIGKILL = 9, SIGINT = 2;

    public static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        if (OperatingSystem.IsLinux())
        {
            // A zombie still answers kill(0); its state in /proc says it is done.
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
            }
            catch { return false; }
        }
        try { return SysKill(pid, 0) == 0 || Marshal.GetLastWin32Error() == 1; } catch { return false; }
    }

    public static bool Signal(int pid, int signal)
    {
        try { return SysKill(pid, signal) == 0; } catch { return false; }
    }

    /// <summary>Asks a process to end (SIGTERM), waits, and kills it (SIGKILL) if it is still there. True when it is gone.</summary>
    public static async Task<bool> StopAsync(int pid, TimeSpan grace, CancellationToken ct = default) => await WhyNotStoppedAsync(pid, grace, ct) is null;

    /// <summary>
    /// Asks a process to end (SIGTERM), waits up to <paramref name="grace"/>, then kills it (SIGKILL). Null when it is
    /// gone; otherwise why it is not, in words to show (not allowed to signal it, or it outlived SIGKILL).
    /// </summary>
    public static async Task<string?> WhyNotStoppedAsync(int pid, TimeSpan grace, CancellationToken ct = default)
    {
        if (!Alive(pid)) return null;
        if (SysKill(pid, SIGTERM) != 0 && Denied() is { } denied) return denied;
        var until = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < until)
        {
            if (!Alive(pid)) return null;
            await Task.Delay(250, ct);
        }
        if (SysKill(pid, SIGKILL) != 0 && Denied() is { } denied2) return denied2;
        for (var i = 0; i < 30 && Alive(pid); i++) await Task.Delay(100, ct);
        if (!Alive(pid)) return null;
        // SIGKILL cannot be caught: a process that survives it is stuck in the kernel (a driver call, a hung disk).
        var state = OperatingSystem.IsLinux() && Read($"/proc/{pid}/stat") is { } stat && stat.LastIndexOf(')') is var close and > 0 && close + 2 < stat.Length ? stat[close + 2] : '?';
        return state == 'D'
            ? $"it is still there after SIGTERM and SIGKILL: it is stuck in the kernel (state D, waiting on a driver or disk) and ends when that call returns"
            : $"it is still there after SIGTERM, {grace.TotalSeconds:0} seconds, and SIGKILL";
    }

    private static string? Denied()
    {
        var errno = Marshal.GetLastWin32Error();
        return errno switch
        {
            1 => "avamux is not allowed to signal it (it belongs to another user, or runs in a sandbox avamux cannot reach)",
            3 => null, // ESRCH: it ended in the meantime
            _ => $"signalling it failed (errno {errno})",
        };
    }

    private static string? Read(string path) { try { return File.ReadAllText(path); } catch { return null; } }
}
