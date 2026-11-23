using System.Text;
using System.Text.RegularExpressions;
using Avamux.Core.Config;
using Avamux.Core.Logging;

namespace Avamux.Core.Terminal;

public sealed record TerminalStartOptions(string? Command = null, IReadOnlyList<string>? Args = null, string? Cwd = null,
    string? Name = null, int Cols = 120, int Rows = 32);

/// <summary>Owns all terminal sessions.</summary>
public sealed partial class TerminalManager : IDisposable
{
    /// <summary>
    /// The tab a terminal is shown in: its own id, or (for a pane of a split tab) the tab's. The UI sets this; without
    /// one every terminal is its own tab.
    /// </summary>
    public static Func<string, string> TabOf { get; set; } = t => t;

    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly object _gate = new();
    private readonly List<TerminalSession> _sessions = [];
    private readonly Timer _titleTimer;
    private int _nextId = 1;

    public TerminalManager(AppPaths paths, ConfigService config)
    {
        _paths = paths; _config = config;
        _titleTimer = new Timer(_ => RefreshTitles(), null, 1000, 1000);
    }

    /// <summary>Raised when the set of sessions or any title changes.</summary>
    public event Action? Changed;
    public event Action<TerminalSession>? Created;
    public event Action<TerminalSession>? Closed;

    /// <summary>
    /// Asks the terminal view for the rendered text of a session (scrollback included). Set by the web layer;
    /// used to preserve the history of dead sessions exactly as it looked.
    /// </summary>
    public Func<string, Task<string?>>? RenderedTextProvider { get; set; }

    public IReadOnlyList<TerminalSession> Sessions { get { lock (_gate) return _sessions.ToList(); } }

    public TerminalSession? Find(string idOrName)
    {
        lock (_gate)
            return _sessions.FirstOrDefault(s => s.Id == idOrName)
                ?? _sessions.FirstOrDefault(s => string.Equals(s.Name, idOrName, StringComparison.OrdinalIgnoreCase))
                ?? _sessions.FirstOrDefault(s => string.Equals(s.Title, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public TerminalSession Get(string idOrName) => Find(idOrName) ?? throw new KeyNotFoundException($"No terminal '{idOrName}'.");

    public static string DefaultShell(TerminalSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.Shell)) return s.Shell;
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(shell) && File.Exists(shell)) return shell;
        return File.Exists("/bin/zsh") && OperatingSystem.IsMacOS() ? "/bin/zsh" : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    public TerminalSession Create(TerminalStartOptions? options = null)
    {
        options ??= new TerminalStartOptions();
        var settings = _config.Settings.Terminal;
        var command = options.Command;
        var args = options.Args?.ToList() ?? [];
        if (string.IsNullOrWhiteSpace(command))
        {
            command = DefaultShell(settings);
            // A leading dash is not portable through `exec`; ask for a login shell explicitly instead.
            if (args.Count == 0 && Path.GetFileName(command) is "bash" or "zsh" or "fish") args.Add("-l");
        }
        var cwd = options.Cwd;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) cwd = _paths.HomeDir;

        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        foreach (var k in env.Keys.Where(k => k.StartsWith("AVAMUX_RELAUNCH") || k.StartsWith("WEBKIT_") || k is "TMUX" or "TMUX_PANE" or "COLUMNS" or "LINES" or "GDK_BACKEND" or "AVAMUX_TERMINAL_ID").ToList())
            env.Remove(k);
        env["TERM"] = settings.Term;
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "avamux";
        env["AVAMUX"] = "1";
        if (!env.ContainsKey("LANG") && !env.ContainsKey("LC_ALL")) env["LANG"] = "en_US.UTF-8";

        TerminalSession session;
        lock (_gate)
        {
            var id = (_nextId++).ToString();
            // Lets programs in the tab (an AI agent) say which tab they are: `avamux browser` opens its pane beside this one.
            env["AVAMUX_TERMINAL_ID"] = id;
            if (_config.Settings.Browser.SetBrowserVariable && BrowserOpener() is { } opener) env["BROWSER"] = opener;
            var pty = Pty.Spawn(command, args, cwd, env, options.Cols, options.Rows);
            // Raw replay budget: generous relative to the configured scrollback so a reconnecting view can rebuild it.
            var budget = Math.Clamp((long)settings.Scrollback * 400, 1024 * 1024, 32L * 1024 * 1024);
            session = new TerminalSession(id, pty, command, cwd, options.Name, options.Cols, options.Rows, budget);
            session.Exited += OnExited;
            _sessions.Add(session);
        }
        Log.Info("terminal", $"Started terminal {session.Id}: {command} {string.Join(' ', args)} (pid {session.Pid}) in {cwd}");
        Diagnostics.Timeline.Note("terminal started", session.Id);
        Created?.Invoke(session);
        Changed?.Invoke();
        return session;
    }

