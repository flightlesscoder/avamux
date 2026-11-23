using System.Diagnostics;
using System.Text;
using Avamux.Core.Logging;

namespace Avamux.Core.Terminal;

public sealed record TerminalInfo(string Id, string Title, string? Name, string Process, string? OscTitle, int Pid,
    string Cwd, string Command, DateTime Created, bool Running, int? ExitCode, int Cols, int Rows, string State, string? Agent);

/// <summary>What a terminal is doing, as far as can be told from outside.</summary>
public enum TerminalState
{
    /// <summary>The shell is at its prompt.</summary>
    Idle,
    /// <summary>A program is in the foreground.</summary>
    Running,
    /// <summary>An AI agent is in the foreground and producing output.</summary>
    AgentWorking,
    /// <summary>An AI agent is in the foreground and has gone quiet (or rang the bell): it wants input.</summary>
    AgentWaiting,
    /// <summary>The program has ended.</summary>
    Exited,
}

/// <summary>One terminal tab: a pty, its process, and a bounded replay buffer of raw output.</summary>
public sealed class TerminalSession
{
    private readonly Pty _pty;
    private readonly object _gate = new();
    private readonly LinkedList<byte[]> _chunks = new();
    private long _bufferedBytes;
    private readonly long _maxBufferedBytes;
    private readonly List<Action<byte[]>> _subscribers = [];

