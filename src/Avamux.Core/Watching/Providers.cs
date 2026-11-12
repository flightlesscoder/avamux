using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avamux.Plugins;

namespace Avamux.Core.Watching;

/// <summary>Runs any command; the command prints a <see cref="WatchResult"/> as JSON on stdout.</summary>
public sealed class CommandWatchProvider : IWatchProvider
{
    public string Kind => "command";
    public string Description => "Run a script or command that prints the watch result JSON on stdout.";
    public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["command"] = "Command line to run through /bin/sh (required).",
        ["cwd"] = "Working directory (optional).",
    };

    public async Task<WatchResult> CheckAsync(WatchContext context, CancellationToken cancellationToken)
    {
        var env = new Dictionary<string, string> { ["AVAMUX_WATCH_NAME"] = context.Name, ["AVAMUX_WATCH_LINK"] = context.Link ?? "" };
        foreach (var (k, v) in context.Settings)
            env["AVAMUX_WATCH_" + new string(k.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray())] = v;
        var run = await Shell.RunAsync(context.Require("command"), context.Get("cwd"), env, context.Timeout, cancellationToken);
        if (run.TimedOut) throw new TimeoutException($"Command timed out after {context.Timeout.TotalSeconds:0}s.");
        return ParseOutput(run.Stdout, run.Stderr, run.ExitCode);
    }

    /// <summary>
    /// Accepts the JSON shape; as a convenience, non-JSON output becomes the summary and the exit code the status
    /// (0 ok, 1 warn, anything else error).
    /// </summary>
    public static WatchResult ParseOutput(string stdout, string stderr, int exitCode)
    {
        var text = stdout.Trim();
        var start = text.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                var result = JsonSerializer.Deserialize<WatchResult>(text[start..], JsonUtil.Options);
                if (result is not null)
                {
                    result.Status = NormalizeStatus(result.Status);
                    return result;
                }
            }
            catch (JsonException) { /* fall through to plain text */ }
        }
        var firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        if (string.IsNullOrEmpty(firstLine)) firstLine = stderr.Trim().Split('\n').FirstOrDefault()?.Trim();
        return new WatchResult
        {
            Status = exitCode == 0 ? "ok" : exitCode == 1 ? "warn" : "error",
            Summary = string.IsNullOrEmpty(firstLine) ? $"Exit code {exitCode}" : firstLine,
        };
    }

    public static string NormalizeStatus(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "ok" or "success" or "succeeded" or "pass" or "passed" or "green" => "ok",
        "info" or "pending" or "running" or "inprogress" or "in_progress" or "notstarted" => "info",
        "warn" or "warning" or "partiallysucceeded" or "yellow" => "warn",
        "error" or "fail" or "failed" or "failure" or "red" or "canceled" or "cancelled" => "error",
        _ => "info",
    };
}

internal static class Http
{
    public static readonly HttpClient Client = Create();
    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("avamux-watch");
        return c;
    }

    public static async Task<JsonNode> GetJsonAsync(string url, AuthenticationHeaderValue? auth, string? accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth is not null) req.Headers.Authorization = auth;
        req.Headers.Accept.ParseAdd(accept ?? "application/json");
        using var resp = await Client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} from {new Uri(url).Host}: {Truncate(body, 200)}");
        if (resp.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
            throw new HttpRequestException("Got a sign-in page instead of JSON; check the access token.");
        return JsonNode.Parse(body) ?? throw new HttpRequestException("Empty response.");
    }

    public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    public static string? Token(WatchContext c, string envSetting, params string[] defaults)
    {
        if (c.Get("token") is { } direct) return direct;
        var names = c.Get(envSetting) is { } n ? [n] : defaults;
        return names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }
}

