using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Avamux.Core.Workloads;

/// <summary>A GPU's memory, and which processes hold how much of it.</summary>
public sealed record GpuInfo(int Index, string Name, long UsedMiB, long TotalMiB, long FreeMiB);

public sealed record GpuSnapshot(IReadOnlyList<GpuInfo> Gpus, IReadOnlyDictionary<int, long> UsedByPid)
{
    public static readonly GpuSnapshot Empty = new([], new Dictionary<int, long>());
    public long FreeMiB => Gpus.Sum(g => g.FreeMiB);
}

/// <summary>Reads NVIDIA GPUs with nvidia-smi (other GPUs: nothing, for now).</summary>
public static class Gpu
{
    private static bool _missing;

    public static async Task<GpuSnapshot> ReadAsync(CancellationToken ct = default)
    {
        if (_missing) return GpuSnapshot.Empty;
        try
        {
            var gpus = await Shell.RunAsync("nvidia-smi", ["--query-gpu=index,name,memory.used,memory.total,memory.free", "--format=csv,noheader,nounits"], timeout: TimeSpan.FromSeconds(5), ct: ct);
            if (gpus.ExitCode != 0) return GpuSnapshot.Empty;
            var list = new List<GpuInfo>();
            foreach (var line in gpus.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(',', StringSplitOptions.TrimEntries);
                if (f.Length < 5) continue;
                list.Add(new GpuInfo(Int(f[0]), f[1], Int(f[2]), Int(f[3]), Int(f[4])));
            }
            var apps = await Shell.RunAsync("nvidia-smi", ["--query-compute-apps=pid,used_memory", "--format=csv,noheader,nounits"], timeout: TimeSpan.FromSeconds(5), ct: ct);
            var used = new Dictionary<int, long>();
            foreach (var line in apps.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(',', StringSplitOptions.TrimEntries);
                if (f.Length >= 2 && int.TryParse(f[0], out var pid)) used[pid] = used.GetValueOrDefault(pid) + Int(f[1]);
            }
            return new GpuSnapshot(list, used);
        }
        catch (System.ComponentModel.Win32Exception) { _missing = true; return GpuSnapshot.Empty; }
        catch (Exception) { return GpuSnapshot.Empty; }
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>What a model server says about itself.</summary>
/// <param name="Answering">It answered at all.</param>
/// <param name="Ready">It is ready for requests (false while a model loads).</param>
/// <param name="Busy">It is working on a request now.</param>
/// <param name="Activity">Changes whenever the server takes a new request (llama.cpp: the slots' task ids), for idle unloading.</param>
public sealed record ServerStatus(bool Answering, bool Ready, bool Busy, string? Model, long? Context, string? Detail, int Slots = 0, long? Activity = null)
{
    public static readonly ServerStatus Down = new(false, false, false, null, null, null);
}

/// <summary>Asks servers that speak llama.cpp's HTTP API (llama-server, whisper.cpp, OpenAI-compatible ones) how they are.</summary>
public static class ServerProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static string LocalUrl(int port) => $"http://127.0.0.1:{port}";

    public static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var r = await Http.GetAsync(url, ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            var node = text.Length > 0 && (text[0] == '{' || text[0] == '[') ? JsonNode.Parse(text) : null;
            if (!r.IsSuccessStatusCode) return node is JsonObject o ? new JsonObject { ["_status"] = (int)r.StatusCode, ["_body"] = o.DeepClone() } : new JsonObject { ["_status"] = (int)r.StatusCode };
            return node;
        }
        catch { return null; }
    }

