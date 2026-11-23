using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avamux.Core.Config;
using Avamux.Core.Logging;

namespace Avamux.Core.Ai;

public enum SpeechState { Off, Starting, Ready, Speaking }

/// <summary>
/// Speaks text aloud with Kokoro: a Python helper keeps the model loaded and makes
/// a WAV file per sentence while the one before plays, so speech starts after one sentence's work. It is off until
/// <c>ai.tts.enabled</c> is switched on; switching it off (or quitting) ends the helper and anything playing. Text
/// after a line that is only "---" is not spoken, and code blocks are skipped.
/// </summary>
public sealed partial class SpeechService : IAsyncDisposable
{
    private readonly AppServices _app;
    private readonly object _gate = new();
    private readonly Queue<(long Id, string Wav)> _ready = new();
    private readonly HashSet<long> _expected = [];
    private JsonLinesProcess? _helper;
    private TaskCompletionSource<string?>? _starting;
    private Process? _player;
    private Task? _playing;
    private Timer? _idle;
    private long _next, _generation;
    private string _settingsKey = "";
    private List<string> _voices = [];

    public SpeechService(AppServices app)
    {
        _app = app;
        HelperPath = Path.Combine(app.Paths.DataDir, "ai", "kokoro_helper.py");
        WriteHelper();
        app.Config.Changed += OnConfigChanged;
        _settingsKey = Key(Settings);
    }

    private TtsSettings Settings => _app.Config.Settings.Ai.Tts;
    private static string Key(TtsSettings s) => $"{s.Python}|{s.Model}|{s.Voices}";
    public string HelperPath { get; }

    public SpeechState State { get; private set; }
    /// <summary>What is being spoken now: the tag given to <see cref="Speak"/> (a chat message's id, say).</summary>
    public string? SpeakingTag { get; private set; }
    public event Action? Changed;
    /// <summary>Raised after each sentence's file was played (tests).</summary>
    public event Action<string>? Played;
    public IReadOnlyList<string> Voices { get { lock (_gate) return [.. _voices]; } }
    public string? LastError { get; private set; }

    private void SetState(SpeechState state, string? tag = null)
    {
        State = state;
        SpeakingTag = state == SpeechState.Speaking ? tag ?? SpeakingTag : null;
        Changed?.Invoke();
    }

    public bool Enabled => Settings.Enabled;

