using Avamux.Plugins;

namespace Avamux.Core.Watching;

public sealed class WatchItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "command";
    public Dictionary<string, string> Settings { get; set; } = new();
    /// <summary>Minutes between checks; null uses watching.defaultIntervalMinutes.</summary>
    public double? IntervalMinutes { get; set; }
    /// <summary>User-set link. When empty, the link reported by the last check is used.</summary>
    public string? Link { get; set; }
    public bool Enabled { get; set; } = true;
    public WatchResult? LastResult { get; set; }
    public DateTime? LastChecked { get; set; }
    public DateTime? LastChanged { get; set; }
    public string? LastError { get; set; }
    /// <summary>True once the watch has had a first result, so later differences are real changes.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Notified { get; set; }

    public string? EffectiveLink => !string.IsNullOrWhiteSpace(Link) ? Link : LastResult?.Link;
    public string Status => LastError is not null ? "error" : LastResult?.Status ?? "unknown";
    public string Summary => LastError ?? LastResult?.Summary ?? "Not checked yet";
}

public sealed class WatchState
{
    public List<WatchItem> Items { get; set; } = [];
}
