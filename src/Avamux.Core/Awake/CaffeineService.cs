using System.Diagnostics;
using System.Runtime.InteropServices;
using Avamux.Core.Config;
using Avamux.Core.Logging;
using Avamux.Core.Terminal;

namespace Avamux.Core.Awake;

/// <summary>Why the computer is being kept awake.</summary>
public enum CaffeineMode
{
    Off,
    /// <summary>Until the user turns it off.</summary>
    Indefinite,
    /// <summary>Until a time, then it turns itself off.</summary>
    Timed,
    /// <summary>Whenever a terminal is running something (a command or a working AI agent), plus a grace period after.</summary>
    Terminals,
}

/// <summary>What the Caffeine page shows.</summary>
public sealed record CaffeineStatus(CaffeineMode Mode, bool Holding, DateTime? Until, IReadOnlyList<string> BusyTerminals, DateTime? GraceUntil, bool Display, string? Problem)
{
    public string Describe(DateTime now) => Mode switch
    {
        CaffeineMode.Off => "Off: the computer sleeps as usual.",
        CaffeineMode.Indefinite => "Keeping the computer awake until you turn it off.",
        CaffeineMode.Timed when Until is { } u => $"Keeping the computer awake for {Remaining(u - now)} more (until {u:HH:mm}).",
        CaffeineMode.Terminals when BusyTerminals.Count > 0 => $"Keeping the computer awake: {string.Join(", ", BusyTerminals)} {(BusyTerminals.Count == 1 ? "is" : "are")} busy.",
        CaffeineMode.Terminals when GraceUntil is { } g && g > now => $"Terminals finished; staying awake {Remaining(g - now)} more in case more work starts.",
        CaffeineMode.Terminals => "Waiting: the computer may sleep until a terminal starts working.",
        _ => "",
    };

    public static string Remaining(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }
}

/// <summary>Stops the system from sleeping (and optionally the screen from blanking) while held.</summary>
public interface ISleepInhibitor : IDisposable
{
    /// <summary>Starts holding. Returns a problem description when the platform could not do it.</summary>
    string? Hold(string why, bool display);
    void Release();
    bool Holding { get; }
}

/// <summary>
/// The Caffeine feature: keeps the computer awake until turned off, for a while, or while terminals are busy.
/// State is not saved: a restart of avamux starts with it off.
/// </summary>
public sealed class CaffeineService : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _busyTerminals;
    private readonly Func<CaffeineSettings> _settings;
    private readonly ISleepInhibitor _inhibitor;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private Timer? _timer;
    private CaffeineMode _mode;
    private DateTime? _until, _graceUntil;
    private IReadOnlyList<string> _busy = [];
    private string? _problem;
    private bool _display;

    public CaffeineService(TerminalManager terminals, Func<CaffeineSettings> settings)
        : this(() => terminals.Sessions.Where(s => s.State is TerminalState.Running or TerminalState.AgentWorking).Select(s => s.Title).ToList(), settings)
    {
        terminals.Changed += () => Tick();
    }

    /// <param name="busyTerminals">Names of the terminals that are working right now.</param>
    public CaffeineService(Func<IReadOnlyList<string>> busyTerminals, Func<CaffeineSettings> settings, ISleepInhibitor? inhibitor = null, Func<DateTime>? now = null)
    {
        _busyTerminals = busyTerminals;
        _settings = settings;
        _inhibitor = inhibitor ?? SleepInhibitor.ForPlatform();
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Raised (on a background thread) when the mode or whether it is holding changes.</summary>
    public event Action? Changed;
    /// <summary>Raised when a timed session runs out.</summary>
    public event Action? Expired;

    public CaffeineMode Mode { get { lock (_gate) return _mode; } }

    public CaffeineStatus Status
    {
        get { lock (_gate) return new CaffeineStatus(_mode, _inhibitor.Holding, _until, _busy, _graceUntil, _display, _problem); }
    }

    public void Start() => _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    public void Off() => Set(CaffeineMode.Off, null);
    public void Indefinite() => Set(CaffeineMode.Indefinite, null);
    public void For(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentException("The time must be more than zero.");
        if (duration > TimeSpan.FromDays(7)) throw new ArgumentException("That is more than a week; use 'until I turn it off' instead.");
        Set(CaffeineMode.Timed, _now() + duration);
    }
    public void UntilTime(DateTime when)
    {
        if (when <= _now()) throw new ArgumentException("That time has already passed.");
        For(when - _now());
    }
    public void WhileTerminalsBusy() => Set(CaffeineMode.Terminals, null);

    private void Set(CaffeineMode mode, DateTime? until)
    {
        lock (_gate)
        {
            _mode = mode;
            _until = until;
            _graceUntil = null;
            _problem = null;
            Log.Info("awake", mode switch
            {
                CaffeineMode.Off => "Caffeine off",
                CaffeineMode.Timed => $"Caffeine on until {until:HH:mm}",
                CaffeineMode.Terminals => "Caffeine on while terminals are busy",
                _ => "Caffeine on until turned off",
            });
        }
        Tick(force: true);
    }

    /// <summary>Re-evaluates whether to hold: called every few seconds, and when terminals change.</summary>
    public void Tick(bool force = false)
    {
        bool changed, expired = false;
        lock (_gate)
        {
            var now = _now();
            var before = (_inhibitor.Holding, _busy.Count, _graceUntil);
            if (_mode == CaffeineMode.Timed && _until is { } u && now >= u)
            {
                _mode = CaffeineMode.Off;
                _until = null;
                expired = true;
                Log.Info("awake", "Caffeine time is up");
            }
            _busy = _mode == CaffeineMode.Terminals ? _busyTerminals() : [];
            if (_mode == CaffeineMode.Terminals)
            {
                if (_busy.Count > 0) _graceUntil = now + TimeSpan.FromMinutes(Math.Clamp(_settings().GraceMinutes, 0, 240));
            }
            else _graceUntil = null;
            var want = _mode switch
            {
                CaffeineMode.Indefinite or CaffeineMode.Timed => true,
                CaffeineMode.Terminals => _busy.Count > 0 || _graceUntil > now,
                _ => false,
            };
            var display = _settings().KeepDisplayOn;
            if (want && (!_inhibitor.Holding || display != _display))
            {
                if (_inhibitor.Holding) _inhibitor.Release();
                _display = display;
                _problem = _inhibitor.Hold(_mode == CaffeineMode.Terminals ? "Terminals in avamux are busy" : "Caffeine is on in avamux", display);
                if (_problem is not null) Log.Warn("awake", _problem);
            }
            else if (!want && _inhibitor.Holding) _inhibitor.Release();
            changed = force || expired || before != (_inhibitor.Holding, _busy.Count, _graceUntil);
        }
        if (expired) Expired?.Invoke();
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _inhibitor.Dispose();
    }
}