/// <summary>GitHub pull requests through the REST API (github.com or GitHub Enterprise).</summary>
public sealed class GitHubPrWatchProvider : IWatchProvider
{
    public string Kind => "github-pr";
    public string Description => "Watch one GitHub pull request, or the open pull requests of a repository.";
    public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["repo"] = "owner/name (required).",
        ["number"] = "Pull request number. Omit to list pull requests.",
        ["state"] = "open (default), closed or all, when listing.",
        ["author"] = "Only pull requests by this login, when listing.",
        ["tokenEnv"] = "Environment variable holding a token (default GITHUB_TOKEN, then GH_TOKEN). Optional for public repos.",
        ["apiUrl"] = "API base URL (default https://api.github.com).",
    };

    public async Task<WatchResult> CheckAsync(WatchContext c, CancellationToken ct)
    {
        var repo = c.Require("repo");
        var api = (c.Get("apiUrl") ?? "https://api.github.com").TrimEnd('/');
        var token = Http.Token(c, "tokenEnv", "GITHUB_TOKEN", "GH_TOKEN");
        var auth = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        const string accept = "application/vnd.github+json";

        if (c.Get("number") is { } number)
        {
            var pr = await Http.GetJsonAsync($"{api}/repos/{repo}/pulls/{number}", auth, accept, ct);
            var item = ToItem(pr);
            var merged = pr["merged"]?.GetValue<bool>() == true;
            var state = merged ? "merged" : pr["draft"]?.GetValue<bool>() == true ? "draft" : pr["state"]?.GetValue<string>() ?? "?";
            var mergeable = pr["mergeable_state"]?.GetValue<string>();
            var status = merged ? "ok" : state == "closed" ? "warn" : mergeable is "dirty" or "blocked" ? "warn" : "info";
            return new WatchResult
            {
                Status = status,
                Summary = $"#{number} {pr["title"]?.GetValue<string>()} — {state}" + (mergeable is null or "unknown" || merged ? "" : $" ({mergeable})"),
                Link = pr["html_url"]?.GetValue<string>(),
                Items = [item],
            };
        }

        var list = (JsonArray)await Http.GetJsonAsync($"{api}/repos/{repo}/pulls?state={c.Get("state") ?? "open"}&per_page=50", auth, accept, ct);
        var items = list.Where(p => p is not null).Select(p => p!)
            .Where(p => c.Get("author") is not { } a || string.Equals(p["user"]?["login"]?.GetValue<string>(), a, StringComparison.OrdinalIgnoreCase))
            .Select(ToItem).ToList();
        return new WatchResult
        {
            Status = items.Count == 0 ? "ok" : "info",
            Summary = $"{items.Count} {c.Get("state") ?? "open"} pull request(s) in {repo}",
            Link = $"{(api == "https://api.github.com" ? "https://github.com" : api.Replace("/api/v3", ""))}/{repo}/pulls",
            Items = items,
        };
    }

    private static WatchResultItem ToItem(JsonNode pr) => new()
    {
        Id = pr["number"]?.ToString(),
        Title = $"#{pr["number"]} {pr["title"]?.GetValue<string>()}",
        Status = pr["draft"]?.GetValue<bool>() == true ? "draft" : pr["state"]?.GetValue<string>(),
        Detail = $"{pr["user"]?["login"]?.GetValue<string>()} · {pr["head"]?["ref"]?.GetValue<string>()} → {pr["base"]?["ref"]?.GetValue<string>()}",
        Link = pr["html_url"]?.GetValue<string>(),
    };
}

/// <summary>Shared plumbing for the Azure DevOps providers. Authenticates with a personal access token.</summary>
public abstract class AdoWatchProvider : IWatchProvider
{
    public abstract string Kind { get; }
    public abstract string Description { get; }
    protected abstract Dictionary<string, string> OwnSettings { get; }

    public IReadOnlyDictionary<string, string> Settings
    {
        get
        {
            var d = new Dictionary<string, string>
            {
                ["organization"] = "Azure DevOps organization name (required).",
                ["project"] = "Project name (required).",
                ["patEnv"] = "Environment variable holding a personal access token (default AZURE_DEVOPS_EXT_PAT, then ADO_PAT).",
                ["baseUrl"] = "Server URL (default https://dev.azure.com). Set this for Azure DevOps Server.",
            };
            foreach (var (k, v) in OwnSettings) d[k] = v;
            return d;
        }
    }

