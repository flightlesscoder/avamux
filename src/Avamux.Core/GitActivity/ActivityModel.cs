using System.Text.Json.Serialization;

namespace Avamux.Core.GitActivity;

/// <summary>One repository Git Activity reads: listed from a host, found in a folder, or given by URL.</summary>
public sealed class ActivityRepo
{
    /// <summary>Stable id: kind, then the host's name or the folder.</summary>
    public string Key { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>Shown name: owner/name on GitHub, project/name on Azure DevOps, the folder for local repositories.</summary>
    public string Name { get; set; } = "";
    /// <summary>Where it is fetched from (remote repositories).</summary>
    public string? CloneUrl { get; set; }
    /// <summary>The working copy (local repositories).</summary>
    public string? LocalPath { get; set; }
    /// <summary>The repository's page, for links to commits and branches.</summary>
    public string? WebUrl { get; set; }
    /// <summary>Branch name without refs/heads/.</summary>
    public string? DefaultBranch { get; set; }
    public long? SizeKb { get; set; }
    public bool Archived { get; set; }
    /// <summary>Last push, when the host says (GitHub): repositories untouched in the period are not fetched at all.</summary>
    public DateTimeOffset? PushedAt { get; set; }

    /// <summary>What two listings of the same repository have in common (a local clone and the organisation's copy).</summary>
    [JsonIgnore]
    public string Identity => (Repos.RepoService.ToWebUrl(CloneUrl) ?? WebUrl)?.ToLowerInvariant().TrimEnd('/') ?? Key;
}

/// <summary>The changes of one commit to one kind of file in one set of frameworks.</summary>
public sealed class ActivityPart
{
    /// <summary>File key (see <see cref="Languages.KeyFor"/>).</summary>
    [JsonPropertyName("k")] public string Key { get; set; } = "";
    /// <summary>Frameworks joined with '|', or empty.</summary>
    [JsonPropertyName("w")] public string Frameworks { get; set; } = "";
    /// <summary>True for test files.</summary>
    [JsonPropertyName("t")] public bool Test { get; set; }
    [JsonPropertyName("f")] public int Files { get; set; }
    [JsonPropertyName("a")] public int Added { get; set; }
    [JsonPropertyName("d")] public int Deleted { get; set; }
}

/// <summary>A commit as Git Activity counts it.</summary>
public sealed class ActivityCommit
{
    [JsonPropertyName("h")] public string Hash { get; set; } = "";
    /// <summary>Set when loaded; not saved (each repository has its own file).</summary>
    [JsonIgnore] public string RepoKey { get; set; } = "";
    [JsonPropertyName("an")] public string AuthorName { get; set; } = "";
    [JsonPropertyName("ae")] public string AuthorEmail { get; set; } = "";
    /// <summary>The person credited (differs from the author when an AI agent authored the commit).</summary>
    [JsonPropertyName("pn")] public string PersonName { get; set; } = "";
    [JsonPropertyName("pe")] public string PersonEmail { get; set; } = "";
    /// <summary>When it was written, in the author's own time zone.</summary>
    [JsonPropertyName("at")] public DateTimeOffset When { get; set; }
    [JsonPropertyName("s")] public string Subject { get; set; } = "";
    /// <summary>The branch it is on (a feature branch is preferred over a mainline branch that also has it).</summary>
    [JsonPropertyName("b")] public string Branch { get; set; } = "";
    [JsonPropertyName("m")] public bool Merge { get; set; }
    /// <summary>The oldest commit fetched: what it changed cannot be told, so nothing is counted for it.</summary>
    [JsonPropertyName("x")] public bool Boundary { get; set; }
    [JsonPropertyName("ai")] public string[] AiTools { get; set; } = [];
    [JsonPropertyName("am")] public string[] AiModels { get; set; } = [];
    /// <summary>True when an AI agent is the author.</summary>
    [JsonPropertyName("ag")] public bool Agent { get; set; }
    [JsonPropertyName("bot")] public bool Bot { get; set; }
    [JsonPropertyName("p")] public List<ActivityPart> Parts { get; set; } = [];

    [JsonIgnore] public bool WithAi => AiTools.Length > 0;
    [JsonIgnore] public int Files => Parts.Sum(p => p.Files);
    [JsonIgnore] public int Added => Parts.Sum(p => p.Added);
    [JsonIgnore] public int Deleted => Parts.Sum(p => p.Deleted);
}

/// <summary>What was read from one repository, saved between runs.</summary>
public sealed class RepoActivity
{
    public string Key { get; set; } = "";
    /// <summary>What decided how the commits were read (settings that change the result): a change means reading again.</summary>
    public string Signature { get; set; } = "";
    /// <summary>The branch tips last read: unchanged tips need no fetch and no reading.</summary>
    public string Refs { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
    /// <summary>ok, inactive (nothing in the period), error.</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    /// <summary>lines or files.</summary>
    public string Mode { get; set; } = "lines";
    public int Branches { get; set; }
    /// <summary>The branch tips read (commit ids): the next read only looks at commits that are not below one of these.</summary>
    public List<string> Tips { get; set; } = [];
    public List<string> Frameworks { get; set; } = [];
    public List<ActivityCommit> Commits { get; set; } = [];
}

/// <summary>Links to a repository's web pages, for the hosts that are known.</summary>
public static class WebLinks
{
    public static string? Commit(string? web, string hash) => web is null ? null
        : IsGitLab(web) ? $"{web}/-/commit/{hash}" : IsBitbucket(web) ? $"{web}/commits/{hash}" : $"{web}/commit/{hash}";

    public static string? Branch(string? web, string branch) => web is null ? null
        : IsAzure(web) ? $"{web}?version=GB{Uri.EscapeDataString(branch)}"
        : IsGitLab(web) ? $"{web}/-/tree/{branch}" : IsBitbucket(web) ? $"{web}/branch/{branch}" : $"{web}/tree/{branch}";

    private static bool IsAzure(string u) => u.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) || u.Contains(".visualstudio.com", StringComparison.OrdinalIgnoreCase) || u.Contains("/_git/");
    private static bool IsGitLab(string u) => u.Contains("gitlab", StringComparison.OrdinalIgnoreCase);
    private static bool IsBitbucket(string u) => u.Contains("bitbucket", StringComparison.OrdinalIgnoreCase);
}
