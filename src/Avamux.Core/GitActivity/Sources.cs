using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Avamux.Core.GitActivity;

/// <summary>
/// The adapter for one kind of source: how to list its repositories and how git signs in to fetch them. Local
/// sources need no sign-in; GitHub uses a token (environment, then the GitHub CLI); Azure DevOps a PAT or the
/// Azure CLI's sign-in (az login), handed to git as an HTTP header through the environment, never on a command line.
/// </summary>
public interface IActivitySourceAdapter
{
    string Kind { get; }
    string Description { get; }
    Task<List<ActivityRepo>> DiscoverAsync(ActivitySource source, CancellationToken ct);
    /// <summary>Environment for git when fetching one of this source's repositories (empty: git's own credentials).</summary>
    Task<Dictionary<string, string>> GitEnvAsync(ActivitySource source, ActivityRepo repo, CancellationToken ct);
}

public static class ActivitySources
{
    public static readonly IReadOnlyList<IActivitySourceAdapter> All = [new GitHubSource(), new AzureDevOpsSource(), new LocalSource(), new GitUrlSource(), new RepositoriesSource()];

    public static IActivitySourceAdapter For(string kind) =>
        All.FirstOrDefault(a => string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"Unknown source kind '{kind}'. Kinds: {string.Join(", ", All.Select(a => a.Kind))}.");

    /// <summary>The repositories on the Repositories page, for the "repositories" source. Set by the app.</summary>
    public static Func<IEnumerable<(string Name, string Path)>> RepositoriesPage { get; set; } = () => [];

