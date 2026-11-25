using System.Text;
using Avamux.Core.Terminal;
using Avamux.Core.Wiki;

namespace Avamux.Core.Search;

/// <param name="Source">One of <see cref="SearchService.Sources"/>.</param>
/// <param name="Target">What to open: a wiki path, an event id, a log file path, a repository id, a watch id, a terminal id, a setting key.</param>
/// <param name="Extra">Refines the target: "commit:hash" or "file:path" for repositories.</param>
/// <param name="Line">Line number inside the target, when the match is a line of text.</param>
public sealed record SearchHit(string Source, string Title, string Detail, string Target, string? Extra = null, int Line = 0);

/// <summary>One search box over everything avamux knows about.</summary>
public sealed class SearchService(AppServices app)
{
    public static readonly (string Key, string Label)[] Sources =
    [
        ("wiki", "Wiki"), ("tags", "Tags"), ("events", "Events"), ("watches", "Watches"), ("terminals", "Terminals"),
        ("repositories", "Repositories"), ("logs", "Logs"), ("settings", "Settings"),
    ];

    public const int PerSource = 60;

    private static bool Has(string? text, string q) => text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>A line cut down to the neighbourhood of the match.</summary>
    public static string Snippet(string line, string query, int width = 160)
    {
        var text = line.Trim();
        if (text.Length <= width) return text;
        var at = Math.Max(0, text.IndexOf(query, StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, Math.Min(at - width / 3, text.Length - width));
        return (start > 0 ? "…" : "") + text.Substring(start, Math.Min(width, text.Length - start)) + (start + width < text.Length ? "…" : "");
    }

    /// <summary>Searches the given sources (all when null or empty). Sources are searched side by side.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, IEnumerable<string>? sources = null, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        var wanted = sources?.Select(s => s.ToLowerInvariant()).ToHashSet() ?? [];
        if (wanted.Count == 0) wanted = Sources.Select(s => s.Key).ToHashSet();
        var tasks = new List<Task<List<SearchHit>>>();
        void Run(string key, Func<List<SearchHit>> work)
        {
            if (!wanted.Contains(key)) return;
            tasks.Add(Task.Run(() =>
            {
                try { return work(); }
                catch (Exception ex) { Logging.Log.Warn("search", $"Searching {key} failed: {ex.Message}"); return []; }
            }, ct));
        }
        Run("wiki", () => Wiki(q, ct));
        Run("tags", () => Tags(q));
        Run("events", () => Events(q));
        Run("watches", () => Watches(q));
        Run("terminals", () => Terminals(q));
        Run("logs", () => Logs(q, ct));
        Run("settings", () => Settings(q));
        if (wanted.Contains("repositories")) tasks.Add(ReposAsync(q, ct));
        var results = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        var order = Sources.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i);
        return results.SelectMany(r => r.Take(PerSource)).OrderBy(h => order.GetValueOrDefault(h.Source, 99)).ToList();
    }

    /// <summary>Pages carrying a tag whose name holds the query ("#" optional): the tag's first line on each.</summary>
    private List<SearchHit> Tags(string q)
    {
        var key = WikiTags.Normalize(q);
        if (key.Length == 0) return [];
        var hits = new List<SearchHit>();
        foreach (var tag in app.Tags.Tags().Where(t => t.Key.Contains(key, StringComparison.Ordinal)).OrderBy(t => t.Key == key ? 0 : 1))
            foreach (var hit in app.Tags.Pages(tag.Key, nested: false))
            {
                if (hits.Count >= PerSource) return hits;
                hits.Add(new SearchHit("tags", hit.Page, "#" + tag.Name + " · " + hit.Snippet, hit.Page, Line: hit.Line));
            }
        return hits;
    }

    private List<SearchHit> Wiki(string q, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        foreach (var page in app.Wiki.Pages())
        {
            if (ct.IsCancellationRequested || hits.Count >= PerSource) break;
            if (Has(page, q)) hits.Add(new SearchHit("wiki", page, "file name", page));
            if (WikiService.KindOf(page) is not (WikiKind.Markdown or WikiKind.Text)) continue;
            string[] lines;
            try
            {
                var full = app.Wiki.Resolve(page);
                if (new FileInfo(full).Length > WikiService.MaxEditableBytes) continue;
                lines = File.ReadAllLines(full);
            }
            catch { continue; }
            var perPage = 0;
            for (var i = 0; i < lines.Length && perPage < 8 && hits.Count < PerSource; i++)
                if (Has(lines[i], q)) { hits.Add(new SearchHit("wiki", page, Snippet(lines[i], q), page, null, i + 1)); perPage++; }
        }
        return hits;
    }

    private List<SearchHit> Events(string q) =>
        app.Events.Items.Where(e => Has(e.Title, q) || Has(e.Notes, q) || Has(e.Location, q) || e.Attachments.Any(a => Has(a, q)))
            .Select(e => new SearchHit("events", e.Title,
                $"{e.Kind.ToString().ToLowerInvariant()} · {e.Start:yyyy-MM-dd HH:mm}" + (Has(e.Location, q) ? $" · {e.Location}" : Has(e.Notes, q) ? " · " + Snippet(e.Notes!.Split('\n').First(l => Has(l, q)), q, 100) : ""), e.Id)).ToList();

    private List<SearchHit> Watches(string q)
    {
        var hits = new List<SearchHit>();
        foreach (var w in app.Watches.Items)
        {
            if (Has(w.Name, q) || Has(w.Summary, q) || Has(w.Kind, q) || Has(w.Link, q)) hits.Add(new SearchHit("watches", w.Name, $"{w.Kind} · {w.Summary}", w.Id));
            foreach (var item in w.LastResult?.Items ?? [])
                if (Has(item.Title, q) || Has(item.Detail, q) || Has(item.Author, q))
                    hits.Add(new SearchHit("watches", item.Title, $"{w.Name} · {string.Join(" · ", new[] { item.Status, item.Detail }.Where(x => !string.IsNullOrWhiteSpace(x)))}", w.Id, item.Link));
        }
        return hits;
    }

    private List<SearchHit> Terminals(string q)
    {
        var hits = new List<SearchHit>();
        foreach (var t in app.Terminals.Sessions)
        {
            if (Has(t.Title, q) || Has(t.Cwd, q) || Has(t.Command, q) || Has(t.OscTitle, q)) hits.Add(new SearchHit("terminals", t.Title, $"tab {t.Id} · {t.Cwd}", t.Id));
            var text = TerminalManager.StripAnsi(Encoding.UTF8.GetString(t.Snapshot()));
            var lines = text.Split('\n');
            var found = 0;
            // Newest output first: that is usually what is being looked for.
            for (var i = lines.Length - 1; i >= 0 && found < 8; i--)
                if (Has(lines[i], q)) { hits.Add(new SearchHit("terminals", t.Title, Snippet(lines[i], q), t.Id, null, i + 1)); found++; }
        }
        return hits;
    }

    private List<SearchHit> Logs(string q, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        foreach (var f in app.Logs.List().Take(80))
        {
            if (ct.IsCancellationRequested || hits.Count >= PerSource) break;
            if (Has(f.Name, q)) hits.Add(new SearchHit("logs", $"{f.Category}/{f.Name}", "file name", f.Path));
            string text;
            try
            {
                using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const long cap = 2 * 1024 * 1024;
                if (fs.Length > cap) fs.Seek(-cap, SeekOrigin.End);
                using var reader = new StreamReader(fs);
                text = reader.ReadToEnd();
            }
            catch { continue; }
            var lines = text.Split('\n');
            var found = 0;
            for (var i = lines.Length - 1; i >= 0 && found < 6 && hits.Count < PerSource; i--)
                if (Has(lines[i], q)) { hits.Add(new SearchHit("logs", $"{f.Category}/{f.Name}", Snippet(lines[i], q), f.Path, null, i + 1)); found++; }
        }
        return hits;
    }

    private List<SearchHit> Settings(string q) =>
        app.Config.Describe().Where(v => Has(v.Path, q) || Has(v.Value?.ToString(), q))
            .Select(v => new SearchHit("settings", v.Path, JsonUtil.Line(v.Value), v.Path)).ToList();

    /// <summary>Every setting whose name or value contains the text: the Settings source of the search, without the per-source cap.</summary>
    public HashSet<string> MatchingSettings(string query)
    {
        var q = query.Trim();
        return q.Length == 0 ? [] : Settings(q).Select(h => h.Target).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<SearchHit>> ReposAsync(string q, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        try
        {
            var env = new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C.UTF-8" };
            foreach (var repo in app.Repos.Items)
            {
                if (ct.IsCancellationRequested || hits.Count >= PerSource) break;
                if (Has(repo.Name, q) || Has(repo.Path, q)) hits.Add(new SearchHit("repositories", repo.Name, repo.Path, repo.Id));
                if (q.Length < 2) continue;
                Task<Shell.RunResult> Git(params string[] args) => Shell.RunAsync("git", new[] { "-c", "core.quotepath=off", "-c", "color.ui=never" }.Concat(args), repo.Path, env, TimeSpan.FromSeconds(10), ct);
                var commits = Git("log", "-n", "10", "-i", "-F", "--grep=" + q, "--pretty=format:%h%x1f%an%x1f%ad%x1f%s", "--date=format:%Y-%m-%d");
                var files = Git("ls-files");
                var grep = Git("grep", "-n", "-I", "-i", "-F", "--max-count=3", "-e", q);
                await Task.WhenAll(commits, files, grep);
                foreach (var p in commits.Result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\x1f')).Where(p => p.Length == 4))
                    hits.Add(new SearchHit("repositories", p[3], $"{repo.Name} · commit {p[0]} · {p[1]} · {p[2]}", repo.Id, "commit:" + p[0]));
                foreach (var f in files.Result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(f => Has(f, q)).Take(10))
                    hits.Add(new SearchHit("repositories", f, $"{repo.Name} · file", repo.Id, "file:" + f));
                foreach (var line in grep.Result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(20))
                {
                    var parts = line.Split(':', 3);
                    if (parts.Length == 3 && int.TryParse(parts[1], out var n))
                        hits.Add(new SearchHit("repositories", parts[0], $"{repo.Name} · {Snippet(parts[2], q, 120)}", repo.Id, "file:" + parts[0], n));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logging.Log.Warn("search", "Searching repositories failed: " + ex.Message); }
        return hits;
    }
}
