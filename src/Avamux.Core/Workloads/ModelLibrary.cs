using System.Text;
using System.Text.RegularExpressions;
using Avamux.Core.Logging;

namespace Avamux.Core.Workloads;

/// <summary>A model on disk. A model split into shards is one entry, at its first shard.</summary>
public sealed class ModelFile
{
    public string Path { get; set; } = "";
    /// <summary>The file's name (without the extension and shard numbers).</summary>
    public string Name { get; set; } = "";
    /// <summary>The name in the file's metadata (general.name), when it has a useful one.</summary>
    public string? Title { get; set; }
    /// <summary>gguf, ggml (whisper.cpp), onnx, safetensors.</summary>
    public string Format { get; set; } = "";
    public long Bytes { get; set; }
    public int Shards { get; set; } = 1;
    public DateTime Modified { get; set; }
    public string? Architecture { get; set; }
    /// <summary>"27B", "35B-A3B" (general.size_label).</summary>
    public string? Size { get; set; }
    public string? Quant { get; set; }
    public long? ContextLength { get; set; }
    public long? Layers { get; set; }
    public long? Experts { get; set; }
    /// <summary>A vision/audio projector (mmproj) rather than a model on its own.</summary>
    public bool Projector { get; set; }
    public string? Error { get; set; }

    internal ModelFile Copy() => (ModelFile)MemberwiseClone();
}

/// <summary>
/// The model files under a few folders: GGUF ones with what their header says (architecture, size, quantisation,
/// context, layers), whisper.cpp's ggml files, ONNX and safetensors files by name and size. Reading a header takes a
/// few milliseconds, but there can be hundreds of files, so results are kept by path, size and time.
/// </summary>
public sealed partial class ModelLibrary(string cacheFile)
{
    private readonly object _gate = new();
    private Dictionary<string, ModelFile>? _cache;

    public static IEnumerable<string> DefaultFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            System.IO.Path.Combine(home, "models"), System.IO.Path.Combine(home, ".ollama", "models"), System.IO.Path.Combine(home, ".lmstudio", "models"),
            System.IO.Path.Combine(home, ".cache", "huggingface", "hub"), System.IO.Path.Combine(home, ".cache", "lm-studio", "models"),
            System.IO.Path.Combine(home, ".cache", "llama.cpp"),
        ];
        return candidates.Where(Directory.Exists);
    }

    [GeneratedRegex(@"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex Shard();

    private static readonly string[] Extensions = [".gguf", ".onnx", ".safetensors", ".bin"];

    /// <summary>Scans the folders (recursively, following no links into themselves) and returns the models, biggest first.</summary>
    public List<ModelFile> Scan(IEnumerable<string> folders, CancellationToken ct = default)
    {
        LoadCache();
        var found = new Dictionary<string, ModelFile>();
        var shards = new Dictionary<string, List<FileInfo>>();
        var seen = new HashSet<string>();
        foreach (var root in folders.Select(Ai.AiService.ExpandHome).Where(Directory.Exists))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, MaxRecursionDepth = 8 };
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", options); } catch { continue; }
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (!Extensions.Contains(ext)) continue;
                FileInfo info;
                try { info = new FileInfo(WorkloadService.RealPath(path)); } catch { continue; }
                if (!info.Exists || !seen.Add(info.FullName)) continue;
                if (info.Length < 1_000_000) continue; // configs, tokenizers, tiny test files
                if (ext == ".bin" && !System.IO.Path.GetFileName(path).StartsWith("ggml-", StringComparison.OrdinalIgnoreCase)) continue;
                if (ext == ".gguf" && Shard().Match(info.Name) is { Success: true } sm)
                {
                    var key = System.IO.Path.Combine(info.DirectoryName!, info.Name[..sm.Index]);
                    if (!shards.TryGetValue(key, out var list)) shards[key] = list = [];
                    list.Add(info);
                    continue;
                }
                found[info.FullName] = Describe(info);
            }
        }
        foreach (var (_, parts) in shards)
        {
            var first = parts.OrderBy(p => p.Name, StringComparer.Ordinal).First();
            // A copy: the cache keeps what the first shard's own header and size say, so the next scan finds it there.
            var m = Describe(first).Copy();
            m.Name = Shard().Replace(first.Name, "");
            m.Bytes = parts.Sum(p => p.Length);
            m.Shards = parts.Count;
            found[first.FullName] = m;
        }
        SaveCache();
        return found.Values.OrderByDescending(m => m.Bytes).ToList();
    }

    private ModelFile Describe(FileInfo info)
    {
        lock (_gate)
            if (_cache!.TryGetValue(info.FullName, out var cached) && cached.Bytes == info.Length && cached.Modified == info.LastWriteTimeUtc)
                return cached;
        var ext = info.Extension.ToLowerInvariant();
        var m = new ModelFile
        {
            Path = info.FullName, Name = System.IO.Path.GetFileNameWithoutExtension(info.Name), Bytes = info.Length, Modified = info.LastWriteTimeUtc,
            Format = ext switch { ".gguf" => "gguf", ".bin" => "ggml", _ => ext.TrimStart('.') },
        };
        if (ext == ".gguf")
        {
            try { Gguf.Fill(info.FullName, m); }
            catch (Exception ex) { m.Error = ex.Message; }
        }
        if (m.Name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) m.Projector = true;
        lock (_gate) _cache![info.FullName] = m;
        return m;
    }

    private void LoadCache()
    {
        lock (_gate)
        {
            if (_cache is not null) return;
            try { _cache = File.Exists(cacheFile) ? JsonUtil.LoadFile<Dictionary<string, ModelFile>>(cacheFile) : []; }
            catch { _cache = []; }
        }
    }

    /// <summary>
    /// Saves every file's entry: Describe put each one there, and what was found in folders not scanned this time is
    /// kept, so scanning one folder does not forget the rest. A sharded model's entry is its first shard alone (its
    /// own size, which the next scan compares), never the model's total.
    /// </summary>
    private void SaveCache()
    {
        try
        {
            lock (_gate) JsonUtil.SaveFile(cacheFile, _cache);
        }
        catch (Exception ex) { Log.Warn("workloads", "Could not save the model cache: " + ex.Message); }
    }
}