    /// <summary>What is missing before Kokoro can run (empty when it is set up).</summary>
    public List<string> Problems()
    {
        var s = Settings;
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Python)) problems.Add("ai.tts.python: the Python that has kokoro_onnx installed is not set.");
        else if (!File.Exists(AiService.ExpandHome(s.Python))) problems.Add($"ai.tts.python: {s.Python} does not exist.");
        if (string.IsNullOrWhiteSpace(s.Model)) problems.Add("ai.tts.model: the Kokoro model file (kokoro-v1.0.onnx) is not set.");
        else if (!File.Exists(AiService.ExpandHome(s.Model))) problems.Add($"ai.tts.model: {s.Model} does not exist.");
        if (string.IsNullOrWhiteSpace(s.Voices)) problems.Add("ai.tts.voices: the voices file (voices-v1.0.bin) is not set.");
        else if (!File.Exists(AiService.ExpandHome(s.Voices))) problems.Add($"ai.tts.voices: {s.Voices} does not exist.");
        return problems;
    }

    public bool Configured => Problems().Count == 0;

    private void OnConfigChanged()
    {
        var key = Key(Settings);
        var changed = key != _settingsKey;
        _settingsKey = key;
        if (!Settings.Enabled || changed) _ = ShutdownAsync();
    }

    private void WriteHelper()
    {
        try
        {
            using var stream = typeof(SpeechService).Assembly.GetManifestResourceStream("Avamux.Core.Ai.kokoro_helper.py");
            if (stream is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(HelperPath)!);
            var text = new StreamReader(stream).ReadToEnd();
            if (!File.Exists(HelperPath) || File.ReadAllText(HelperPath) != text) File.WriteAllText(HelperPath, text);
        }
        catch (Exception ex) { Log.Warn("speech", "Could not write the Kokoro helper: " + ex.Message); }
    }

    /// <summary>Where the helper writes its WAV files: one folder per service, deleted when it shuts down.</summary>
    private string OutDir { get; } = Path.Combine(Path.GetTempPath(), $"avamux-speech-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");

    /// <summary>Starts the helper (loading the model) if it is not running. Returns an error, or null.</summary>
    public async Task<string?> StartAsync()
    {
        if (!Settings.Enabled) return "Kokoro is switched off (ai.tts.enabled).";
        if (Problems() is { Count: > 0 } problems) return string.Join("\n", problems);
        TaskCompletionSource<string?> starting;
        lock (_gate)
        {
            if (_helper is { HasExited: false } && _starting is { Task.IsCompleted: true } done) return done.Task.Result;
            if (_starting is { Task.IsCompleted: false } pending) starting = pending;
            else
            {
                starting = _starting = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var s = Settings;
                SetState(SpeechState.Starting);
                try
                {
                    _helper = new JsonLinesProcess("kokoro", AiService.ExpandHome(s.Python),
                        [HelperPath, "--model", AiService.ExpandHome(s.Model), "--voices", AiService.ExpandHome(s.Voices), "--out", OutDir],
                        Path.GetTempPath(), null, OnHelperLine, code => OnHelperExit(code));
                }
                catch (Exception ex) { starting.TrySetResult(ex.Message); SetState(SpeechState.Off); }
            }
        }
        var error = await starting.Task.WaitAsync(TimeSpan.FromSeconds(90)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "Kokoro took too long to load.");
        if (error is not null) { LastError = error; await ShutdownAsync(); }
        else _ = _helper!.SendAsync(new JsonObject { ["voices"] = true });
        return error;
    }

    private void OnHelperLine(JsonObject o)
    {
        if (o["ready"] is not null) { _starting?.TrySetResult(null); SetState(SpeechState.Ready); return; }
        if (o["voices"] is JsonArray voices) { lock (_gate) _voices = voices.Select(v => v?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList(); Changed?.Invoke(); return; }
        if (o["error"]?.GetValue<string>() is { } error)
        {
            LastError = error;
            Log.Warn("speech", "Kokoro: " + error);
            if (_starting is { Task.IsCompleted: false } s) s.TrySetResult(error);
            if (o["id"] is JsonValue idv && idv.TryGetValue<long>(out var failed)) lock (_gate) _expected.Remove(failed);
            return;
        }
        if (o["wav"]?.GetValue<string>() is { } wav && o["id"] is JsonValue v && v.TryGetValue<long>(out var id))
        {
            lock (_gate)
            {
                if (!_expected.Remove(id)) { TryDelete(wav); return; } // stopped since
                _ready.Enqueue((id, wav));
                _playing ??= Task.Run(PlayLoopAsync);
            }
        }
    }

    private void OnHelperExit(int code)
    {
        var error = _helper?.Errors is { Length: > 0 } e ? JsonLinesProcess.LastLines(e) : $"The Kokoro helper stopped (exit code {code}).";
        _starting?.TrySetResult(error);
        lock (_gate) { _helper = null; _expected.Clear(); }
        if (State != SpeechState.Off) SetState(SpeechState.Off);
    }

    /// <summary>AVAMUX_SPEECH_PLAYER replaces the player (tests set it to a program that plays nothing).</summary>
    private static string[] Players => Environment.GetEnvironmentVariable("AVAMUX_SPEECH_PLAYER") is { Length: > 0 } custom ? [custom]
        : OperatingSystem.IsMacOS() ? ["afplay"] : ["pw-play", "paplay", "aplay"];

    private async Task PlayLoopAsync()
    {
        while (true)
        {
            (long Id, string Wav) next;
            lock (_gate)
            {
                if (_ready.Count == 0)
                {
                    _playing = null;
                    if (_expected.Count == 0 && State == SpeechState.Speaking) SetState(SpeechState.Ready);
                    ResetIdle();
                    return;
                }
                next = _ready.Dequeue();
            }
            await PlayAsync(next.Wav);
            TryDelete(next.Wav);
        }
    }

    private async Task PlayAsync(string wav)
    {
        var volume = Math.Clamp(Settings.Volume, 0, 1);
        var v = volume.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var exe in Players)
        {
            var args = exe switch { "pw-play" => new[] { "--volume=" + v, wav }, "paplay" => ["--volume=" + (int)(65536 * volume), wav], "afplay" => ["-v", v, wav], "aplay" => ["-q", wav], _ => [wav] };
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                var p = Process.Start(psi);
                if (p is null) continue;
                lock (_gate) _player = p;
                await p.WaitForExitAsync();
                lock (_gate) _player = null;
                Played?.Invoke(wav);
                var ok = p.ExitCode == 0;
                p.Dispose();
                if (ok) return;
            }
            catch (System.ComponentModel.Win32Exception) { /* not installed: try the next */ }
        }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    private void ResetIdle()
    {
        var minutes = Settings.IdleMinutes;
        if (minutes <= 0) { _idle?.Dispose(); _idle = null; return; }
        (_idle ??= new Timer(_ => { if (State == SpeechState.Ready) { Log.Info("speech", "Kokoro idle: unloading the model."); _ = ShutdownAsync(); } }))
            .Change(TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Speaks text (stopping whatever is being spoken). Needs Kokoro switched on; returns an error, or null.</summary>
    public async Task<string?> SpeakAsync(string text, string? tag = null)
    {
        Stop();
        var stream = Stream(tag);
        var error = await StartAsync();
        if (error is not null) return error;
        stream.Feed(text);
        stream.Finish();
        return null;
    }

    /// <summary>Speaks text as it arrives (a reply being written): sentence by sentence, up to a "---" line.</summary>
    public Spoken Stream(string? tag = null) => new(this, Interlocked.Read(ref _generation), tag);

    internal void Say(long generation, string sentence, string? tag)
    {
        if (generation != Interlocked.Read(ref _generation)) return;
        var helper = _helper;
        if (helper is null || helper.HasExited) return;
        var id = Interlocked.Increment(ref _next);
        lock (_gate) _expected.Add(id);
        if (State != SpeechState.Speaking) SetState(SpeechState.Speaking, tag);
        _idle?.Change(Timeout.Infinite, Timeout.Infinite);
        var s = Settings;
        _ = helper.SendAsync(new JsonObject { ["say"] = sentence, ["id"] = id, ["voice"] = s.Voice, ["speed"] = s.Speed });
    }

    /// <summary>Stops speaking: what is playing, and what was waiting to be.</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        List<string> files;
        lock (_gate)
        {
            _expected.Clear();
            files = _ready.Select(r => r.Wav).ToList();
            _ready.Clear();
            try { _player?.Kill(); } catch { }
        }
        foreach (var f in files) TryDelete(f);
        if (_helper is { HasExited: false } h) _ = h.SendAsync(new JsonObject { ["cancel"] = true });
        if (State == SpeechState.Speaking) SetState(SpeechState.Ready);
        ResetIdle();
    }

    /// <summary>Ends the helper and anything playing (Kokoro switched off, its files changed, idle, or avamux quitting).</summary>
    public async Task ShutdownAsync()
    {
        Stop();
        JsonLinesProcess? helper;
        lock (_gate) { helper = _helper; _helper = null; _starting = null; }
        _idle?.Dispose(); _idle = null;
        if (helper is not null) { await helper.DisposeAsync(); Log.Info("speech", "Kokoro stopped."); }
        try { if (Directory.Exists(OutDir)) Directory.Delete(OutDir, true); } catch { }
        if (State != SpeechState.Off) SetState(SpeechState.Off);
    }

    /// <summary>The helper's process id while it runs (tests check nothing is left).</summary>
    public int? HelperPid => _helper is { HasExited: false } h ? h.Pid : null;

    public async ValueTask DisposeAsync()
    {
        _app.Config.Changed -= OnConfigChanged;
        await ShutdownAsync();
    }

    // ---- sentences ----

    [GeneratedRegex(@"(?<=[.!?…])[""')\]]*\s+|\n{2,}")]
    private static partial Regex SentenceEnd();

    /// <summary>Markdown made speakable: code blocks out, links as their text, marks gone.</summary>
    public static string Speakable(string text)
    {
        var sb = new StringBuilder();
        var inCode = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```") || line.StartsWith("~~~")) { inCode = !inCode; continue; }
            if (inCode) continue;
            if (line.StartsWith('|')) continue; // tables do not read well
            line = Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", "");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            line = Regex.Replace(line, @"`([^`]*)`", "$1");
            line = Regex.Replace(line, @"^(#+|>|[-*+]|\d+[.)])\s+", "");
            line = line.Replace("**", "").Replace("__", "").Replace("*", "").Replace("~~", "");
            line = Regex.Replace(line, @"https?://\S+", "a link");
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Text spoken as it streams in, a sentence at a time.</summary>
    public sealed class Spoken(SpeechService owner, long generation, string? tag)
    {
        private readonly StringBuilder _buffer = new();
        private int _fed;
        private bool _done;

        /// <summary>The whole text so far (not just what is new): code blocks are only known to be code once they close.</summary>
        public void Update(string fullText)
        {
            if (_done) return;
            var cut = Regex.Match(fullText, @"(^|\n)\s*---+\s*(\n|$)");
            var text = cut.Success ? fullText[..cut.Index] : fullText;
            _buffer.Clear().Append(text);
            Pump(final: cut.Success);
            if (cut.Success) _done = true;
        }

        public void Feed(string fullText) => Update(fullText);

        public void Finish() { if (_done) return; Pump(final: true); _done = true; }

        private void Pump(bool final)
        {
            var speakable = Speakable(_buffer.ToString());
            // Hold back the last, unfinished sentence (and anything inside a code block still open).
            var end = speakable.Length;
            if (!final)
            {
                var last = SentenceEnd().Matches(speakable).LastOrDefault();
                end = last is null ? 0 : last.Index + last.Length;
            }
            if (end <= _fed) return;
            var piece = speakable[_fed..end];
            _fed = end;
            foreach (var sentence in SentenceEnd().Split(piece).Select(x => x.Trim()).Where(x => x.Any(char.IsLetterOrDigit)))
                owner.Say(generation, sentence, tag);
        }
    }
}
