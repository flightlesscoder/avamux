using System.Diagnostics;
using System.Globalization;

namespace Avamux.Core.Workloads;

/// <summary>
/// Starts programs so they outlive avamux: in a session of their own (setsid), output appended to a log file, stdin
/// closed. avamux keeps their pid in a file, as the user's own start scripts do, so a later avamux can find them again.
/// </summary>
public static class Launcher
{
    public const string ExitMarker = "[avamux] exit code ";

    /// <summary>Starts a program in the background; returns its pid.</summary>
    public static async Task<int> StartDetachedAsync(string program, IReadOnlyList<string> args, string logFile, string? cwd, IDictionary<string, string>? env, bool noteExit = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        await File.AppendAllTextAsync(logFile, $"\n[avamux] {DateTime.Now:yyyy-MM-dd HH:mm:ss} starting: {Quote(program)} {string.Join(' ', args.Select(Quote))}\n");
        // sh starts the program in the background and prints its pid; with noteExit it also waits and writes how it ended,
        // so a start script's exit code can be read from the log after avamux has let go of it.
        var script = noteExit
            ? "log=$1; shift; ( \"$@\" >>\"$log\" 2>&1 </dev/null; echo \"" + ExitMarker + "$?\" >>\"$log\" ) >/dev/null 2>&1 </dev/null & echo $!"
            : "log=$1; shift; \"$@\" >>\"$log\" 2>&1 </dev/null & echo $!";
        var psi = new ProcessStartInfo(OperatingSystem.IsLinux() && File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        if (psi.FileName.EndsWith("setsid", StringComparison.Ordinal)) psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(logFile);
        psi.ArgumentList.Add(program);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        // A server must not think it runs in one of avamux's terminals, or inherit avamux's own variables.
        psi.Environment.Remove("AVAMUX_TERMINAL_ID");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + program);
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync();
        var error = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (!int.TryParse(output.Trim().Split('\n').LastOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            throw new InvalidOperationException($"Could not start {Path.GetFileName(program)}: {error.Trim()}");
        return pid;
    }

    /// <summary>The exit code a <c>noteExit</c> start wrote at the end of its log, or null while it runs.</summary>
    public static int? ExitCode(string logFile)
    {
        var tail = Diagnosis.Tail(logFile, 5);
        var i = tail.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = tail[(i + ExitMarker.Length)..].Split('\n')[0].Trim();
        return int.TryParse(rest, out var code) ? code : null;
    }

    public static int? ReadPid(string? pidFile)
    {
        if (string.IsNullOrEmpty(pidFile) || !File.Exists(pidFile)) return null;
        try { return int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && ProcessScanner.Alive(pid) ? pid : null; }
        catch { return null; }
    }

    public static void WritePid(string pidFile, int pid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pidFile)!);
        File.WriteAllText(pidFile, pid.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Runs a command line (a stop script, an ssh command) and waits for it; returns its output.</summary>
    public static async Task<Shell.RunResult> RunAsync(string program, IEnumerable<string> args, string? cwd, TimeSpan timeout, CancellationToken ct = default) =>
        await Shell.RunAsync(program, args, cwd, timeout: timeout, ct: ct);

    private static string Quote(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "-_./=:,+@%".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";
}
