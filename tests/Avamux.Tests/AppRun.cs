using System.Diagnostics;
using System.Text.Json.Nodes;
using Avamux.Core;
using Avamux.Core.Ipc;
using Avamux.TestSupport;

namespace Avamux.Tests;

/// <summary>
/// The real app, started as its own process over a data folder, home and working folder of its own, so it never
/// meets the avamux you use. It needs a display: scripts/load-tests.sh gives it a virtual one (KWin with --virtual),
/// so no window appears on your screen.
/// </summary>
public sealed class AppRun : IDisposable
{
    public string Root { get; }
    public AppPaths Paths { get; }
    public Process? Process { get; private set; }
    /// <summary>When the process was started, by this test's clock.</summary>
    public Stopwatch SinceLaunch { get; } = new();
    public string TimelineFile => Path.Combine(Root, "timeline.json");
    private readonly bool _keep;

    /// <param name="root">An existing sandbox to start again (its data is kept), or null for a new one.</param>
    public AppRun(string? root = null)
    {
        _keep = root is not null;
        Root = root ?? Path.Combine(Path.GetTempPath(), "avamux-startup-" + Guid.NewGuid().ToString("N")[..10]);
        foreach (var d in new[] { "data", "home", "work" }) Directory.CreateDirectory(Path.Combine(Root, d));
        Paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "home"), Path.Combine(Root, "work"));
    }

    /// <summary>The built app: the newest of the Debug and Release builds of src/Avamux.</summary>
    public static string? Binary
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "avamux.slnx"))) dir = dir.Parent;
            if (dir is null) return null;
            var name = OperatingSystem.IsWindows() ? "avamux.exe" : "avamux";
            return new[] { "Debug", "Release" }.Select(c => Path.Combine(dir.FullName, "src", "Avamux", "bin", c, "net10.0", name))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
    }

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "avamux.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    public static bool HasDisplay => OperatingSystem.IsMacOS()
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public string Home(string file) => Path.Combine(Paths.HomeDir, file);

    /// <summary>Writes the home settings file (avamux.json) the app reads at start.</summary>
    public void Settings(JsonObject settings) => File.WriteAllText(Home("avamux.json"), settings.ToJsonString());

    /// <summary>Starts the app's window (or, with <paramref name="args"/>, runs the CLI and returns at once).</summary>
    public Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Binary ?? throw new InvalidOperationException("Build src/Avamux first."))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Paths.WorkingDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["AVAMUX_DATA_DIR"] = Paths.DataDir;
        psi.Environment["AVAMUX_HOME"] = Paths.HomeDir;
        psi.Environment["AVAMUX_TIMELINE_FILE"] = TimelineFile;
        var p = System.Diagnostics.Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (args.Length == 0)
        {
            Process = p;
            SinceLaunch.Restart();
        }
        return p;
    }

    public JsonNode? Send(string command, JsonObject? args = null, int timeoutMs = 10_000)
    {
        var reply = IpcClient.SendAsync(Paths.SocketPath, command, args ?? new JsonObject(), TimeSpan.FromMilliseconds(timeoutMs)).GetAwaiter().GetResult();
        if (reply is null) return null;
        if (reply["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"{command}: {reply["error"]}");
        return reply["result"];
    }

    /// <summary>Waits for a condition, checking every few milliseconds; returns the ms since launch when it held.</summary>
    public double WaitFor(string what, Func<bool> condition, int timeoutMs = 30_000)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            bool done;
            try { done = condition(); } catch (Exception) { done = false; }
            if (done) return SinceLaunch.Elapsed.TotalMilliseconds;
            if (Process is { HasExited: true }) throw new InvalidOperationException($"The app exited (code {Process.ExitCode}) while waiting for {what}.");
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"No {what} within {timeoutMs} ms.");
            Thread.Sleep(10);
        }
    }

    /// <summary>Until the app answers commands; returns the ms since launch.</summary>
    public double WaitUntilAnswering(int timeoutMs = 30_000) => WaitFor("answer to commands", () => IpcClient.IsRunningAsync(Paths.SocketPath).GetAwaiter().GetResult(), timeoutMs);

    /// <summary>The app's own timeline (see Avamux.Core.Diagnostics.Timeline).</summary>
    public List<(string Name, double Ms, string? Detail)> Timeline() =>
        (Send("app timeline") as JsonArray ?? []).OfType<JsonObject>()
            .Select(m => (m["name"]!.GetValue<string>(), m["ms"]!.GetValue<double>(), m["detail"]?.GetValue<string>())).ToList();

    /// <summary>The timeline the app wrote as it exited.</summary>
    public List<(string Name, double Ms, string? Detail)> SavedTimeline() =>
        File.Exists(TimelineFile)
            ? (JsonNode.Parse(File.ReadAllText(TimelineFile)) as JsonArray ?? []).OfType<JsonObject>()
                .Select(m => (m["name"]!.GetValue<string>(), m["ms"]!.GetValue<double>(), m["detail"]?.GetValue<string>())).ToList()
            : [];

    /// <summary>Waits for a timeline mark, optionally one noted after a given time; returns its ms.</summary>
    public double WaitForMark(string name, double after = 0, string? detail = null, int timeoutMs = 30_000)
    {
        double found = 0;
        WaitFor($"'{name}' on the timeline", () =>
        {
            var m = Timeline().FirstOrDefault(m => m.Name == name && m.Ms >= after && (detail is null || m.Detail == detail));
            if (m.Name is null) return false;
            found = m.Ms;
            return true;
        }, timeoutMs);
        return found;
    }

    /// <summary>Memory the app's process holds (resident set), in MB; Linux only, otherwise the .NET view of it.</summary>
    public double ResidentMb()
    {
        if (Process is null) return 0;
        if (OperatingSystem.IsLinux())
            foreach (var line in File.ReadLines($"/proc/{Process.Id}/status"))
                if (line.StartsWith("VmRSS:")) return double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024;
        Process.Refresh();
        return Process.WorkingSet64 / 1048576.0;
    }

    /// <summary>Asks the app to quit and waits for the process to end; returns the ms that took.</summary>
    public double Quit(int timeoutMs = 20_000)
    {
        var watch = Stopwatch.StartNew();
        try { Send("app quit", timeoutMs: 3000); } catch (Exception) { /* the reply may be cut off by the exit */ }
        if (!Process!.WaitForExit(timeoutMs)) throw new TimeoutException($"The app did not exit within {timeoutMs} ms of `app quit`.");
        return watch.Elapsed.TotalMilliseconds;
    }

    public void Dispose()
    {
        try { if (Process is { HasExited: false }) Process.Kill(entireProcessTree: true); } catch { }
        Process?.Dispose();
        if (!_keep) try { Directory.Delete(Root, true); } catch { }
    }
}