    internal static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("avamux-git-activity");
        return c;
    }

    internal static async Task<(JsonNode Body, HttpResponseHeaders Headers)> GetAsync(string url, AuthenticationHeaderValue? auth, CancellationToken ct, string accept = "application/json")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth is not null) req.Headers.Authorization = auth;
        req.Headers.Accept.ParseAdd(accept);
        using var resp = await Client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} from {new Uri(url).Host}: {(body.Length > 240 ? body[..240] + "…" : body)}", null, resp.StatusCode);
        if (resp.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
            throw new HttpRequestException("Got a sign-in page instead of data: check the token, or sign in again.");
        return (JsonNode.Parse(body) ?? throw new HttpRequestException("Empty response."), resp.Headers);
    }

    /// <summary>Whether a repository passes the source's include and exclude lists (matched on the full and the short name).</summary>
    public static bool Wanted(ActivitySource s, string fullName)
    {
        var shortName = fullName[(fullName.LastIndexOf('/') + 1)..];
        bool Hit(List<string> globs) => Glob.Any(globs, fullName) || Glob.Any(globs, shortName);
        return (s.Include.Count == 0 || Hit(s.Include)) && !Hit(s.Exclude);
    }

    /// <summary>git configuration given through the environment (GIT_CONFIG_COUNT…), so secrets stay off command lines.</summary>
    public static Dictionary<string, string> GitConfigEnv(params (string Key, string Value)[] entries)
    {
        var env = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = entries.Length.ToString() };
        for (var i = 0; i < entries.Length; i++) { env[$"GIT_CONFIG_KEY_{i}"] = entries[i].Key; env[$"GIT_CONFIG_VALUE_{i}"] = entries[i].Value; }
        return env;
    }

    internal static string Origin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}/" : url;

    private static readonly Dictionary<string, (string? Value, DateTime At)> Tokens = new();

    /// <summary>A token from a command (gh auth token, az account get-access-token), remembered for a while.</summary>
    internal static async Task<string?> CommandTokenAsync(string cacheKey, string file, string[] args, TimeSpan keep, CancellationToken ct)
    {
        lock (Tokens) if (Tokens.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < keep) return hit.Value;
        string? token = null;
        try
        {
            var r = await Shell.RunAsync(file, args, null, null, TimeSpan.FromSeconds(60), ct);
            if (r.ExitCode == 0 && r.Stdout.Trim() is { Length: > 0 } t && !t.Contains(' ')) token = t;
            else Logging.Log.Warn("activity", $"{file} gave no token: {(r.Stderr.Trim().Length > 0 ? r.Stderr.Trim() : "exit " + r.ExitCode)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logging.Log.Warn("activity", $"Could not run {file}: {ex.Message}"); }
        lock (Tokens) Tokens[cacheKey] = (token, DateTime.UtcNow);
        return token;
    }

    internal static string? Env(string? preferred, params string[] defaults) =>
        (preferred is { Length: > 0 } p ? [p] : defaults).Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    internal static string Expand(string path, string home) => Path.GetFullPath(path.StartsWith('~') ? home + path[1..] : path);
}

/// <summary>Every repository of a GitHub organisation or user (github.com or GitHub Enterprise).</summary>
public sealed class GitHubSource : IActivitySourceAdapter
{
    public string Kind => ActivitySource.GitHub;
    public string Description => "Every repository of a GitHub organisation or user. Signs in with GITHUB_TOKEN / GH_TOKEN, or the GitHub CLI (gh auth login).";

    private static string Api(ActivitySource s) => (s.ApiUrl is { Length: > 0 } a ? a : "https://api.github.com").TrimEnd('/');
    private static string Web(ActivitySource s) => Api(s) == "https://api.github.com" ? "https://github.com" : Api(s).Replace("/api/v3", "", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> TokenAsync(ActivitySource s, CancellationToken ct)
    {
        if (ActivitySources.Env(s.TokenEnv, "GITHUB_TOKEN", "GH_TOKEN") is { } env) return env;
        var host = new Uri(Web(s)).Host;
        return await ActivitySources.CommandTokenAsync("gh:" + host, "gh", host == "github.com" ? ["auth", "token"] : ["auth", "token", "--hostname", host], TimeSpan.FromMinutes(30), ct);
    }

    public async Task<List<ActivityRepo>> DiscoverAsync(ActivitySource s, CancellationToken ct)
    {
        var owner = (s.Owner ?? "").Trim();
        var web = Web(s);
        if (s.Repositories.Count > 0)
            return s.Repositories.Where(r => r.Trim().Length > 0).Select(r => r.Contains('/') ? r.Trim() : $"{owner}/{r.Trim()}")
                .Select(full => Repo(s, full, $"{web}/{full}.git", $"{web}/{full}", null, null, false, null)).ToList();
        if (owner.Length == 0) throw new InvalidOperationException("Set the GitHub organisation or user (owner).");
        var token = await TokenAsync(s, ct);
        var auth = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        var api = Api(s);
        string first;
        try { await ActivitySources.GetAsync($"{api}/orgs/{Uri.EscapeDataString(owner)}", auth, ct); first = $"{api}/orgs/{Uri.EscapeDataString(owner)}/repos?type=all&per_page=100"; }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Not an organisation: a user. One's own private repositories are only listed through /user/repos.
            string? me = null;
            if (auth is not null) try { me = (await ActivitySources.GetAsync($"{api}/user", auth, ct)).Body["login"]?.GetValue<string>(); } catch { /* public only */ }
            first = string.Equals(me, owner, StringComparison.OrdinalIgnoreCase) ? $"{api}/user/repos?affiliation=owner&per_page=100" : $"{api}/users/{Uri.EscapeDataString(owner)}/repos?type=owner&per_page=100";
        }
        var list = new List<ActivityRepo>();
        for (var url = first; url is not null && list.Count < 20000;)
        {
            var (body, headers) = await ActivitySources.GetAsync(url, auth, ct, "application/vnd.github+json");
            foreach (var r in body.AsArray().OfType<JsonObject>())
            {
                var full = r["full_name"]?.GetValue<string>() ?? "";
                var archived = r["archived"]?.GetValue<bool>() == true;
                var fork = r["fork"]?.GetValue<bool>() == true;
                if (full.Length == 0 || archived && !s.IncludeArchived || fork && !s.IncludeForks || !ActivitySources.Wanted(s, full)) continue;
                DateTimeOffset? pushed = DateTimeOffset.TryParse(r["pushed_at"]?.GetValue<string>(), out var p) ? p : null;
                list.Add(Repo(s, full, r["clone_url"]?.GetValue<string>() ?? $"{web}/{full}.git", r["html_url"]?.GetValue<string>() ?? $"{web}/{full}",
                    r["default_branch"]?.GetValue<string>(), r["size"]?.GetValue<long>(), archived, pushed));
            }
            url = NextLink(headers);
        }
        return list;
    }

    private static ActivityRepo Repo(ActivitySource s, string full, string clone, string web, string? branch, long? size, bool archived, DateTimeOffset? pushed) => new()
    {
        Key = $"github:{new Uri(Web(s)).Host}/{full}".ToLowerInvariant(), SourceId = s.Id, Kind = ActivitySource.GitHub, Name = full,
        CloneUrl = clone, WebUrl = web, DefaultBranch = branch, SizeKb = size, Archived = archived, PushedAt = pushed,
    };

    private static string? NextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(",", values).Split(','))
            if (part.Contains("rel=\"next\"")) return part[(part.IndexOf('<') + 1)..part.IndexOf('>')];
        return null;
    }

    public async Task<Dictionary<string, string>> GitEnvAsync(ActivitySource s, ActivityRepo repo, CancellationToken ct)
    {
        if (await TokenAsync(s, ct) is not { } token || repo.CloneUrl is null) return [];
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        return ActivitySources.GitConfigEnv(($"http.{ActivitySources.Origin(repo.CloneUrl)}.extraheader", "Authorization: Basic " + basic));
    }
}

