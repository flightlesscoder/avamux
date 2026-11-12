namespace Avamux.Plugins;

/// <summary>
/// Implement this in a .NET class library to add a new kind of "watch" to avamux.
/// Drop the built DLL into avamux's plugins folder (see `avamux app paths`), or point a watch of kind
/// "plugin" at it with the "assembly" setting. The class needs a public parameterless constructor.
/// </summary>
public interface IWatchProvider
{
    /// <summary>Unique kind name used when creating a watch, e.g. "jira-issue".</summary>
    string Kind { get; }

    /// <summary>One-line description shown in the UI and in `avamux watch kinds`.</summary>
    string Description { get; }

    /// <summary>Setting keys this provider understands, with a short help text each.</summary>
    IReadOnlyDictionary<string, string> Settings { get; }

    Task<WatchResult> CheckAsync(WatchContext context, CancellationToken cancellationToken);
}

public sealed class WatchContext
{
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, string> Settings { get; init; }
    public string? Link { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public string? Get(string key) => Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    public string Require(string key) => Get(key) ?? throw new InvalidOperationException($"Setting '{key}' is required.");
}

/// <summary>
/// The shape every watch produces. Script watchers print this as JSON on stdout:
/// {"status":"ok|info|warn|error","summary":"…","link":"https://…","items":[{"id":"…","title":"…","status":"…","detail":"…","link":"…"}]}
/// </summary>
public sealed class WatchResult
{
    /// <summary>ok, info, warn or error.</summary>
    public string Status { get; set; } = "ok";
    public string Summary { get; set; } = "";
    public string? Link { get; set; }
    public List<WatchResultItem> Items { get; set; } = [];
}

public sealed class WatchResultItem
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string? Status { get; set; }
    public string? Detail { get; set; }
    public string? Link { get; set; }
    /// <summary>Who the item belongs to (a pull request's author, say). Optional.</summary>
    public string? Author { get; set; }
    /// <summary>True when the item belongs to the current user. Optional; used by "only mine" filters.</summary>
    public bool? Mine { get; set; }
    /// <summary>False for items that are finished (merged, closed, abandoned). Optional; used by "active only" filters.</summary>
    public bool? Active { get; set; }
}