    public string Id { get; }
    public string? Name { get; set; }
    public string Process { get; private set; }
    public string? OscTitle { get; set; }
    public string Cwd { get; }
    public string Command { get; }
    public DateTime Created { get; } = DateTime.Now;
    public bool Running { get; private set; } = true;
    public int? ExitCode { get; private set; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int Pid => _pty.Pid;
    public TerminalState State { get; private set; } = TerminalState.Idle;
    /// <summary>Name of the AI agent in the foreground, if one was recognised.</summary>
    public string? Agent { get; private set; }
    public DateTime LastOutput { get; private set; } = DateTime.Now;
    public DateTime LastInput { get; private set; }
    private DateTime _lastActivity = DateTime.Now;
    private bool _bell;

    /// <summary>Tab caption: the user's name if set, otherwise what is running inside.</summary>
    public string Title => !string.IsNullOrWhiteSpace(Name) ? Name! : Process;

    public event Action<TerminalSession, int>? Exited;

    internal TerminalSession(string id, Pty pty, string command, string cwd, string? name, int cols, int rows, long maxBufferedBytes)
    {
        Id = id; _pty = pty; Command = command; Cwd = cwd; Name = name; Cols = cols; Rows = rows;
        _maxBufferedBytes = maxBufferedBytes;
        Process = Path.GetFileName(command);
        new Thread(ReadLoop) { IsBackground = true, Name = "pty-" + id }.Start();
    }

    private static int _anyOutput;

    private void ReadLoop()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var n = _pty.Read(buf);
                if (n <= 0) break;
                var chunk = buf.AsSpan(0, n).ToArray();
                NoteOutput(DateTime.Now);
                if (Interlocked.Exchange(ref _anyOutput, 1) == 0) Diagnostics.Timeline.Note("first terminal output", Id);
                Action<byte[]>[] subs;
                lock (_gate)
                {
                    _chunks.AddLast(chunk);
                    _bufferedBytes += chunk.Length;
                    while (_bufferedBytes > _maxBufferedBytes && _chunks.Count > 1)
                    {
                        _bufferedBytes -= _chunks.First!.Value.Length;
                        _chunks.RemoveFirst();
                    }
                    subs = _subscribers.ToArray();
                }
                foreach (var s in subs) { try { s(chunk); } catch { } }
            }
        }
        catch (Exception ex) { Log.Error("terminal", $"Read loop for {Id} failed", ex); }
        var code = _pty.WaitForExit();
        ExitCode = code;
        Running = false;
        State = TerminalState.Exited;
        _pty.Dispose();
        try { Exited?.Invoke(this, code); } catch (Exception ex) { Log.Error("terminal", "Exit handler failed", ex); }
    }

    /// <summary>Atomically returns everything buffered so far and subscribes to what follows.</summary>
    public (byte[] Replay, IDisposable Subscription) Attach(Action<byte[]> onOutput)
    {
        lock (_gate)
        {
            var replay = new byte[_bufferedBytes];
            var off = 0;
            foreach (var c in _chunks) { c.CopyTo(replay, off); off += c.Length; }
            _subscribers.Add(onOutput);
            return (replay, new Unsubscribe(this, onOutput));
        }
    }

    private sealed class Unsubscribe(TerminalSession s, Action<byte[]> a) : IDisposable
    {
        public void Dispose() { lock (s._gate) s._subscribers.Remove(a); }
    }

    public byte[] Snapshot()
    {
        lock (_gate)
        {
            var all = new byte[_bufferedBytes];
            var off = 0;
            foreach (var c in _chunks) { c.CopyTo(all, off); off += c.Length; }
            return all;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!Running) return;
        LastInput = DateTime.Now;
        _bell = false;
        _pty.Write(data);
    }

    /// <summary>The program rang the bell. For an agent that means "look at me".</summary>
    public void Bell() => _bell = true;

    /// <summary>
    /// Records output. Output right after a key press is taken to be the echo of typing, not work: without that
    /// an agent would look busy for as long as the user is typing its next prompt.
    /// </summary>
    internal void NoteOutput(DateTime now)
    {
        LastOutput = now;
        if (now - LastInput > TimeSpan.FromMilliseconds(400)) _lastActivity = now;
    }
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        if (!Running || cols < 2 || rows < 1 || (cols == Cols && rows == Rows)) return;
        Cols = cols; Rows = rows;
        _pty.Resize(cols, rows);
    }

    public void Kill()
    {
        if (!Running) return;
        _pty.Signal(1); // SIGHUP, like closing a terminal window
        var pty = _pty;
        _ = Task.Delay(1500).ContinueWith(_ => { if (Running) pty.Signal(9); });
    }

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "sh", "bash", "zsh", "fish", "dash", "ksh", "tcsh", "csh", "nu", "pwsh", "elvish", "xonsh" };

    /// <summary>
    /// Refreshes <see cref="Process"/>, <see cref="Agent"/> and <see cref="State"/> from the terminal's foreground
    /// process. Returns true when any of them changed.
    /// </summary>
    public bool Refresh(IReadOnlyCollection<string> agentNames, TimeSpan agentIdle) => Refresh(agentNames, agentIdle, DateTime.Now);

    internal bool Refresh(IReadOnlyCollection<string> agentNames, TimeSpan agentIdle, DateTime now)
    {
        if (!Running)
        {
            if (State == TerminalState.Exited) return false;
            State = TerminalState.Exited;
            return true;
        }
        try
        {
            var pgid = _pty.ForegroundProcessGroup();
            if (pgid <= 0) return false;
            var name = ProcessName(pgid);
            if (string.IsNullOrEmpty(name)) return false;
            var agent = MatchAgent(name, pgid == Pid ? null : CommandLine(pgid), agentNames);
            var state = Classify(pgid == Pid && Shells.Contains(name), agent is not null, _bell, now - _lastActivity, agentIdle);
            if (name == Process && agent == Agent && state == State) return false;
            Process = name; Agent = agent; State = state;
            return true;
        }
        catch { return false; }
    }

    /// <summary>The decision itself, separated from process inspection so it can be tested.</summary>
    public static TerminalState Classify(bool shellAtPrompt, bool agent, bool bell, TimeSpan quietFor, TimeSpan agentIdle) =>
        agent ? (bell || quietFor >= agentIdle ? TerminalState.AgentWaiting : TerminalState.AgentWorking)
        : shellAtPrompt ? TerminalState.Idle : TerminalState.Running;

    /// <summary>
    /// Finds an agent name in a process name or in the first words of its command line (agents written in
    /// JavaScript or Python run as "node" or "python" with the script as an argument).
    /// </summary>
    public static string? MatchAgent(string processName, IReadOnlyList<string>? commandLine, IReadOnlyCollection<string> agentNames)
    {
        if (agentNames.Count == 0) return null;
        string? Match(string word)
        {
            var w = Path.GetFileNameWithoutExtension(word.TrimEnd('/'));
            return agentNames.FirstOrDefault(a => string.Equals(a, w, StringComparison.OrdinalIgnoreCase) || string.Equals(a, Path.GetFileName(word), StringComparison.OrdinalIgnoreCase));
        }
        if (Match(processName) is { } direct) return direct;
        if (commandLine is null) return null;
        foreach (var arg in commandLine.Take(4))
        {
            if (arg.StartsWith('-')) continue;
            if (Match(arg) is { } m) return m;
            // npm-installed tools live in …/node_modules/<package>/…, and the package is often scoped.
            var parts = arg.Split('/');
            var i = Array.LastIndexOf(parts, "node_modules");
            if (i >= 0)
                foreach (var part in parts.Skip(i + 1).Take(2))
                    if (agentNames.FirstOrDefault(a => part.Contains(a, StringComparison.OrdinalIgnoreCase) && a.Length >= 4) is { } pkg) return pkg;
        }
        return null;
    }

    private static string? ProcessName(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            var path = $"/proc/{pid}/comm";
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        return Ps(pid, "comm=") is { Length: > 0 } text ? Path.GetFileName(text).TrimStart('-') : null;
    }

    private static IReadOnlyList<string>? CommandLine(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var path = $"/proc/{pid}/cmdline";
                return File.Exists(path) ? File.ReadAllText(path).Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
            }
            return Ps(pid, "command=")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return null; }
    }

    private static string? Ps(int pid, string column)
    {
        var psi = new ProcessStartInfo("ps", $"-o {column} -p {pid}") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return null;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(1000);
        return text.Length == 0 ? null : text;
    }

    public TerminalInfo Info() => new(Id, Title, Name, Process, OscTitle, Pid, Cwd, Command, Created, Running, ExitCode, Cols, Rows, State.ToString(), Agent);
}