/// <summary>Every git repository of an Azure DevOps organisation, or of one of its projects.</summary>
public sealed class AzureDevOpsSource : IActivitySourceAdapter
{
    /// <summary>Azure DevOps' resource id, for tokens from az account get-access-token.</summary>
    public const string Resource = "499b84ac-1321-427f-aa17-267ca6aa798d";

    public string Kind => ActivitySource.AzureDevOps;
    public string Description => "Every git repository of an Azure DevOps organisation or project. Signs in with az login (the Azure CLI), or a PAT in AZURE_DEVOPS_EXT_PAT / ADO_PAT.";

    private static string Base(ActivitySource s) => (s.BaseUrl is { Length: > 0 } b ? b : "https://dev.azure.com").TrimEnd('/');

    private static async Task<AuthenticationHeaderValue> AuthAsync(ActivitySource s, CancellationToken ct)
    {
        if (ActivitySources.Env(s.PatEnv, "AZURE_DEVOPS_EXT_PAT", "ADO_PAT") is { } pat)
            return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat)));
        var token = await ActivitySources.CommandTokenAsync("az", "az", ["account", "get-access-token", "--resource", Resource, "--query", "accessToken", "-o", "tsv"], TimeSpan.FromMinutes(40), ct)
            ?? throw new InvalidOperationException("No Azure DevOps sign-in: run az login, or set AZURE_DEVOPS_EXT_PAT to a personal access token.");
        return new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<ActivityRepo>> DiscoverAsync(ActivitySource s, CancellationToken ct)
    {
        var org = (s.Organization ?? "").Trim();
        if (org.Length == 0) throw new InvalidOperationException("Set the Azure DevOps organisation.");
        var auth = await AuthAsync(s, ct);
        var scope = $"{Base(s)}/{Uri.EscapeDataString(org)}" + (string.IsNullOrWhiteSpace(s.Project) ? "" : "/" + Uri.EscapeDataString(s.Project.Trim()));
        var (body, _) = await ActivitySources.GetAsync($"{scope}/_apis/git/repositories?api-version=7.1", auth, ct);
        var wanted = s.Repositories.Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        var list = new List<ActivityRepo>();
        foreach (var r in (body["value"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var name = r["name"]?.GetValue<string>() ?? "";
            var project = r["project"]?["name"]?.GetValue<string>() ?? "";
            var full = $"{project}/{name}";
            if (r["isDisabled"]?.GetValue<bool>() == true || r["isInMaintenance"]?.GetValue<bool>() == true) continue;
            if (r["isFork"]?.GetValue<bool>() == true && !s.IncludeForks) continue;
            if (wanted.Count > 0 ? !wanted.Any(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase) || string.Equals(w, full, StringComparison.OrdinalIgnoreCase)) : !ActivitySources.Wanted(s, full)) continue;
            var size = r["size"]?.GetValue<long>();
            if (size == 0) continue;   // never pushed to
            list.Add(new ActivityRepo
            {
                Key = $"ado:{org}/{full}".ToLowerInvariant(), SourceId = s.Id, Kind = Kind, Name = full,
                CloneUrl = r["remoteUrl"]?.GetValue<string>(), WebUrl = r["webUrl"]?.GetValue<string>(),
                DefaultBranch = r["defaultBranch"]?.GetValue<string>()?.Replace("refs/heads/", ""), SizeKb = size / 1024,
            });
        }
        return list;
    }

    public async Task<Dictionary<string, string>> GitEnvAsync(ActivitySource s, ActivityRepo repo, CancellationToken ct)
    {
        if (repo.CloneUrl is null) return [];
        var auth = await AuthAsync(s, ct);
        return ActivitySources.GitConfigEnv(($"http.{ActivitySources.Origin(repo.CloneUrl)}.extraheader", $"Authorization: {auth.Scheme} {auth.Parameter}"));
    }
}