    /// <summary>
    /// A small script for $BROWSER: programs that open a web page call it with the address, and it opens in the
    /// browser pane beside their tab. Null when the avamux executable cannot be found.
    /// </summary>
    private string? BrowserOpener()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            var command = Path.GetFileNameWithoutExtension(exe) == "dotnet" && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll
                ? $"\"{exe}\" \"{dll}\"" : $"\"{exe}\"";
            var dir = Path.Combine(_paths.DataDir, "browser");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "avamux-open-url");
            var text = "#!/bin/sh\n# Written by avamux: opens web pages in the browser pane beside the calling terminal tab.\n" +
                       $"for url in \"$@\"; do {command} browser open \"$url\" --no-wait >/dev/null || exit $?; done\n";
            if (!File.Exists(file) || File.ReadAllText(file) != text)
            {
                File.WriteAllText(file, text);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return file;
        }
        catch (Exception ex) { Log.Warn("terminal", "Could not write the $BROWSER helper: " + ex.Message); return null; }
    }

    public void Rename(string idOrName, string? name)
    {
        Get(idOrName).Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Changed?.Invoke();
    }

    public void Close(string idOrName)
    {
        var s = Get(idOrName);
        if (s.Running) s.Kill(); else Remove(s);
    }

    public void Move(string idOrName, int newIndex)
    {
        lock (_gate)
        {
            var s = Get(idOrName);
            _sessions.Remove(s);
            _sessions.Insert(Math.Clamp(newIndex, 0, _sessions.Count), s);
        }
        Changed?.Invoke();
    }

    private void OnExited(TerminalSession session, int code)
    {
        Log.Info("terminal", $"Terminal {session.Id} ({session.Title}) exited with code {code}");
        _ = Task.Run(async () =>
        {
            try { if (_config.Settings.Terminal.PreserveDeadSessions) await PreserveAsync(session); }
            catch (Exception ex) { Log.Error("terminal", "Could not preserve scrollback", ex); }
            if (_config.Settings.Terminal.CloseTabOnExit) Remove(session);
            else Changed?.Invoke();
        });
    }

    private void Remove(TerminalSession session)
    {
        bool removed;
        lock (_gate) removed = _sessions.Remove(session);
        if (!removed) return;
        Closed?.Invoke(session);
        Changed?.Invoke();
    }

    private async Task PreserveAsync(TerminalSession session)
    {
        string? text = null;
        if (RenderedTextProvider is { } provider)
        {
            try { text = await provider(session.Id).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        text ??= StripAnsi(Encoding.UTF8.GetString(session.Snapshot()));
        if (string.IsNullOrWhiteSpace(text)) return;
        var safe = Regex.Replace(session.Title, "[^A-Za-z0-9._-]+", "_").Trim('_');
        if (safe.Length == 0) safe = "terminal";
        var file = Path.Combine(_paths.TerminalLogsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{session.Id}-{safe}.log");
        var header = $"# avamux terminal {session.Id} \"{session.Title}\"\n# command: {session.Command}\n# cwd: {session.Cwd}\n" +
                     $"# started: {session.Created:O}\n# ended: {DateTime.Now:O} (exit code {session.ExitCode})\n\n";
        await File.WriteAllTextAsync(file, header + text.TrimEnd() + "\n");
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[PX^_][^\x1B]*\x1B\\|[()][0-9A-Za-z]|[@-Z\\-_=>])|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex AnsiRegex();

    public static string StripAnsi(string text) => AnsiRegex().Replace(text, "").Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>Raised when an agent in a terminal stops working and starts waiting for input.</summary>
    public event Action<TerminalSession>? AgentWaiting;

    public static IReadOnlyCollection<string> AgentNames(TerminalSettings s) =>
        s.AgentProcesses.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void RefreshTitles()
    {
        var settings = _config.Settings.Terminal;
        var agents = AgentNames(settings);
        var idle = TimeSpan.FromSeconds(Math.Max(0.5, settings.AgentIdleSeconds));
        var changed = false;
        foreach (var s in Sessions)
        {
            var before = s.State;
            if (!s.Refresh(agents, idle)) continue;
            changed = true;
            if (s.State == TerminalState.AgentWaiting && before == TerminalState.AgentWorking)
            {
                try { AgentWaiting?.Invoke(s); } catch (Exception ex) { Log.Error("terminal", "Agent-waiting handler failed", ex); }
            }
        }
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        _titleTimer.Dispose();
        foreach (var s in Sessions) { try { s.Kill(); } catch { } }
    }
}
