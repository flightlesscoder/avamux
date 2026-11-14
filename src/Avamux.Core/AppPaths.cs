namespace Avamux.Core;

/// <summary>Well-known locations. AVAMUX_DATA_DIR / AVAMUX_HOME override them (used by tests).</summary>
public sealed class AppPaths
{
    public const string AppName = "avamux";

    public AppPaths(string? dataDir = null, string? homeDir = null, string? workingDir = null)
    {
        HomeDir = homeDir ?? Environment.GetEnvironmentVariable("AVAMUX_HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        WorkingDir = workingDir ?? Environment.CurrentDirectory;
        DataDir = dataDir ?? Environment.GetEnvironmentVariable("AVAMUX_DATA_DIR") ?? DefaultDataDir(HomeDir);
        foreach (var d in new[] { DataDir, LogsDir, AppLogsDir, CrashDir, TerminalLogsDir, EventLogsDir, WikiDir, DashboardDir, PluginsDir, ThemesDir })
            Directory.CreateDirectory(d);
    }

    private static string DefaultDataDir(string home)
    {
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", AppName);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, AppName);
    }

    public string HomeDir { get; }
    public string WorkingDir { get; }
    public string DataDir { get; }
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string AppLogsDir => Path.Combine(LogsDir, "app");
    public string CrashDir => Path.Combine(LogsDir, "crashes");
    public string TerminalLogsDir => Path.Combine(LogsDir, "terminals");
    public string EventLogsDir => Path.Combine(LogsDir, "scripts");
    public string WikiDir => Path.Combine(DataDir, "wiki");
    public string DashboardDir => Path.Combine(DataDir, "dashboard");
    public string PluginsDir => Path.Combine(DataDir, "plugins");
    /// <summary>Theme files (*.json, *.yaml, *.yml), one theme per file.</summary>
    public string ThemesDir => Path.Combine(DataDir, "themes");
    /// <summary>Files attached to events, one folder per event id.</summary>
    public string AttachmentsDir => Path.Combine(DataDir, "attachments");
    /// <summary>
    /// Control socket. Unix socket paths are limited to about 100 bytes, so a long data folder falls back to a
    /// short name in the temp folder derived from the data folder's path.
    /// </summary>
    public string SocketPath
    {
        get
        {
            var path = Path.Combine(DataDir, "avamux.sock");
            if (System.Text.Encoding.UTF8.GetByteCount(path) <= 100) return path;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(DataDir))))[..16].ToLowerInvariant();
            return Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", $"avamux-{Environment.UserName}-{hash}.sock");
        }
    }
    public string StateFile(string name) => Path.Combine(DataDir, name + ".json");

    public const string ConfigFileName = AppName + ".json";
    public const string LocalConfigFileName = AppName + ".local.json";
}
