using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Avamux.Core;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static JsonNode? Parse(string text) => JsonNode.Parse(text, null, DocOptions);
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
    public static T? To<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Options);
    public static string Pretty(JsonNode? node) => node is null ? "null" : node.ToJsonString(Options);
    public static string Line(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    /// <summary>Deep-merges <paramref name="over"/> into <paramref name="target"/>. Objects merge per key; everything else is replaced.</summary>
    public static void Merge(JsonObject target, JsonObject over)
    {
        foreach (var (key, value) in over)
        {
            if (value is JsonObject o && target[key] is JsonObject t) Merge(t, o);
            else target[key] = value?.DeepClone();
        }
    }

    public static JsonNode? GetPath(JsonObject root, string path)
    {
        JsonNode? cur = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is not JsonObject o) return null;
            var key = o.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase));
            if (key is null) return null;
            cur = o[key];
        }
        return cur;
    }

    public static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) cur[parts[i]] = next = new JsonObject();
            cur = next;
        }
        if (value is null) cur.Remove(parts[^1]); else cur[parts[^1]] = value;
    }

    /// <summary>Flattens to dotted leaf paths.</summary>
    public static IEnumerable<(string Path, JsonNode? Value)> Flatten(JsonObject root, string prefix = "")
    {
        foreach (var (key, value) in root)
        {
            var p = prefix.Length == 0 ? key : prefix + "." + key;
            if (value is JsonObject o) foreach (var x in Flatten(o, p)) yield return x;
            else yield return (p, value);
        }
    }

    /// <summary>Loads a JSON state file, returning a default instance when missing or corrupt.</summary>
    public static T LoadFile<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex) { Logging.Log.Error("state", $"Could not read {path}: {ex.Message}"); }
        return new T();
    }

    public static void SaveFile<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}