    protected static string Base(WatchContext c) =>
        $"{(c.Get("baseUrl") ?? "https://dev.azure.com").TrimEnd('/')}/{Uri.EscapeDataString(c.Require("organization"))}/{Uri.EscapeDataString(c.Require("project"))}";

    protected static AuthenticationHeaderValue Auth(WatchContext c)
    {
        var pat = Http.Token(c, "patEnv", "AZURE_DEVOPS_EXT_PAT", "ADO_PAT")
            ?? throw new InvalidOperationException("No Azure DevOps token: set AZURE_DEVOPS_EXT_PAT or the watch's patEnv setting.");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
    }

    public abstract Task<WatchResult> CheckAsync(WatchContext context, CancellationToken cancellationToken);
}

public sealed class AdoPrWatchProvider : AdoWatchProvider
{
    public override string Kind => "ado-pr";
    public override string Description => "Watch Azure DevOps pull requests (one by id, or the active ones in a project/repository).";
    protected override Dictionary<string, string> OwnSettings => new()
    {
        ["repository"] = "Repository name. Omit for every repository in the project.",
        ["pullRequestId"] = "A single pull request id. Omit to list.",
        ["status"] = "active (default), completed, abandoned or all, when listing.",
    };

    public override async Task<WatchResult> CheckAsync(WatchContext c, CancellationToken ct)
    {
        var root = Base(c);
        var repoPart = c.Get("repository") is { } r ? $"/repositories/{Uri.EscapeDataString(r)}" : "";
        JsonNode[] prs;
        if (c.Get("pullRequestId") is { } id)
            prs = [await Http.GetJsonAsync($"{root}/_apis/git/pullrequests/{id}?api-version=7.1", Auth(c), null, ct)];
        else
        {
            var data = await Http.GetJsonAsync($"{root}/_apis/git{repoPart}/pullrequests?searchCriteria.status={c.Get("status") ?? "active"}&$top=50&api-version=7.1", Auth(c), null, ct);
            prs = (data["value"] as JsonArray)?.Where(x => x is not null).Select(x => x!).ToArray() ?? [];
        }
        var items = prs.Select(pr =>
        {
            var repo = pr["repository"]?["name"]?.GetValue<string>();
            var votes = (pr["reviewers"] as JsonArray)?.Select(v => v?["vote"]?.GetValue<int>() ?? 0).ToList() ?? [];
            var review = votes.Any(v => v < 0) ? "changes requested" : votes.Any(v => v > 0) ? "approved" : "awaiting review";
            return new WatchResultItem
            {
                Id = pr["pullRequestId"]?.ToString(),
                Title = $"!{pr["pullRequestId"]} {pr["title"]?.GetValue<string>()}",
                Status = pr["isDraft"]?.GetValue<bool>() == true ? "draft" : pr["status"]?.GetValue<string>(),
                Detail = $"{pr["createdBy"]?["displayName"]?.GetValue<string>()} · {repo} · {review}",
                Link = $"{root}/_git/{Uri.EscapeDataString(repo ?? "")}/pullrequest/{pr["pullRequestId"]}",
            };
        }).ToList();
        if (c.Get("pullRequestId") is not null && items.Count == 1)
        {
            var st = items[0].Status;
            return new WatchResult { Status = st == "completed" ? "ok" : st == "abandoned" ? "warn" : "info", Summary = $"{items[0].Title} — {st} ({items[0].Detail})", Link = items[0].Link, Items = items };
        }
        return new WatchResult { Status = items.Count == 0 ? "ok" : "info", Summary = $"{items.Count} {c.Get("status") ?? "active"} pull request(s)", Link = $"{root}/_git/{c.Get("repository") ?? ""}/pullrequests", Items = items };
    }
}