/// <summary>A repository on this computer, or every repository in a folder (looking a few levels down).</summary>
public sealed class LocalSource : IActivitySourceAdapter
{
    public string Kind => ActivitySource.Local;
    public string Description => "Repositories already on this computer: one, or every one in a folder. Their local and remote-tracking branches are read; Fetch updates them first.";

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", "dist", "build", "target", "vendor", "packages", ".venv", "venv", "__pycache__" };

    public async Task<List<ActivityRepo>> DiscoverAsync(ActivitySource s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Path)) throw new InvalidOperationException("Set the folder.");
        var root = ActivitySources.Expand(s.Path.Trim(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder not found: {root}");
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            if (ct.IsCancellationRequested) return;
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) { found.Add(dir); return; }
            if (depth <= 0) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); } catch { return; }
            foreach (var sub in subs.Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || Skip.Contains(name)) continue;
                Walk(sub, depth - 1);
            }
        }
        await Task.Run(() => Walk(root, Math.Clamp(s.Depth, 0, 8)), ct);
        var list = new List<ActivityRepo>();
        foreach (var dir in found)
        {
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            var name = rel == "." ? Path.GetFileName(dir) : rel;
            if (!ActivitySources.Wanted(s, name)) continue;
            list.Add(await LocalRepo(s.Id, Kind, dir, name, ct));
        }
        return list;
    }

    internal static async Task<ActivityRepo> LocalRepo(string sourceId, string kind, string dir, string name, CancellationToken ct)
    {
        var remote = await Shell.RunAsync("git", ["remote", "get-url", "origin"], dir, null, TimeSpan.FromSeconds(10), ct);
        var url = remote.ExitCode == 0 ? remote.Stdout.Trim() : null;
        return new ActivityRepo { Key = "local:" + dir, SourceId = sourceId, Kind = kind, Name = name, LocalPath = dir, WebUrl = Repos.RepoService.ToWebUrl(url), CloneUrl = url };
    }

    public Task<Dictionary<string, string>> GitEnvAsync(ActivitySource s, ActivityRepo repo, CancellationToken ct) => Task.FromResult(new Dictionary<string, string>());
}

/// <summary>Repositories given by clone URL, fetched with whatever credentials git already has.</summary>
public sealed class GitUrlSource : IActivitySourceAdapter
{
    public string Kind => ActivitySource.Git;
    public string Description => "Any git repositories by clone URL (GitLab, Bitbucket, a server of your own…), using git's own credentials.";

    public Task<List<ActivityRepo>> DiscoverAsync(ActivitySource s, CancellationToken ct) => Task.FromResult(s.Repositories.Select(u => u.Trim()).Where(u => u.Length > 0).Select(u =>
    {
        var web = Repos.RepoService.ToWebUrl(u);
        var path = (web is not null ? new Uri(web).AbsolutePath : u).Trim('/');
        if (path.EndsWith(".git")) path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new ActivityRepo
        {
            Key = "git:" + (web ?? u).ToLowerInvariant(), SourceId = s.Id, Kind = Kind, Name = string.Join('/', parts.TakeLast(2)), CloneUrl = u, WebUrl = web,
        };
    }).ToList());

    public Task<Dictionary<string, string>> GitEnvAsync(ActivitySource s, ActivityRepo repo, CancellationToken ct) => Task.FromResult(new Dictionary<string, string>());
}

/// <summary>The repositories on the Repositories page.</summary>
public sealed class RepositoriesSource : IActivitySourceAdapter
{
    public string Kind => ActivitySource.RepositoriesPage;
    public string Description => "The repositories listed on the Repositories page.";

    public async Task<List<ActivityRepo>> DiscoverAsync(ActivitySource s, CancellationToken ct)
    {
        var list = new List<ActivityRepo>();
        foreach (var (name, path) in ActivitySources.RepositoriesPage())
            if (Directory.Exists(path) && ActivitySources.Wanted(s, name)) list.Add(await LocalSource.LocalRepo(s.Id, ActivitySource.Local, path, name, ct));
        return list;
    }

    public Task<Dictionary<string, string>> GitEnvAsync(ActivitySource s, ActivityRepo repo, CancellationToken ct) => Task.FromResult(new Dictionary<string, string>());
}
