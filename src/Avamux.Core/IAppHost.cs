namespace Avamux.Core;

/// <summary>What the core needs from the UI shell. The default implementation does nothing (tests, headless).</summary>
public interface IAppHost
{
    Task SetClipboardAsync(string text);
    Task<string?> GetClipboardAsync();
    /// <summary>Switches the main window to a page (terminals, settings, watching, …).</summary>
    void Navigate(string page);
    string CurrentPage { get; }
    void Activate();
    void SelectTerminal(string id);
    string? SelectedTerminal { get; }
    void Notify(string title, string message);
    void Quit();
    void ReloadDashboard();
    void OpenDashboardDevTools();
    void OpenWikiPage(string path);
    void SelectRepository(string? id);
    /// <summary>Runs a query in the Search tab.</summary>
    void Search(string query);
    /// <summary>Opens the theme editor, on a given theme or the current one.</summary>
    void OpenThemeEditor(string? theme);
    /// <summary>Shows (true), hides (false) or toggles (null) the boss screen.</summary>
    void BossScreen(bool? show);
}

public class NullAppHost : IAppHost
{
    public string? Clipboard { get; set; }
    public virtual Task SetClipboardAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public virtual Task<string?> GetClipboardAsync() => Task.FromResult(Clipboard);
    public virtual void Navigate(string page) => CurrentPage = page;
    public string CurrentPage { get; protected set; } = "terminals";
    public virtual void Activate() { }
    public virtual void SelectTerminal(string id) => SelectedTerminal = id;
    public string? SelectedTerminal { get; protected set; }
    public virtual void Notify(string title, string message) { }
    public virtual void Quit() { }
    public virtual void ReloadDashboard() { }
    public virtual void OpenDashboardDevTools() { }
    public virtual void OpenWikiPage(string path) { }
    public virtual void SelectRepository(string? id) { }
    public virtual void Search(string query) { }
    public virtual void OpenThemeEditor(string? theme) { }
    public virtual void BossScreen(bool? show) { }
}

public static class Shell
{
    /// <summary>Opens a URL or file with the desktop's default handler.</summary>
    public static void Open(string target)
    {
        try
        {
            var psi = OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open")
                : OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer")
                : new System.Diagnostics.ProcessStartInfo("xdg-open");
            psi.ArgumentList.Add(target);
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Logging.Log.Error("shell", $"Could not open {target}", ex); }
    }

    /// <summary>Opens the file manager with a file selected (or, failing that, its folder).</summary>
    public static void ShowInFolder(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (OperatingSystem.IsMacOS()) { Start("open", "-R", path); return; }
            if (OperatingSystem.IsWindows()) { Start("explorer", "/select," + path); return; }
            // The freedesktop file manager interface selects the file; most file managers implement it.
            try
            {
                using var p = Start("dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                    "org.freedesktop.FileManager1.ShowItems", "array:string:" + new Uri(path).AbsoluteUri, "string:");
                if (p is not null && p.WaitForExit(3000) && p.ExitCode == 0) return;
            }
            catch { /* fall back to opening the folder */ }
            Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception ex) { Logging.Log.Error("shell", $"Could not show {path}", ex); }
    }

    private static System.Diagnostics.Process? Start(string file, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return System.Diagnostics.Process.Start(psi);
    }

    public sealed record RunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    /// <summary>Runs a command line through the user's shell and captures its output.</summary>
    public static async Task<RunResult> RunAsync(string commandLine, string? cwd = null, IDictionary<string, string>? env = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(commandLine);
        return await RunAsync(psi, cwd, env, timeout, ct);
    }

    public static async Task<RunResult> RunAsync(string file, IEnumerable<string> args, string? cwd = null, IDictionary<string, string>? env = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await RunAsync(psi, cwd, env, timeout, ct);
    }

    private static async Task<RunResult> RunAsync(System.Diagnostics.ProcessStartInfo psi, string? cwd, IDictionary<string, string>? env, TimeSpan? timeout, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        p.Start();
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { p.Kill(true); } catch { }
        }
        var o = await stdout.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(x => x.IsCompletedSuccessfully ? x.Result : "");
        var e = await stderr.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(x => x.IsCompletedSuccessfully ? x.Result : "");
        return new RunResult(timedOut ? -1 : p.ExitCode, o, e, timedOut);
    }
}
