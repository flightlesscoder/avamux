using System.Collections.Concurrent;

namespace Avamux.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-5}] {Category}: {Message}";
}

/// <summary>Process-wide logger. Writes daily files under logs/app, rolled by size, and keeps a small in-memory tail.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static long _maxFileBytes = 10L * 1024 * 1024;
    private static readonly ConcurrentQueue<LogEntry> Tail = new();

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static bool EchoToConsole { get; set; }
    public static event Action<LogEntry>? Written;

    public static void Configure(string directory, long maxFileBytes, LogLevel minLevel)
    {
        lock (Gate) { _dir = directory; _maxFileBytes = Math.Max(64 * 1024, maxFileBytes); MinLevel = minLevel; }
    }

    public static IReadOnlyList<LogEntry> Recent() => Tail.ToArray();

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);
    public static void Error(string category, string message, Exception ex) => Write(LogLevel.Error, category, message + ": " + ex);

    public static void Write(LogLevel level, string category, string message)
    {
        if (level < MinLevel) return;
        var entry = new LogEntry(DateTime.Now, level, category, message);
        Tail.Enqueue(entry);
        while (Tail.Count > 2000 && Tail.TryDequeue(out _)) { }
        if (EchoToConsole) Console.Error.WriteLine(entry);
        lock (Gate)
        {
            if (_dir is not null)
            {
                try
                {
                    var path = CurrentFile();
                    File.AppendAllText(path, entry + Environment.NewLine);
                }
                catch { /* logging must never throw */ }
            }
        }
        try { Written?.Invoke(entry); } catch { }
    }

    private static string CurrentFile()
    {
        var baseName = $"avamux-{DateTime.Now:yyyyMMdd}";
        for (var i = 0; ; i++)
        {
            var path = Path.Combine(_dir!, i == 0 ? baseName + ".log" : $"{baseName}.{i}.log");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < _maxFileBytes) return path;
        }
    }

    /// <summary>Writes a crash report file and returns its path.</summary>
    public static string? WriteCrash(string crashDir, string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(crashDir);
            var path = Path.Combine(crashDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.WriteAllText(path,
                $"{DateTime.Now:O}\nSource: {source}\nVersion: {typeof(Log).Assembly.GetName().Version}\nOS: {Environment.OSVersion}\n" +
                $"Runtime: {Environment.Version}\n\n{ex}\n\n--- recent log ---\n{string.Join('\n', Recent().TakeLast(200))}\n");
            return path;
        }
        catch { return null; }
    }
}
