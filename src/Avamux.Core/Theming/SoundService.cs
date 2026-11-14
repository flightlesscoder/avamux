using System.Diagnostics;
using Avamux.Core.Config;
using Avamux.Core.Logging;

namespace Avamux.Core.Theming;

public enum SoundEvent { EventStart, EventEnd, Timer, WatchChanged, PullRequest, AgentWaiting }

/// <summary>
/// Theme sounds. Each sound set is synthesised into WAV files under the data folder the first time it is needed,
/// and played with the desktop's own player. A file named after the event in sounds/custom/ replaces the built-in.
/// </summary>
public sealed class SoundService(AppPaths paths, ConfigService config)
{
    public static readonly string[] Sets = ["soft", "retro", "synth"];
    public const int SampleRate = 44100;
    private const int Rate = SampleRate;
    private const int SynthVersion = 2;
    private DateTime _last;

    public string Folder => Path.Combine(paths.DataDir, "sounds");
    public string CustomFolder => Path.Combine(Folder, "custom");

    private static string FileName(SoundEvent e) => e switch
    {
        SoundEvent.EventStart => "event-start", SoundEvent.EventEnd => "event-end", SoundEvent.Timer => "timer",
        SoundEvent.WatchChanged => "watch-changed", SoundEvent.PullRequest => "pull-request", _ => "agent-waiting",
    };

    public string ActiveSet()
    {
        var s = config.Settings.Theme;
        var set = s.SoundSet.Trim().ToLowerInvariant();
        return Sets.Contains(set) ? set : Themes.Find(s.Name).SoundSet;
    }

    private static bool Wanted(ThemeSettings s, SoundEvent e) => s.Sounds && e switch
    {
        SoundEvent.EventStart or SoundEvent.EventEnd => s.SoundOnEvent, SoundEvent.Timer => s.SoundOnTimer, SoundEvent.WatchChanged => s.SoundOnWatch,
        SoundEvent.PullRequest => s.SoundOnPullRequest, _ => s.SoundOnAgentWaiting,
    };

    /// <summary>Set while the boss screen is up: nothing should beep.</summary>
    public bool Quiet { get; set; }

    /// <summary>Plays the sound for an event if the settings allow it. <paramref name="force"/> is for "Test" buttons.</summary>
    public void Play(SoundEvent e, bool force = false)
    {
        if (Quiet && !force) return;
        try
        {
            var s = config.Settings.Theme;
            if (!force && !Wanted(s, e)) return;
            // Several things changing in the same moment should not become a chord of alerts.
            if (!force && DateTime.Now - _last < TimeSpan.FromMilliseconds(400)) return;
            _last = DateTime.Now;
            var file = Resolve(e);
            var volume = Math.Clamp(s.SoundVolume, 0, 100) / 100.0;
            if (volume <= 0) return;
            _ = Task.Run(() => PlayFile(file, volume));
        }
        catch (Exception ex) { Log.Warn("sound", "Could not play a sound: " + ex.Message); }
    }