/// <summary>Reads the metadata of a GGUF file's header (not its tensors).</summary>
public static class Gguf
{
    private static readonly Dictionary<long, string> FileTypes = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [7] = "Q8_0", [8] = "Q5_0", [9] = "Q5_1", [10] = "Q2_K", [11] = "Q3_K_S", [12] = "Q3_K_M",
        [13] = "Q3_K_L", [14] = "Q4_K_S", [15] = "Q4_K_M", [16] = "Q5_K_S", [17] = "Q5_K_M", [18] = "Q6_K", [19] = "IQ2_XXS", [20] = "IQ2_XS", [21] = "Q2_K_S",
        [22] = "IQ3_XS", [23] = "IQ3_XXS", [24] = "IQ1_S", [25] = "IQ4_NL", [26] = "IQ3_S", [27] = "IQ3_M", [28] = "IQ2_S", [29] = "IQ2_M", [30] = "IQ4_XS",
        [31] = "IQ1_M", [32] = "BF16", [36] = "TQ1_0", [37] = "TQ2_0", [38] = "MXFP4",
    };

    public static Dictionary<string, object> ReadMetadata(string path, int maxKeys = 4096)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var r = new BinaryReader(f, Encoding.UTF8);
        if (r.ReadUInt32() != 0x46554747) throw new InvalidDataException("not a GGUF file");
        var version = r.ReadUInt32();
        if (version < 2) throw new InvalidDataException($"GGUF version {version} is too old to read");
        r.ReadUInt64(); // tensors
        var count = r.ReadUInt64();
        var meta = new Dictionary<string, object>();
        for (ulong i = 0; i < count && i < (ulong)maxKeys; i++)
        {
            var key = Str(r);
            var type = r.ReadUInt32();
            var value = Value(r, type, keep: !key.StartsWith("tokenizer.", StringComparison.Ordinal));
            if (value is not null) meta[key] = value;
        }
        return meta;
    }

    public static void Fill(string path, ModelFile m)
    {
        var meta = ReadMetadata(path);
        string? S(string k) => meta.TryGetValue(k, out var v) ? v as string : null;
        long? L(string k) => meta.TryGetValue(k, out var v) ? v switch { long l => l, int i => i, uint u => u, ulong ul => (long)ul, _ => null } : null;
        var arch = S("general.architecture");
        m.Architecture = arch;
        if (S("general.name") is { Length: > 0 } name && !System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9a-f]{16,}$")) m.Title = name;
        m.Size = S("general.size_label");
        if (L("general.file_type") is { } ft) m.Quant = FileTypes.GetValueOrDefault(ft, $"type {ft}");
        if (arch is not null)
        {
            m.ContextLength = L($"{arch}.context_length");
            m.Layers = L($"{arch}.block_count");
            m.Experts = L($"{arch}.expert_count");
        }
        if (arch == "clip" || S("general.type") is "mmproj" or "projector") m.Projector = true;
    }

    private static string Str(BinaryReader r)
    {
        var len = r.ReadUInt64();
        if (len > 1 << 20) throw new InvalidDataException("bad string length");
        return Encoding.UTF8.GetString(r.ReadBytes((int)len));
    }

    private static int Size(uint type) => type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => -1 };

    private static object? Value(BinaryReader r, uint type, bool keep)
    {
        switch (type)
        {
            case 0: return (long)r.ReadByte();
            case 1: return (long)r.ReadSByte();
            case 2: return (long)r.ReadUInt16();
            case 3: return (long)r.ReadInt16();
            case 4: return (long)r.ReadUInt32();
            case 5: return (long)r.ReadInt32();
            case 6: return (double)r.ReadSingle();
            case 7: return r.ReadByte() != 0;
            case 8: return Str(r);
            case 10: return (long)r.ReadUInt64();
            case 11: return r.ReadInt64();
            case 12: return r.ReadDouble();
            case 9:
            {
                var itemType = r.ReadUInt32();
                var n = r.ReadUInt64();
                var size = Size(itemType);
                if (size > 0) { r.BaseStream.Seek((long)n * size, SeekOrigin.Current); return keep ? $"[{n} items]" : null; }
                for (ulong i = 0; i < n; i++) Value(r, itemType, false);
                return keep ? $"[{n} items]" : null;
            }
            default: throw new InvalidDataException($"unknown GGUF value type {type}");
        }
    }
}
