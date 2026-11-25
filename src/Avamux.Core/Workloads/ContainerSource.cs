using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avamux.Plugins;

namespace Avamux.Core.Workloads;

/// <summary>
/// Containers and distroboxes (podman, else docker) that look like AI things by name or image (web UIs, Letta,
/// SillyTavern, CUDA build boxes …), running or stopped. podman is slow to ask, so the answer is kept for a few seconds.
/// </summary>
internal sealed partial class ContainerSource(HookList<Regex> patterns) : IWorkloadSource
{
    private (DateTime At, WorkloadSnapshot Snap)? _cache;
    private string? _tool;
    private bool _missing;

    public string Id => "containers";
    public string Name => "Containers";
    public string Machine => "This computer";
    public event Action? Changed { add { } remove { } }

    [GeneratedRegex(@"webui|open-?webui|letta|silly|tavern|ollama|llama|cuda|comfy|vllm|whisper|kokoro|tts|stt|kobold|tabby|lmstudio|mikupad|\bai\b|-ai-|ai-|localai|rocm|text-gen|invoke|stable-?diffusion|sd-", RegexOptions.IgnoreCase)]
    private static partial Regex AiLike();

    public async Task<WorkloadSnapshot> ListAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(10)) return c.Snap;
        if (_missing) return new WorkloadSnapshot([]);
        _tool ??= await FindToolAsync(ct);
        if (_tool is null) { _missing = true; return new WorkloadSnapshot([]); }
        var r = await Shell.RunAsync(_tool, ["ps", "-a", "--format", "json"], timeout: TimeSpan.FromSeconds(10), ct: ct);
        if (r.ExitCode != 0) return new WorkloadSnapshot([], $"{_tool} ps failed: {r.Stderr.Trim()}");
        var items = new List<WorkloadInfo>();
        foreach (var o in ParseList(r.Stdout))
        {
            var name = o["Names"] is JsonArray names ? names.FirstOrDefault()?.ToString() ?? "" : o["Names"]?.ToString() ?? "";
            var image = o["Image"]?.ToString() ?? "";
            var labels = o["Labels"] as JsonObject;
            var distrobox = labels?["manager"]?.ToString() == "distrobox";
            if (!distrobox && !Looks(name) && !Looks(image)) continue;
            var state = (o["State"]?.ToString() ?? "").ToLowerInvariant();
            var running = state == "running";
            var ports = o["Ports"] is JsonArray pa
                ? string.Join(", ", pa.OfType<JsonObject>().Where(p => p["host_port"] is not null).Select(p => $":{p["host_port"]}").Distinct())
                : o["Ports"]?.ToString() ?? "";
            DateTime? started = ServerProbe.Long(o["StartedAt"]) is { } s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime : null;
            items.Add(new WorkloadInfo
            {
                Id = name, Kind = WorkloadKind.Container, Name = name, Type = distrobox ? "distrobox" : _tool + " container",
                State = running ? WorkloadState.Running : WorkloadState.Stopped, Detail = o["Status"]?.ToString(), Model = image,
                Started = running ? started : null, Pid = running && ServerProbe.Long(o["Pid"]) is { } pid and > 0 ? (int)pid : null,
                Actions = running ? [new(WorkloadActions.Stop, "Stop", $"Stop the {(distrobox ? "distrobox" : "container")} {name}?")] : [new(WorkloadActions.Start, "Start")],
                Facts = new Dictionary<string, string> { ["Image"] = image, ["Ports"] = ports.Length > 0 ? ports : "none" },
            });
        }
        var snap = new WorkloadSnapshot(items);
        _cache = (DateTime.UtcNow, snap);
        return snap;
    }

    /// <summary>A name or image that looks like an AI thing: the usual words, or a pattern a plugin added.</summary>
    private bool Looks(string text) => AiLike().IsMatch(text) || patterns.Items.Any(p => p.IsMatch(text));

    private static IEnumerable<JsonObject> ParseList(string text)
    {
        text = text.Trim();
        if (text.StartsWith('[')) return JsonNode.Parse(text) is JsonArray a ? a.OfType<JsonObject>() : [];
        // docker prints one object per line
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => { try { return JsonNode.Parse(l) as JsonObject; } catch { return null; } }).OfType<JsonObject>();
    }

    private static async Task<string?> FindToolAsync(CancellationToken ct)
    {
        foreach (var tool in new[] { "podman", "docker" })
        {
            try { if ((await Shell.RunAsync(tool, ["--version"], timeout: TimeSpan.FromSeconds(5), ct: ct)).ExitCode == 0) return tool; }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    public async Task<string> ActAsync(string workloadId, string action, CancellationToken ct = default)
    {
        if (_tool is null || action is not (WorkloadActions.Start or WorkloadActions.Stop)) throw new InvalidOperationException($"Cannot {action} {workloadId}.");
        var r = await Shell.RunAsync(_tool, [action, workloadId], timeout: TimeSpan.FromMinutes(2), ct: ct);
        _cache = null;
        if (r.ExitCode != 0) throw new InvalidOperationException($"{_tool} {action} failed: {r.Stderr.Trim()}");
        return action == WorkloadActions.Start ? $"{workloadId} is started." : $"{workloadId} is stopped.";
    }

    public async Task<string> LogAsync(string workloadId, int lines = 200, CancellationToken ct = default)
    {
        if (_tool is null) return "";
        var r = await Shell.RunAsync(_tool, ["logs", "--tail", lines.ToString(), workloadId], timeout: TimeSpan.FromSeconds(15), ct: ct);
        return (r.Stdout + r.Stderr).TrimEnd();
    }
}
