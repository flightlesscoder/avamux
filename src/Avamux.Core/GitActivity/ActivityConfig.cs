using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Avamux.Core.GitActivity;

/// <summary>
/// Git Activity settings: which repositories to look at (organisations, projects, folders, single repositories)
/// and how to read them. Kept in git-activity.json in the data folder, apart from the app's settings, so it can
/// be shared or generated on its own.
/// </summary>
public sealed class GitActivityConfig
{
    /// <summary>How many days back to read (and to show at first). 61 is about two months.</summary>
    public int Days { get; set; } = 61;
    /// <summary>
    /// Read from this date (yyyy-MM-dd) instead of <see cref="Days"/> back, or "all" for the whole history. Choosing
    /// an earlier start in Git Activity sets it.
    /// </summary>
    public string ReadFrom { get; set; } = "";
    /// <summary>How many entries the leaderboards and heatmap rows show.</summary>
    public int TopCount { get; set; } = 15;
    /// <summary>How many repositories are fetched and read at the same time.</summary>
    public int Parallel { get; set; } = 4;
    /// <summary>Minutes between automatic refreshes; 0 refreshes only when asked.</summary>
    public int RefreshMinutes { get; set; }
    /// <summary>
    /// "lines" downloads the files changed in the period, so lines added and removed can be counted. "files" downloads
    /// only names (much less to download for very large organisations): files changed are counted, lines are not.
    /// </summary>
    public string Detail { get; set; } = "lines";
    /// <summary>Repositories larger than this (as the host reports it) are read in "files" detail even when Detail is "lines". 0 means no limit.</summary>
    public int MaxLinesRepoMb { get; set; } = 2000;
    /// <summary>Where the remote repositories are mirrored. Empty means the git-activity/mirrors folder in the data folder.</summary>
    public string MirrorFolder { get; set; } = "";
    /// <summary>Branch names (globs, case-insensitive; * stays within one level, ** crosses levels) that count as mainline.</summary>
    public List<string> MainlineBranches { get; set; } =
    [
        "main", "master", "trunk", "dev", "develop", "development", "devel", "test", "testing", "qa", "uat", "stage", "staging",
        "release", "releases", "release/**", "releases/**", "prod", "production", "live", "hotfix", "hotfixes", "integration", "default",
    ];
    /// <summary>Show only contributors with a commit in the last this many days of the range, to begin with (0: everyone).</summary>
    public int ActivePeopleDays { get; set; }
    /// <summary>Show only repositories with a commit in the last this many days of the range, to begin with (0: all).</summary>
    public int ActiveReposDays { get; set; }
    /// <summary>Hide bot accounts (dependabot, renovate, build services…) by default. AI agents are not bots for this.</summary>
    public bool HideBots { get; set; } = true;
    /// <summary>Merge identities that share a name (people who commit from more than one email address).</summary>
    public bool MergeByName { get; set; } = true;
    /// <summary>Contributors (name or email) left out of everything.</summary>
    public List<string> ExcludedContributors { get; set; } = [];
    /// <summary>One person under several names or addresses: shown name → the names and addresses that are them.</summary>
    public Dictionary<string, List<string>> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paths (globs) whose changes are not counted: lock files, minified and generated files.</summary>
    public List<string> ExcludePaths { get; set; } =
    [
        "**/package-lock.json", "**/yarn.lock", "**/pnpm-lock.yaml", "**/*.lock", "**/packages.lock.json", "**/*.min.js", "**/*.min.css", "**/*.map",
        "**/node_modules/**", "**/dist/**", "**/bin/**", "**/obj/**", "**/*.Designer.cs", "**/*.designer.cs", "**/*.g.cs", "**/*.g.i.cs", "**/Migrations/*.Designer.cs",
        "**/*.snap", "**/__snapshots__/**", "**/vendor/**", "**/go.sum",
    ];
    /// <summary>Extra or replacement file types: ".ext" (or a file name) → language.</summary>
    public Dictionary<string, string> Languages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Extra ways to recognise AI help in commits.</summary>
    public List<AiRule> AiRules { get; set; } = [];
    /// <summary>Extra frameworks to recognise.</summary>
    public List<FrameworkRule> Frameworks { get; set; } = [];
    /// <summary>Repositories (their key, shown under Git Activity Settings) that are not read.</summary>
    public List<string> DisabledRepositories { get; set; } = [];
    public List<ActivitySource> Sources { get; set; } = [];
}

/// <summary>Somewhere repositories come from.</summary>
public sealed class ActivitySource
{
    public const string GitHub = "github", AzureDevOps = "azure-devops", Local = "local", Git = "git", RepositoriesPage = "repositories";
    public static readonly string[] Kinds = [GitHub, AzureDevOps, Local, Git, RepositoriesPage];