public sealed class AdoWorkItemWatchProvider : AdoWatchProvider
{
    public override string Kind => "ado-pbi";
    public override string Description => "Watch Azure DevOps work items (PBIs, bugs, tasks) by id or by a WIQL query.";
    protected override Dictionary<string, string> OwnSettings => new()
    {
        ["ids"] = "Comma-separated work item ids.",
        ["wiql"] = "A WIQL query to run instead of ids, e.g. SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me AND [System.State] <> 'Done'.",
    };

    public override async Task<WatchResult> CheckAsync(WatchContext c, CancellationToken ct)
    {
        var root = Base(c);
        var ids = c.Get("ids");
        if (ids is null)
        {
            var wiql = c.Require("wiql");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{root}/_apis/wit/wiql?$top=50&api-version=7.1")
            { Content = new StringContent(new JsonObject { ["query"] = wiql }.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = Auth(c);
            using var resp = await Http.Client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Http.Truncate(body, 200)}");
            ids = string.Join(',', (JsonNode.Parse(body)?["workItems"] as JsonArray)?.Select(w => w?["id"]?.ToString()) ?? []);
            if (ids.Length == 0) return new WatchResult { Status = "ok", Summary = "No matching work items" };
        }
        var data = await Http.GetJsonAsync($"{root}/_apis/wit/workitems?ids={ids}&fields=System.Id,System.Title,System.State,System.WorkItemType,System.AssignedTo&api-version=7.1", Auth(c), null, ct);
        var items = (data["value"] as JsonArray ?? []).Where(x => x is not null).Select(w =>
        {
            var f = w!["fields"]!;
            return new WatchResultItem
            {
                Id = w["id"]?.ToString(),
                Title = $"{f["System.WorkItemType"]?.GetValue<string>()} {w["id"]}: {f["System.Title"]?.GetValue<string>()}",
                Status = f["System.State"]?.GetValue<string>(),
                Detail = f["System.AssignedTo"]?["displayName"]?.GetValue<string>() ?? "Unassigned",
                Link = $"{root}/_workitems/edit/{w["id"]}",
            };
        }).ToList();
        var summary = items.Count == 1 ? $"{items[0].Title} — {items[0].Status}"
            : $"{items.Count} work item(s): " + string.Join(", ", items.GroupBy(i => i.Status).Select(g => $"{g.Count()} {g.Key}"));
        return new WatchResult { Status = "info", Summary = summary, Link = items.Count == 1 ? items[0].Link : $"{root}/_workitems", Items = items };
    }
}

public sealed class AdoPipelineWatchProvider : AdoWatchProvider
{
    public override string Kind => "ado-pipeline";
    public override string Description => "Watch the latest runs of an Azure DevOps pipeline.";
    protected override Dictionary<string, string> OwnSettings => new()
    {
        ["definitionId"] = "Pipeline (build definition) id (required).",
        ["branch"] = "Only runs of this branch, e.g. refs/heads/main.",
        ["top"] = "How many recent runs to list (default 5).",
    };

