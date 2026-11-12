using Avamux.Core.Config;

namespace Avamux.Core.Logging;

public sealed record LogFileInfo(string Category, string Name, string Path, long Size, DateTime Modified);

/// <summary>Browses the logs folder and enforces retention (age, count, then total size).</summary>
public sealed class LogStore
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private Timer? _timer;

    public LogStore(AppPaths paths, ConfigService config) { _paths = paths; _config = config; }

    public event Action? Changed;

    public void Start() => _timer = new Timer(_ => { try { ApplyRetention(); } catch (Exception ex) { Log.Error("logs", "Retention failed", ex); } },
        null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30));

    public IReadOnlyList<LogFileInfo> List(string? category = null)
    {
        var root = _paths.LogsDir;
        if (!Directory.Exists(root)) return [];
        return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => new LogFileInfo(CategoryOf(f.FullName), f.Name, f.FullName, f.Length, f.LastWriteTime))
            .Where(f => category is null || string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Modified).ToList();
    }

    private string CategoryOf(string path)
    {
        var rel = Path.GetRelativePath(_paths.LogsDir, path);
        var i = rel.IndexOf(Path.DirectorySeparatorChar);
        return i < 0 ? "app" : rel[..i];
    }

    public string Resolve(string nameOrPath)
    {
        var match = List().FirstOrDefault(f => f.Path == nameOrPath || f.Name == nameOrPath
            || string.Equals($"{f.Category}/{f.Name}", nameOrPath, StringComparison.Ordinal));
        return match?.Path ?? throw new FileNotFoundException($"No log file named '{nameOrPath}'.");
    }

    public string Read(string nameOrPath, int? tailLines = null)
    {
        var path = Resolve(nameOrPath);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const long cap = 8 * 1024 * 1024;
        if (fs.Length > cap) fs.Seek(-cap, SeekOrigin.End);
        using var reader = new StreamReader(fs);
        var text = reader.ReadToEnd();
        if (tailLines is int n)
        {
            var lines = text.Split('\n');
            text = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - n - 1)));
        }
        return text;
    }

    public void Delete(string nameOrPath) { File.Delete(Resolve(nameOrPath)); Changed?.Invoke(); }

    public int Clear(string? category)
    {
        var n = 0;
        foreach (var f in List(category)) { try { File.Delete(f.Path); n++; } catch { } }
        Changed?.Invoke();
        return n;
    }

    public long TotalSize() => List().Sum(f => f.Size);

    /// <summary>Deletes files past max age, then beyond max count, then oldest-first until under the size budget.</summary>
    public int ApplyRetention()
    {
        var s = _config.Settings.Logs;
        var files = List().OrderByDescending(f => f.Modified).ToList();
        var doomed = new HashSet<string>();
        if (s.MaxAgeDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-s.MaxAgeDays);
            foreach (var f in files.Where(f => f.Modified < cutoff)) doomed.Add(f.Path);
        }
        var kept = files.Where(f => !doomed.Contains(f.Path)).ToList();
        if (s.MaxFileCount > 0) foreach (var f in kept.Skip(s.MaxFileCount)) doomed.Add(f.Path);
        kept = kept.Where(f => !doomed.Contains(f.Path)).ToList();
        var budget = (long)s.MaxTotalSizeMb * 1024 * 1024;
        if (budget > 0)
        {
            var total = kept.Sum(f => f.Size);
            for (var i = kept.Count - 1; i > 0 && total > budget; i--) { doomed.Add(kept[i].Path); total -= kept[i].Size; }
        }
        var deleted = 0;
        foreach (var p in doomed) { try { File.Delete(p); deleted++; } catch { } }
        if (deleted > 0) { Log.Info("logs", $"Retention removed {deleted} file(s)."); Changed?.Invoke(); }
        return deleted;
    }
}