    public string Id { get; set; } = "";
    /// <summary>github, azure-devops, local, git or repositories (the ones on the Repositories page).</summary>
    public string Kind { get; set; } = GitHub;
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>GitHub: the organisation or user.</summary>
    public string? Owner { get; set; }
    /// <summary>GitHub: API base (default https://api.github.com; GitHub Enterprise: https://host/api/v3).</summary>
    public string? ApiUrl { get; set; }
    /// <summary>GitHub: environment variable with a token (default GITHUB_TOKEN, then GH_TOKEN, then `gh auth token`).</summary>
    public string? TokenEnv { get; set; }

    /// <summary>Azure DevOps: the organisation.</summary>
    public string? Organization { get; set; }
    /// <summary>Azure DevOps: one project; empty means every project in the organisation.</summary>
    public string? Project { get; set; }
    /// <summary>Azure DevOps: server URL (default https://dev.azure.com).</summary>
    public string? BaseUrl { get; set; }
    /// <summary>Azure DevOps: environment variable with a PAT (default AZURE_DEVOPS_EXT_PAT, then ADO_PAT, then `az account get-access-token`).</summary>
    public string? PatEnv { get; set; }

    /// <summary>Local: a repository, or a folder with repositories somewhere below it.</summary>
    public string? Path { get; set; }
    /// <summary>Local: how many folders deep to look for repositories.</summary>
    public int Depth { get; set; } = 3;
    /// <summary>Local (and repositories): run git fetch before reading, so the remote branches are current.</summary>
    public bool Fetch { get; set; }

    /// <summary>
    /// GitHub: owner/name or name. Azure DevOps: project/name or name. Git: clone URLs. When given, only these are
    /// used and nothing is listed from the host.
    /// </summary>
    public List<string> Repositories { get; set; } = [];
    /// <summary>Only repositories whose name matches one of these globs (empty: all).</summary>
    public List<string> Include { get; set; } = [];
    /// <summary>Repositories whose name matches one of these globs are left out.</summary>
    public List<string> Exclude { get; set; } = [];
    public bool IncludeArchived { get; set; }
    public bool IncludeForks { get; set; }

    [JsonIgnore]
    public string Title => Name.Length > 0 ? Name : Kind switch
    {
        GitHub => $"GitHub {Owner}", AzureDevOps => $"Azure DevOps {Organization}{(string.IsNullOrEmpty(Project) ? "" : "/" + Project)}",
        Local => Path ?? "Local folder", Git => Repositories.FirstOrDefault() ?? "Git", RepositoriesPage => "Repositories page", _ => Kind,
    };
}

/// <summary>A way of recognising AI help: a regular expression over the author, committer, or message.</summary>
public sealed class AiRule
{
    /// <summary>The tool to credit, e.g. "Claude Code".</summary>
    public string Tool { get; set; } = "";
    /// <summary>Case-insensitive regular expression.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>author, committer, message, or any.</summary>
    public string Field { get; set; } = "any";
    /// <summary>True when the identity that matches is the AI itself (it authored the commit) rather than an assistant.</summary>
    public bool Agent { get; set; }
}

/// <summary>A framework, recognised from a project's dependencies or from the files in it.</summary>
public sealed class FrameworkRule
{
    public string Name { get; set; } = "";
    /// <summary>Front end, Back end, Mobile, Desktop, Data &amp; ML, Testing, Infrastructure, Library…</summary>
    public string Category { get; set; } = "Library";
    /// <summary>npm, NuGet, PyPI, Maven, Go, Cargo, gem, Composer or pub packages (globs) that mean the framework.</summary>
    public List<string> Packages { get; set; } = [];
    /// <summary>.NET project SDKs or MSBuild switches (UseMaui, UseWPF…) that mean the framework.</summary>
    public List<string> Sdks { get; set; } = [];
    /// <summary>Files (globs) that mean the project uses the framework (next.config.*, angular.json).</summary>
    public List<string> Markers { get; set; } = [];
    /// <summary>Files (globs) that belong to the framework wherever they are (*.tf, Dockerfile, .github/workflows/**).</summary>
    public List<string> Files { get; set; } = [];
}

/// <summary>Glob matching for branch names and paths: * within one level, ** across levels, ? one character.</summary>
public static class Glob
{
    private static readonly Dictionary<string, Regex> Cache = new();

    public static Regex ToRegex(string glob)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(glob, out var hit)) return hit;
            var sb = new System.Text.StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    // "**/" also matches nothing, so **/x matches x at the top.
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); } else sb.Append(".*");
                }
                else if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return Cache[glob] = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }
    }

    public static bool Match(string glob, string text) => ToRegex(glob.Trim()).IsMatch(text);

    public static bool Any(IEnumerable<string> globs, string text) => globs.Any(g => g.Trim().Length > 0 && Match(g, text));
}
