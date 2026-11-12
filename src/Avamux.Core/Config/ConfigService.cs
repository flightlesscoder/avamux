using System.Text.Json.Nodes;
using Avamux.Core.Logging;

namespace Avamux.Core.Config;

public enum ConfigScope { Default, Home, HomeLocal, Working, WorkingLocal }

public sealed record ConfigValue(string Path, JsonNode? Value, ConfigScope Source);

/// <summary>
/// Layered settings. Precedence, lowest to highest:
/// built-in defaults, ~/avamux.json, ~/avamux.local.json, ./avamux.json, ./avamux.local.json.
/// Layers are deep-merged, so a file only needs the keys it changes.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Dictionary<ConfigScope, JsonObject> _layers = new();
    private JsonObject _effective = new();
    private Timer? _debounce;

    public ConfigService(AppPaths paths) { _paths = paths; Reload(); }

    public AppSettings Settings { get; private set; } = new();
    public event Action? Changed;

    public static readonly ConfigScope[] FileScopes = [ConfigScope.Home, ConfigScope.HomeLocal, ConfigScope.Working, ConfigScope.WorkingLocal];

    public string FileFor(ConfigScope scope) => scope switch
    {
        ConfigScope.Home => Path.Combine(_paths.HomeDir, AppPaths.ConfigFileName),
        ConfigScope.HomeLocal => Path.Combine(_paths.HomeDir, AppPaths.LocalConfigFileName),
        ConfigScope.Working => Path.Combine(_paths.WorkingDir, AppPaths.ConfigFileName),
        ConfigScope.WorkingLocal => Path.Combine(_paths.WorkingDir, AppPaths.LocalConfigFileName),
        _ => throw new ArgumentException("Defaults have no file."),
    };

    public static JsonObject Defaults() => (JsonObject)JsonUtil.ToNode(new AppSettings())!;

    public void Reload()
    {
        lock (_gate)
        {
            var layers = new Dictionary<ConfigScope, JsonObject> { [ConfigScope.Default] = Defaults() };
            var sameDir = Path.GetFullPath(_paths.HomeDir) == Path.GetFullPath(_paths.WorkingDir);
            foreach (var scope in FileScopes)
            {
                if (sameDir && scope is ConfigScope.Working or ConfigScope.WorkingLocal && sameDir) continue;
                var file = FileFor(scope);
                if (!File.Exists(file)) continue;
                try
                {
                    if (JsonUtil.Parse(File.ReadAllText(file)) is JsonObject o) layers[scope] = o;
                }
                catch (Exception ex) { Log.Error("config", $"Ignoring {file}: {ex.Message}"); }
            }
            var merged = new JsonObject();
            foreach (var scope in Enum.GetValues<ConfigScope>())
                if (layers.TryGetValue(scope, out var layer)) JsonUtil.Merge(merged, layer);
            _layers = layers;
            _effective = merged;
            try { Settings = JsonUtil.To<AppSettings>(merged) ?? new AppSettings(); }
            catch (Exception ex) { Log.Error("config", $"Invalid settings, using defaults: {ex.Message}"); Settings = new AppSettings(); }
        }
        Changed?.Invoke();
    }

    /// <summary>Watches the config files and reloads when they change on disk.</summary>
    public void Watch()
    {
        foreach (var dir in new[] { _paths.HomeDir, _paths.WorkingDir }.Distinct())
        {
            try
            {
                var w = new FileSystemWatcher(dir, "avamux*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler h = (_, _) => (_debounce ??= new Timer(_ => Reload())).Change(300, Timeout.Infinite);
                w.Changed += h; w.Created += h; w.Deleted += h; w.Renamed += (_, _) => h(w, null!);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Warn("config", $"Cannot watch {dir}: {ex.Message}"); }
        }
    }

    public JsonObject Effective() { lock (_gate) return (JsonObject)_effective.DeepClone(); }

    public JsonObject? Layer(ConfigScope scope) { lock (_gate) return _layers.TryGetValue(scope, out var l) ? (JsonObject)l.DeepClone() : null; }

    public JsonNode? Get(string path) { lock (_gate) return JsonUtil.GetPath(_effective, path)?.DeepClone(); }

    /// <summary>Every known setting with its effective value and the layer that supplied it.</summary>
    public IReadOnlyList<ConfigValue> Describe()
    {
        lock (_gate)
        {
            var result = new List<ConfigValue>();
            foreach (var (path, _) in JsonUtil.Flatten(Defaults()))
            {
                var source = ConfigScope.Default;
                foreach (var scope in FileScopes)
                    if (_layers.TryGetValue(scope, out var l) && JsonUtil.GetPath(l, path) is not null) source = scope;
                result.Add(new ConfigValue(path, JsonUtil.GetPath(_effective, path)?.DeepClone(), source));
            }
            return result;
        }
    }

    /// <summary>Canonical (camelCase) spelling of a setting path, or null if unknown.</summary>
    public static string? Canonical(string path) =>
        JsonUtil.Flatten(Defaults()).Select(x => x.Path).FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Converts text to the JSON type of the setting's default value.</summary>
    public static JsonNode? Coerce(string path, string text)
    {
        var def = JsonUtil.GetPath(Defaults(), path) ?? throw new ArgumentException($"Unknown setting '{path}'.");
        switch (def.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False:
                if (bool.TryParse(text, out var b)) return JsonValue.Create(b);
                if (text is "1" or "on" or "yes") return JsonValue.Create(true);
                if (text is "0" or "off" or "no") return JsonValue.Create(false);
                throw new ArgumentException($"'{path}' expects true or false.");
            case System.Text.Json.JsonValueKind.Number:
                if (long.TryParse(text, out var l)) return JsonValue.Create(l);
                if (double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
                throw new ArgumentException($"'{path}' expects a number.");
            default: return JsonValue.Create(text);
        }
    }

    /// <summary>Writes one setting to the file for <paramref name="scope"/>. A null value removes the override.</summary>
    public void Set(string path, JsonNode? value, ConfigScope scope = ConfigScope.Home)
    {
        var canonical = Canonical(path) ?? throw new ArgumentException($"Unknown setting '{path}'.");
        var file = FileFor(scope);
        JsonObject root = new();
        if (File.Exists(file))
        {
            try { root = JsonUtil.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject(); }
            catch (Exception ex) { throw new InvalidOperationException($"{file} is not valid JSON ({ex.Message}); fix it before saving settings."); }
        }
        JsonUtil.SetPath(root, canonical, value?.DeepClone());
        Prune(root);
        File.WriteAllText(file, JsonUtil.Pretty(root) + Environment.NewLine);
        Log.Info("config", $"Set {canonical} = {JsonUtil.Line(value)} in {file}");
        Reload();
    }

    private static void Prune(JsonObject o)
    {
        foreach (var key in o.Select(p => p.Key).ToList())
            if (o[key] is JsonObject child) { Prune(child); if (child.Count == 0) o.Remove(key); }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce?.Dispose();
    }
}