    public override async Task<WatchResult> CheckAsync(WatchContext c, CancellationToken ct)
    {
        var root = Base(c);
        var url = $"{root}/_apis/build/builds?definitions={c.Require("definitionId")}&$top={c.Get("top") ?? "5"}&queryOrder=queueTimeDescending&api-version=7.1";
        if (c.Get("branch") is { } b) url += "&branchName=" + Uri.EscapeDataString(b);
        var data = await Http.GetJsonAsync(url, Auth(c), null, ct);
        var items = (data["value"] as JsonArray ?? []).Where(x => x is not null).Select(run => new WatchResultItem
        {
            Id = run!["id"]?.ToString(),
            Title = $"{run["definition"]?["name"]?.GetValue<string>()} #{run["buildNumber"]?.GetValue<string>()}",
            Status = run["status"]?.GetValue<string>() == "completed" ? run["result"]?.GetValue<string>() : run["status"]?.GetValue<string>(),
            Detail = $"{run["sourceBranch"]?.GetValue<string>()} · {run["requestedFor"]?["displayName"]?.GetValue<string>()}",
            Link = run["_links"]?["web"]?["href"]?.GetValue<string>(),
        }).ToList();
        if (items.Count == 0) return new WatchResult { Status = "info", Summary = "No runs found" };
        return new WatchResult
        {
            Status = CommandWatchProvider.NormalizeStatus(items[0].Status),
            Summary = $"{items[0].Title} — {items[0].Status}",
            Link = items[0].Link,
            Items = items,
        };
    }
}

/// <summary>
/// The pull requests of a local git repository. This is what the Repositories page's "Pull Requests" tab shows:
/// setting that tab up for a repository creates a watch of this kind, so the list, its refresh interval and its
/// change notifications are all ordinary Watching behaviour.
/// </summary>
public sealed class RepoPullRequestWatchProvider(Func<string> defaultCommand) : IWatchProvider
{
    public const string KindName = "repo-pr";
    public string Kind => KindName;
    public string Description => "Pull requests of a local git repository, listed by the GitHub CLI or by your own command.";
    public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["path"] = "Folder of the git repository (required).",
        ["command"] = "Command that prints the pull requests as JSON; runs in the repository folder. Empty uses repositories.pullRequestCommand, then the GitHub CLI: gh pr list.",
        ["limit"] = "How many pull requests to fetch with the GitHub CLI (default 50).",
        ["me"] = "Your user name, to mark pull requests as yours when the command does not say. Empty asks the GitHub CLI.",
    };

    private static readonly Dictionary<string, (string? Login, DateTime At)> LoginCache = new();

    public async Task<WatchResult> CheckAsync(WatchContext c, CancellationToken ct)
    {
        var path = c.Require("path");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Repository folder not found: {path}");
        var env = new Dictionary<string, string> { ["AVAMUX_REPO_PATH"] = path, ["AVAMUX_REPO_NAME"] = Path.GetFileName(path.TrimEnd('/')), ["GH_PROMPT_DISABLED"] = "1", ["NO_COLOR"] = "1" };
        var command = c.Get("command") ?? (defaultCommand() is { Length: > 0 } d ? d : null);
        var me = c.Get("me");
        Shell.RunResult run;
        if (command is null)
        {
            var limit = int.TryParse(c.Get("limit"), out var n) && n > 0 ? n : 50;
            try
            {
                run = await Shell.RunAsync("gh", ["pr", "list", "--state", "all", "--limit", limit.ToString(),
                    "--json", "number,title,state,isDraft,author,url,headRefName,baseRefName,updatedAt,reviewDecision"], path, env, c.Timeout, ct);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("The GitHub CLI (gh) is not installed. Install it, or set a command that lists this repository's pull requests.");
            }
            me ??= await GhLoginAsync(path, env, ct);
        }
        else run = await Shell.RunAsync(command, path, env, c.Timeout, ct);
        if (run.TimedOut) throw new TimeoutException($"Listing pull requests timed out after {c.Timeout.TotalSeconds:0}s.");
        if (run.ExitCode != 0) throw new InvalidOperationException(Http.Truncate((run.Stderr.Trim().Length > 0 ? run.Stderr : run.Stdout).Trim(), 400));
        return Parse(run.Stdout, me);
    }

    private static async Task<string?> GhLoginAsync(string path, Dictionary<string, string> env, CancellationToken ct)
    {
        lock (LoginCache)
            if (LoginCache.TryGetValue("gh", out var hit) && DateTime.Now - hit.At < TimeSpan.FromMinutes(30)) return hit.Login;
        string? login = null;
        try
        {
            var r = await Shell.RunAsync("gh", ["api", "user", "--jq", ".login"], path, env, TimeSpan.FromSeconds(20), ct);
            if (r.ExitCode == 0 && r.Stdout.Trim().Length > 0) login = r.Stdout.Trim();
        }
        catch { /* not signed in: nothing is marked as mine */ }
        lock (LoginCache) LoginCache["gh"] = (login, DateTime.Now);
        return login;
    }