/// <summary>The platform's way of keeping the computer awake.</summary>
public static class SleepInhibitor
{
    public static ISleepInhibitor ForPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsInhibitor()
        : OperatingSystem.IsMacOS() ? new ProcessInhibitor("caffeinate", display => display ? ["-d", "-i", "-w", Environment.ProcessId.ToString()] : ["-i", "-w", Environment.ProcessId.ToString()])
        : new ProcessInhibitor("systemd-inhibit", display =>
            ["--what=" + (display ? "idle:sleep" : "sleep"), "--who=avamux", "--why=Caffeine", "--mode=block", "sleep", "infinity"]);

    /// <summary>Holds by keeping a helper program running (systemd-inhibit on Linux, caffeinate on macOS).</summary>
    private sealed class ProcessInhibitor(string program, Func<bool, string[]> args) : ISleepInhibitor
    {
        private Process? _process;

        public bool Holding => _process is { HasExited: false };

        public string? Hold(string why, bool display)
        {
            Release();
            try
            {
                var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in args(display)) psi.ArgumentList.Add(a.Replace("--why=Caffeine", "--why=" + why));
                _process = Process.Start(psi);
                if (_process is null) return $"Could not start {program}.";
                // A helper that refuses (no permission, no logind) exits at once.
                if (_process.WaitForExit(300)) { var err = _process.StandardError.ReadToEnd().Trim(); _process = null; return $"{program} stopped straight away: {err}"; }
                return null;
            }
            catch (Exception ex) { _process = null; return $"Could not keep the computer awake: {program} is not available ({ex.Message})."; }
        }

        public void Release()
        {
            try { if (_process is { HasExited: false }) _process.Kill(); } catch { /* already gone */ }
            _process?.Dispose();
            _process = null;
        }

        public void Dispose() => Release();
    }

    /// <summary>SetThreadExecutionState from a thread that lives as long as the hold (the state belongs to the thread).</summary>
    private sealed class WindowsInhibitor : ISleepInhibitor
    {
        private const uint Continuous = 0x80000000, SystemRequired = 0x00000001, DisplayRequired = 0x00000002;
        private Thread? _thread;
        private ManualResetEventSlim? _stop;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        public bool Holding => _thread is { IsAlive: true };

        public string? Hold(string why, bool display)
        {
            Release();
            var stop = _stop = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                SetThreadExecutionState(Continuous | SystemRequired | (display ? DisplayRequired : 0));
                stop.Wait();
                SetThreadExecutionState(Continuous);
            }) { IsBackground = true, Name = "caffeine" };
            _thread.Start();
            return null;
        }

        public void Release()
        {
            _stop?.Set();
            _thread?.Join(1000);
            _thread = null;
            _stop = null;
        }

        public void Dispose() => Release();
    }
}
