using System.Text.RegularExpressions;
using Avamux.Core.Config;
using Avamux.Core.Logging;

namespace Avamux.Core.Events;

/// <summary>Timers, scheduled tasks and calendar events. Fires start/end triggers and runs their scripts.</summary>
public sealed class EventService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly object _gate = new();
    private EventState _state;
    private Timer? _timer;
    private DateTime _lastTick;
    private int _ticking;

    public EventService(AppPaths paths, ConfigService config)
    {
        _paths = paths; _config = config;
        _state = JsonUtil.LoadFile<EventState>(paths.StateFile("events"));
        _lastTick = DateTime.Now;
    }

    public event Action? Changed;
    /// <summary>(item, phase "start"/"end", occurrence)</summary>
    public event Action<EventItem, string, Occurrence>? Fired;
    public IAppHost Host { get; set; } = new NullAppHost();
    /// <summary>Clock, replaceable in tests.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public IReadOnlyList<EventItem> Items { get { lock (_gate) return _state.Items.ToList(); } }

    public void Start() => _timer = new Timer(_ => Tick(), null, 1000, 1000);

    public EventItem? Find(string idOrTitle)
    {
        lock (_gate)
            return _state.Items.FirstOrDefault(i => i.Id == idOrTitle)
                ?? _state.Items.FirstOrDefault(i => string.Equals(i.Title, idOrTitle, StringComparison.OrdinalIgnoreCase));
    }

    public EventItem Get(string idOrTitle) => Find(idOrTitle) ?? throw new KeyNotFoundException($"No event '{idOrTitle}'.");

    public EventItem Add(EventItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Title)) throw new ArgumentException("An event needs a title.");
        if (item.End is { } end && end < item.Start) throw new ArgumentException("The end is before the start.");
        lock (_gate)
        {
            item.Id = Watching.WatchService.NewId(_state.Items.Select(i => i.Id));
            _state.Items.Add(item);
        }
        Save();
        return item;
    }

    public EventItem AddTimer(string title, TimeSpan duration, string? script) => Add(new EventItem
    {
        Title = title, Kind = EventKind.Timer, Start = Now() + duration, OnStartScript = script,
    });

    public void Remove(string idOrTitle)
    {
        var item = Get(idOrTitle);
        lock (_gate) _state.Items.Remove(item);
        try { if (item.Id.Length > 0 && Directory.Exists(AttachmentFolder(item))) Directory.Delete(AttachmentFolder(item), true); }
        catch (Exception ex) { Log.Warn("events", $"Could not remove attachments of \"{item.Title}\": {ex.Message}"); }
        Save();
    }

    // ---- attachments ----

    public string AttachmentFolder(EventItem item) => Path.Combine(_paths.AttachmentsDir, item.Id);
    public string AttachmentPath(EventItem item, string name) => PathSafety.Combine(AttachmentFolder(item), Path.GetFileName(name));

    /// <summary>Copies a file into the event's attachments folder. The event must already have an id. Call Save afterwards.</summary>
    public string Attach(EventItem item, string sourceFile)
    {
        if (item.Id.Length == 0) throw new InvalidOperationException("Add the event before attaching files to it.");
        if (!File.Exists(sourceFile)) throw new FileNotFoundException($"File not found: {sourceFile}");
        var folder = AttachmentFolder(item);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(sourceFile);
        for (var i = 1; File.Exists(Path.Combine(folder, name)); i++)
            name = $"{Path.GetFileNameWithoutExtension(sourceFile)}-{i}{Path.GetExtension(sourceFile)}";
        File.Copy(sourceFile, Path.Combine(folder, name));
        item.Attachments.Add(name);
        return name;
    }

    public void Detach(EventItem item, string name)
    {
        if (!item.Attachments.Remove(name)) return;
        try { File.Delete(AttachmentPath(item, name)); } catch { }
    }

    public void Save()
    {
        lock (_gate) JsonUtil.SaveFile(_paths.StateFile("events"), _state);
        Changed?.Invoke();
    }

    public Occurrence? NextOccurrence(EventItem item) => item.Completed || !item.Enabled ? null : RecurrenceCalculator.Next(item, Now());

    /// <summary>Everything happening in [from, to], for calendar views.</summary>
    public IReadOnlyList<(EventItem Item, Occurrence Occurrence)> Agenda(DateTime from, DateTime to)
    {
        var list = new List<(EventItem, Occurrence)>();
        foreach (var item in Items)
            foreach (var o in RecurrenceCalculator.Between(item, from, to).Take(2000))
                list.Add((item, o));
        return list.OrderBy(x => x.Item2.Start).ToList();
    }

    /// <summary>Checks for triggers in (last tick, now]. Public so tests can drive it.</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var settings = _config.Settings.Events;
            var now = Now();
            var from = _lastTick;
            _lastTick = now;
            if (!settings.Enabled) return;
            // After sleep or a clock jump, only honour triggers within the grace window.
            var grace = TimeSpan.FromSeconds(Math.Max(1, settings.MissedGraceSeconds));
            if (now - from > grace) from = now - grace;
            if (now <= from) return;

            var dirty = false;
            foreach (var item in Items.Where(i => i.Enabled && !i.Completed))
            {
                var span = item.End is { } e && e > item.Start ? e - item.Start : TimeSpan.Zero;
                foreach (var o in RecurrenceCalculator.Between(item, from - span, now).Take(1000))
                {
                    if (o.Start > from && o.Start <= now) { Fire(item, "start", o); dirty = true; }
                    if (o.End is { } end && end > from && end <= now) { Fire(item, "end", o); dirty = true; }
                }
                if (item.Recurrence.Freq == RecurrenceFreq.None && (item.End ?? item.Start) <= now) { item.Completed = true; dirty = true; }
                else if (item.Recurrence.Freq != RecurrenceFreq.None && RecurrenceCalculator.Next(item, now) is null
                         && !RecurrenceCalculator.Between(item, now, now).Any()) { item.Completed = true; dirty = true; }
            }
            if (dirty) Save();
        }
        catch (Exception ex) { Log.Error("events", "Tick failed", ex); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void Fire(EventItem item, string phase, Occurrence o)
    {
        item.LastFired = Now();
        var what = item.Kind switch { EventKind.Timer => "Timer", EventKind.Task => "Task", _ => "Event" };
        var verb = phase == "end" ? "ended" : item.Kind == EventKind.Calendar ? "started" : "triggered";
        Log.Info("events", $"{what} \"{item.Title}\" {verb}");
        try { Host.Notify($"{what} {verb}", item.Title); } catch { }
        if (_config.Settings.Events.DesktopNotifications) DesktopNotify($"{what} {verb}", item.Title);
        try { Fired?.Invoke(item, phase, o); } catch { }
        var script = phase == "end" ? item.OnEndScript : item.OnStartScript;
        if (!string.IsNullOrWhiteSpace(script)) _ = RunScriptAsync(item, phase, o, script);
    }

    /// <summary>Runs an item's script now (also used by "run now" in the UI and CLI).</summary>
    public async Task<Shell.RunResult> RunScriptAsync(EventItem item, string phase, Occurrence o, string script)
    {
        var env = new Dictionary<string, string>
        {
            ["AVAMUX_EVENT_ID"] = item.Id, ["AVAMUX_EVENT_TITLE"] = item.Title, ["AVAMUX_EVENT_KIND"] = item.Kind.ToString().ToLowerInvariant(),
            ["AVAMUX_EVENT_PHASE"] = phase, ["AVAMUX_EVENT_START"] = o.Start.ToString("O"), ["AVAMUX_EVENT_END"] = o.End?.ToString("O") ?? "",
        };
        var result = await Shell.RunAsync(script, _paths.HomeDir, env, TimeSpan.FromSeconds(Math.Max(1, _config.Settings.Events.ScriptTimeoutSeconds)));
        var safe = Regex.Replace(item.Title, "[^A-Za-z0-9._-]+", "_").Trim('_');
        var file = Path.Combine(_paths.EventLogsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}-{phase}.log");
        try
        {
            await File.WriteAllTextAsync(file, $"# event {item.Id} \"{item.Title}\" ({phase})\n# script: {script}\n# exit code: {(result.TimedOut ? "timed out" : result.ExitCode)}\n\n{result.Stdout}" +
                (result.Stderr.Length > 0 ? $"\n--- stderr ---\n{result.Stderr}" : ""));
        }
        catch { }
        item.LastRunSummary = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {phase}: " + (result.TimedOut ? "timed out" : $"exit {result.ExitCode}");
        Log.Write(result.ExitCode == 0 ? LogLevel.Info : LogLevel.Warn, "events", $"Script for \"{item.Title}\" ({phase}) finished: {(result.TimedOut ? "timed out" : "exit " + result.ExitCode)} → {Path.GetFileName(file)}");
        Save();
        return result;
    }

    public static void DesktopNotify(string title, string body)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi;
            if (OperatingSystem.IsMacOS())
            {
                psi = new("osascript");
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"display notification \"{body.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with title \"{title.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
            }
            else if (OperatingSystem.IsLinux())
            {
                psi = new("notify-send");
                foreach (var a in new[] { "-a", "avamux", "--", title, body }) psi.ArgumentList.Add(a);
            }
            else return;
            psi.UseShellExecute = false; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch { /* notifications are best effort */ }
    }

    public void Dispose() => _timer?.Dispose();
}