    /// <summary>The file that would be played for an event: the user's own if present, else the synthesised one.</summary>
    public string Resolve(SoundEvent e)
    {
        var name = FileName(e);
        if (Directory.Exists(CustomFolder))
            foreach (var ext in new[] { ".wav", ".ogg", ".oga", ".flac", ".mp3" })
                if (File.Exists(Path.Combine(CustomFolder, name + ext))) return Path.Combine(CustomFolder, name + ext);
        var set = ActiveSet();
        var dir = Path.Combine(Folder, $"{set}-v{SynthVersion}");
        var file = Path.Combine(dir, name + ".wav");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(file, Wav(Synthesize(set, e)));
        }
        return file;
    }

    /// <summary>
    /// Plays a one-off synthesised clip at the sound volume, ignoring the per-event switches. The samples are made
    /// once and kept as clips/<paramref name="name"/>.wav; change the name when the synthesis changes.
    /// </summary>
    public void PlayClip(string name, Func<float[]> synthesize, CancellationToken cancel = default)
    {
        if (Quiet) return;
        try
        {
            var volume = Math.Clamp(config.Settings.Theme.SoundVolume, 0, 100) / 100.0;
            if (volume <= 0) return;
            var file = Path.Combine(Folder, "clips", name + ".wav");
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, Wav(synthesize()));
            }
            _ = Task.Run(() => PlayFile(file, volume, cancel));
        }
        catch (Exception ex) { Log.Warn("sound", "Could not play a clip: " + ex.Message); }
    }

    private static void PlayFile(string file, double volume, CancellationToken cancel = default)
    {
        var v = volume.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        (string Exe, string[] Args)[] players = OperatingSystem.IsMacOS()
            ? [("afplay", ["-v", v, file])]
            : [("pw-play", ["--volume=" + v, file]), ("paplay", ["--volume=" + (int)(65536 * volume), file]), ("aplay", ["-q", file])];
        foreach (var (exe, args) in players)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) continue;
                var waited = 0;
                while (!p.WaitForExit(100) && (waited += 100) < 30000)
                {
                    if (!cancel.IsCancellationRequested) continue;
                    try { p.Kill(); } catch { /* already gone */ }
                    return;
                }
                if (p.HasExited && p.ExitCode == 0) return;
            }
            catch (System.ComponentModel.Win32Exception) { /* player not installed: try the next */ }
            catch (Exception ex) { Log.Debug("sound", $"{exe}: {ex.Message}"); }
        }
    }

    // ---- synthesis ----

    private sealed record Note(double Freq, double Start, double Length, double Gain = 1);

    /// <summary>Samples in -1..1 for one event in one set. Public so tests can check every combination renders.</summary>
    public static float[] Synthesize(string set, SoundEvent e)
    {
        // Note names as frequencies.
        const double C4 = 261.63, E4 = 329.63, G4 = 392.00, A4 = 440.00, C5 = 523.25, D5 = 587.33, E5 = 659.25, G5 = 783.99, A5 = 880.00, C6 = 1046.50, E6 = 1318.51;
        Note[] notes = e switch
        {
            SoundEvent.EventStart => [new(C5, 0, .22), new(G5, .12, .34)],
            SoundEvent.EventEnd => [new(G5, 0, .2), new(C5, .12, .36)],
            SoundEvent.Timer => [new(E5, 0, .16), new(E5, .2, .16), new(E5, .4, .16), new(A5, .62, .5)],
            SoundEvent.WatchChanged => [new(A4, 0, .14), new(D5, .1, .3)],
            SoundEvent.PullRequest => [new(C5, 0, .14), new(E5, .1, .14), new(G5, .2, .14), new(C6, .3, .42)],
            _ => [new(E6, 0, .12, .8), new(C6, .16, .34, .8)],
        };
        if (set == "retro")
        {
            // A lower, squarer register: closer to a PC speaker and a sound card's chime.
            notes = e switch
            {
                SoundEvent.EventStart => [new(C4, 0, .5, .6), new(E4, 0, .5, .6), new(G4, 0, .5, .6), new(C5, 0, .5, .6)],
                SoundEvent.EventEnd => [new(G4, 0, .16), new(E4, .16, .16), new(C4, .32, .36)],
                SoundEvent.Timer => [new(A5, 0, .09), new(A5, .15, .09), new(A5, .3, .09), new(A5, .6, .09), new(A5, .75, .09), new(A5, .9, .09)],
                SoundEvent.WatchChanged => [new(A4, 0, .12), new(A4, .16, .12)],
                SoundEvent.PullRequest => [new(C5, 0, .1), new(G4, .1, .1), new(C5, .2, .1), new(E5, .3, .3)],
                _ => [new(G5, 0, .08), new(C6, .1, .2)],
            };
        }
        var length = notes.Max(n => n.Start + n.Length) + (set == "synth" ? 0.35 : 0.08);
        var buf = new float[(int)(length * Rate)];
        foreach (var n in notes)
        {
            var from = (int)(n.Start * Rate);
            var count = (int)(n.Length * Rate);
            for (var i = 0; i < count && from + i < buf.Length; i++)
            {
                var t = i / (double)Rate;
                var phase = t * n.Freq;
                var wave = set switch
                {
                    "retro" => Math.Sign(Math.Sin(2 * Math.PI * phase)) * 0.55,
                    // Two detuned saws through a falling brightness: a soft analogue pluck.
                    "synth" => (Saw(phase) + Saw(phase * 1.007) * 0.8) * 0.4 * (0.35 + 0.65 * Math.Exp(-t * 9)) + Math.Sin(2 * Math.PI * phase) * 0.35,
                    _ => Math.Sin(2 * Math.PI * phase) + 0.28 * Math.Sin(4 * Math.PI * phase) + 0.08 * Math.Sin(6 * Math.PI * phase),
                };
                var attack = Math.Min(1, t / (set == "retro" ? 0.002 : 0.008));
                var release = set == "retro" ? Math.Min(1, (n.Length - t) / 0.004) : Math.Exp(-t * (set == "synth" ? 5 : 7.5)) * Math.Min(1, (n.Length - t) / 0.02);
                buf[from + i] += (float)(wave * attack * release * n.Gain * 0.34);
            }
        }
        if (set == "synth")
        {
            // A short feedback echo.
            var delay = (int)(0.13 * Rate);
            for (var i = delay; i < buf.Length; i++) buf[i] += buf[i - delay] * 0.32f;
        }
        var peak = Math.Max(0.001f, buf.Max(Math.Abs));
        var scale = 0.8f / peak; // every sound peaks at the same level, whatever its set
        for (var i = 0; i < buf.Length; i++) buf[i] *= scale;
        return buf;
    }

    private static double Saw(double phase) => 2 * (phase - Math.Floor(phase + 0.5));

    private static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var bytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in samples) w.Write((short)(Math.Clamp(s, -1f, 1f) * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
