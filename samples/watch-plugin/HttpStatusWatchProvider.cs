using System.Diagnostics;
using Avamux.Plugins;

namespace SampleWatchPlugin;

/// <summary>
/// Example plugin: checks that one or more URLs answer. Shows everything a provider does — declare a kind,
/// describe its settings, and return a <see cref="WatchResult"/> with a status, a summary, a link and items.
///
///   avamux watch add "Sites" --kind http-status --set urls=https://example.com,https://example.org
/// </summary>
public sealed class HttpStatusWatchProvider : IWatchProvider
{
    private static readonly HttpClient Http = new();

    public string Kind => "http-status";
    public string Description => "Check that web addresses respond (sample plugin).";

    public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["urls"] = "Comma-separated addresses to request (required).",
        ["slowMs"] = "Responses slower than this many milliseconds count as a warning (default 2000).",
    };

    public async Task<WatchResult> CheckAsync(WatchContext context, CancellationToken cancellationToken)
    {
        var urls = context.Require("urls").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var slow = int.TryParse(context.Get("slowMs"), out var ms) ? ms : 2000;
        var items = new List<WatchResultItem>();
        foreach (var url in urls)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var code = (int)response.StatusCode;
                items.Add(new WatchResultItem
                {
                    Id = url, Title = url, Link = url,
                    Status = code >= 400 ? "error" : timer.ElapsedMilliseconds > slow ? "warn" : "ok",
                    Detail = $"HTTP {code} in {timer.ElapsedMilliseconds} ms",
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                items.Add(new WatchResultItem { Id = url, Title = url, Link = url, Status = "error", Detail = ex.Message });
            }
        }

        var failed = items.Count(i => i.Status == "error");
        var slowOnes = items.Count(i => i.Status == "warn");
        return new WatchResult
        {
            Status = failed > 0 ? "error" : slowOnes > 0 ? "warn" : "ok",
            Summary = failed > 0 ? $"{failed} of {items.Count} not responding" : slowOnes > 0 ? $"{slowOnes} of {items.Count} slow" : $"All {items.Count} responding",
            Link = urls.FirstOrDefault(),
            Items = items,
        };
    }
}