    /// <summary>
    /// /health, then /props and /slots for what is loaded and whether it is working. Some servers answer /health with
    /// <c>{"loaded": …, "model": …, "max_context": …}</c> instead; a server still loading its model answers 503.
    /// </summary>
    public static async Task<ServerStatus> ProbeAsync(string baseUrl, CancellationToken ct = default)
    {
        baseUrl = baseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/v1", StringComparison.Ordinal)) baseUrl = baseUrl[..^3];
        var health = await GetJsonAsync(baseUrl + "/health", ct);
        if (health is null)
        {
            // Not every server has /health (Ollama, LM Studio, vLLM do /v1/models).
            var models = await GetJsonAsync(baseUrl + "/v1/models", ct);
            if (models is null) return ServerStatus.Down;
            return new ServerStatus(true, models["_status"] is null, false, FirstModel(models), null, null);
        }
        if (health["_status"] is JsonValue status)
        {
            var message = health["_body"]?["error"]?["message"]?.ToString();
            return new ServerStatus(true, false, false, null, null, message ?? $"HTTP {status}");
        }
        if (health["loaded"] is JsonValue loaded)
        {
            var ready = loaded.TryGetValue<bool>(out var l) && l;
            return new ServerStatus(true, ready, health["busy"]?.GetValue<bool>() == true, health["model"]?.ToString(), Long(health["max_context"]), ready ? null : "Loading the model");
        }

        var props = await GetJsonAsync(baseUrl + "/props", ct);
        string? model = null;
        long? ctx = null;
        var slots = 0;
        if (props is JsonObject p && p["_status"] is null)
        {
            model = p["model_alias"]?.ToString() is { Length: > 0 } alias ? alias : p["model_path"]?.ToString() is { Length: > 0 } path ? Path.GetFileName(path) : null;
            ctx = Long(p["default_generation_settings"]?["n_ctx"]) ?? Long(p["n_ctx"]);
            slots = (int)(Long(p["total_slots"]) ?? 0);
        }
        var busy = false;
        string? detail = null;
        long? activity = null;
        if (await GetJsonAsync(baseUrl + "/slots", ct) is JsonArray slotList)
        {
            slots = Math.Max(slots, slotList.Count);
            // Builds of llama-server differ in what a slot holds (next_token is an array in some, an object in others):
            // a slot that cannot be read is skipped, never the whole server.
            foreach (var s in slotList.OfType<JsonObject>())
            {
                try
                {
                    activity = (activity ?? 0) + (Long(s["id_task"]) ?? 0);
                    if (!(s["is_processing"] is JsonValue processing && processing.TryGetValue<bool>(out var on) && on)) continue;
                    busy = true;
                    var next = s["next_token"] is JsonArray { Count: > 0 } tokens ? tokens[0] : s["next_token"];
                    var decoded = Long((next as JsonObject)?["n_decoded"]) ?? Long(s["n_decoded"]);
                    if (decoded is { } n) detail = $"Writing: {n:N0} tokens so far";
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException) { }
            }
        }
        if (model is null && await GetJsonAsync(baseUrl + "/v1/models", ct) is { } m) model = FirstModel(m);
        return new ServerStatus(true, true, busy, model, ctx, detail, slots, activity);
    }

    private static string? FirstModel(JsonNode models) =>
        models["data"] is JsonArray a && a.Count > 0 ? a[0]?["id"]?.ToString() : models["models"] is JsonArray b && b.Count > 0 ? b[0]?["name"]?.ToString() : null;

    public static long? Long(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return long.TryParse(v.ToString(), out l) ? l : null;
    }

    /// <summary>Waits until the server is ready, the process dies, or the time is up. Returns null when ready, else why not.</summary>
    public static async Task<string?> WaitReadyAsync(string baseUrl, Func<bool>? alive, TimeSpan timeout, Action<ServerStatus>? progress = null, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var s = await ProbeAsync(baseUrl, ct);
            progress?.Invoke(s);
            if (s.Ready) return null;
            if (alive is not null && !alive()) return "it exited while loading";
            await Task.Delay(1500, ct);
        }
        return $"it was not ready after {timeout.TotalMinutes:0} minutes";
    }

    // ---- Ollama ----

    public sealed record OllamaModel(string Name, long SizeBytes, long VramBytes, DateTime? ExpiresAt, long? Context);

    /// <summary>The models Ollama has loaded (<c>/api/ps</c>); null when Ollama does not answer.</summary>
    public static async Task<List<OllamaModel>?> OllamaLoadedAsync(string baseUrl, CancellationToken ct = default)
    {
        if (await GetJsonAsync(baseUrl.TrimEnd('/') + "/api/ps", ct) is not JsonObject ps || ps["models"] is not JsonArray models) return null;
        return models.OfType<JsonObject>().Select(m => new OllamaModel(m["name"]?.ToString() ?? "?", Long(m["size"]) ?? 0, Long(m["size_vram"]) ?? 0,
            DateTime.TryParse(m["expires_at"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null, Long(m["context_length"]))).ToList();
    }

    /// <summary>Unloads a model from Ollama (a request with keep_alive 0).</summary>
    public static async Task OllamaUnloadAsync(string baseUrl, string model, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = model, ["keep_alive"] = 0 }.ToJsonString();
        using var r = await Http.PostAsync(baseUrl.TrimEnd('/') + "/api/generate", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        r.EnsureSuccessStatusCode();
    }
}