    private static string? Text(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => Text(o["login"] ?? o["displayName"] ?? o["name"] ?? o["uniqueName"] ?? o["username"]),
        _ => n.ToString(),
    };

    private static bool? Flag(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>
    /// Accepts either the usual watch result object, or a bare JSON array of pull requests in the shape the
    /// GitHub CLI prints (number, title, state, isDraft, author, url, headRefName, baseRefName, updatedAt) with
    /// lenient alternative names, so scripts for other hosts need little massaging.
    /// </summary>
    public static WatchResult Parse(string stdout, string? me)
    {
        var text = stdout.Trim();
        if (text.Length == 0) return Summarize([], null);
        if (!text.StartsWith('['))
        {
            var result = CommandWatchProvider.ParseOutput(text, "", 0);
            foreach (var i in result.Items) Fill(i, me);
            return string.IsNullOrWhiteSpace(result.Summary) ? Summarize(result.Items, result.Link) : result;
        }
        if (JsonNode.Parse(text) is not JsonArray array) throw new InvalidOperationException("Expected a JSON array of pull requests.");
        var items = new List<WatchResultItem>();
        foreach (var pr in array.OfType<JsonObject>())
        {
            var number = Text(pr["number"] ?? pr["id"] ?? pr["pullRequestId"] ?? pr["iid"]);
            var state = (Text(pr["state"] ?? pr["status"]) ?? "open").ToLowerInvariant();
            if (state == "opened" || state == "active") state = "open";
            if (state == "completed") state = "merged";
            var draft = Flag(pr["isDraft"] ?? pr["draft"]) == true;
            var review = Text(pr["reviewDecision"])?.ToLowerInvariant().Replace('_', ' ');
            var head = Text(pr["headRefName"] ?? pr["sourceRefName"] ?? pr["source_branch"] ?? pr["branch"])?.Replace("refs/heads/", "");
            var target = Text(pr["baseRefName"] ?? pr["targetRefName"] ?? pr["target_branch"])?.Replace("refs/heads/", "");
            var updated = Text(pr["updatedAt"] ?? pr["updated_at"] ?? pr["creationDate"]);
            var author = Text(pr["author"] ?? pr["createdBy"] ?? pr["user"]);
            var item = new WatchResultItem
            {
                Id = number, Title = (number is null ? "" : $"#{number} ") + (Text(pr["title"]) ?? "(untitled)"),
                Status = state == "open" && draft ? "draft" : state == "open" && !string.IsNullOrEmpty(review) ? review : state,
                Link = Text(pr["url"] ?? pr["html_url"] ?? pr["web_url"] ?? pr["link"]), Author = author,
                Mine = Flag(pr["mine"]), Active = Flag(pr["active"]) ?? state == "open",
                Detail = string.Join(" · ", new[]
                {
                    author, head is null ? null : target is null ? head : $"{head} → {target}",
                    DateTime.TryParse(updated, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? "updated " + when.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null,
                }.Where(x => !string.IsNullOrWhiteSpace(x))),
            };
            Fill(item, me);
            items.Add(item);
        }
        return Summarize(items, null);
    }

    private static void Fill(WatchResultItem item, string? me)
    {
        item.Mine ??= me is not null && item.Author is not null && string.Equals(item.Author, me, StringComparison.OrdinalIgnoreCase);
        item.Active ??= (item.Status ?? "open").Trim().ToLowerInvariant() is not ("merged" or "closed" or "abandoned" or "completed" or "declined" or "done");
    }

    private static WatchResult Summarize(List<WatchResultItem> items, string? link)
    {
        var open = items.Count(i => i.Active == true);
        var mine = items.Count(i => i.Active == true && i.Mine == true);
        return new WatchResult
        {
            Status = open > 0 ? "info" : "ok", Link = link, Items = items,
            Summary = items.Count == 0 ? "No pull requests" : open == 0 ? $"No open pull requests ({items.Count} finished)" : $"{open} open" + (mine > 0 ? $", {mine} mine" : ""),
        };
    }
}
