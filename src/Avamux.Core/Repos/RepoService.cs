using Avamux.Core.Config;

namespace Avamux.Core.Repos;

public sealed class RepoEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class RepoState { public List<RepoEntry> Items { get; set; } = []; }

/// <param name="Index">Staged status letter (or space).</param>
/// <param name="WorkTree">Unstaged status letter (or space). '?' means untracked.</param>
public sealed record RepoChange(string RepoId, string RepoName, string Path, char Index, char WorkTree, string? OriginalPath)
{
    public bool Staged => Index is not (' ' or '?');
    public bool Unstaged => WorkTree != ' ';
    public bool Untracked => Index == '?';
    public string Code => $"{Index}{WorkTree}".Replace(' ', '·');
    public string Description => Untracked ? "untracked" : (Index, WorkTree) switch
    {
        ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => "conflict",
        ('A', _) => "added", ('D', _) or (_, 'D') => "deleted", ('R', _) => "renamed", ('C', _) => "copied", _ => "modified",
    };
}

public sealed record RepoStatus(string Id, string Name, string Path, string? Branch, string? Upstream, int Ahead, int Behind,
    IReadOnlyList<RepoChange> Changes, string? Error);

public sealed record RepoCommit(string Hash, string Author, string Date, string Subject);

public sealed record RepoBranch(string Name, bool IsRemote, bool IsCurrent, string? Upstream, string Track, string Date, string Subject);

/// <param name="Status">Git's status letter: A, M, D, R, C, T.</param>
public sealed record RepoCommitFile(string Status, string Path, string? OriginalPath)
{
    public string Description => Status switch { "A" => "added", "D" => "deleted", "R" => "renamed", "C" => "copied", "T" => "type changed", _ => "modified" };
}

public sealed record RepoCommitInfo(string Hash, string ShortHash, string Author, string Email, string Date, string Subject, string Body);

/// <summary>Configured git repositories, inspected through the git command line.</summary>
public sealed class RepoService
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly object _gate = new();
    private RepoState _state;

    public RepoService(AppPaths paths, ConfigService config)
    {
        _paths = paths; _config = config;
        _state = JsonUtil.LoadFile<RepoState>(paths.StateFile("repositories"));
    }

    public event Action? Changed;
    public IReadOnlyList<RepoEntry> Items { get { lock (_gate) return _state.Items.ToList(); } }

    public RepoEntry? Find(string idOrName)
    {
        lock (_gate)
            return _state.Items.FirstOrDefault(r => r.Id == idOrName)
                ?? _state.Items.FirstOrDefault(r => string.Equals(r.Name, idOrName, StringComparison.OrdinalIgnoreCase))
                ?? _state.Items.FirstOrDefault(r => r.Path == System.IO.Path.GetFullPath(idOrName));
    }

    public RepoEntry Get(string idOrName) => Find(idOrName) ?? throw new KeyNotFoundException($"No repository '{idOrName}'.");

    public async Task<RepoEntry> AddAsync(string path, string? name = null)
    {
        path = System.IO.Path.GetFullPath(path.StartsWith('~') ? _paths.HomeDir + path[1..] : path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Folder not found: {path}");
        var top = await GitAsync(path, "rev-parse", "--show-toplevel");
        if (top.ExitCode != 0) throw new InvalidOperationException($"{path} is not inside a git repository.");
        path = top.Stdout.Trim();
        RepoEntry entry;
        lock (_gate)
        {
            if (_state.Items.FirstOrDefault(r => r.Path == path) is { } existing) return existing;
            entry = new RepoEntry { Id = Watching.WatchService.NewId(_state.Items.Select(i => i.Id)), Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(path) : name!, Path = path };
            _state.Items.Add(entry);
        }
        Save();
        return entry;
    }

    public void Remove(string idOrName)
    {
        var r = Get(idOrName);
        lock (_gate) _state.Items.Remove(r);
        Save();
    }

    public void Rename(string idOrName, string name) { Get(idOrName).Name = name; Save(); }

    private void Save()
    {
        lock (_gate) JsonUtil.SaveFile(_paths.StateFile("repositories"), _state);
        Changed?.Invoke();
    }

    private static readonly Dictionary<string, string> GitEnv = new() { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C.UTF-8" };

    private static Task<Shell.RunResult> GitAsync(string cwd, params string[] args) => GitAsync(cwd, 30, args);

    private static Task<Shell.RunResult> GitAsync(string cwd, int timeoutSeconds, params string[] args) =>
        Shell.RunAsync("git", new[] { "-c", "core.quotepath=off", "-c", "color.ui=never" }.Concat(args), cwd, GitEnv, TimeSpan.FromSeconds(timeoutSeconds));

    private static string Rev(string rev) =>
        rev.Length > 0 && !rev.StartsWith('-') && !rev.Any(char.IsWhiteSpace) ? rev : throw new ArgumentException($"Invalid revision '{rev}'.");

    public async Task<RepoStatus> StatusAsync(RepoEntry repo)
    {
        try
        {
            var r = await GitAsync(repo.Path, "status", "--porcelain=v1", "-b", "-uall", "-z");
            if (r.ExitCode != 0) return new RepoStatus(repo.Id, repo.Name, repo.Path, null, null, 0, 0, [], r.Stderr.Trim());
            return ParseStatus(repo, r.Stdout);
        }
        catch (Exception ex) { return new RepoStatus(repo.Id, repo.Name, repo.Path, null, null, 0, 0, [], ex.Message); }
    }

    public static RepoStatus ParseStatus(RepoEntry repo, string porcelainZ)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<RepoChange>();
        var parts = porcelainZ.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var line = parts[i];
            if (line.Length < 3) continue;
            if (line.StartsWith("## "))
            {
                var head = line[3..];
                var bracket = head.IndexOf(" [", StringComparison.Ordinal);
                if (bracket >= 0)
                {
                    foreach (var piece in head[(bracket + 2)..].TrimEnd(']').Split(", "))
                    {
                        if (piece.StartsWith("ahead ")) int.TryParse(piece[6..], out ahead);
                        if (piece.StartsWith("behind ")) int.TryParse(piece[7..], out behind);
                    }
                    head = head[..bracket];
                }
                if (head.StartsWith("No commits yet on ")) head = head["No commits yet on ".Length..];
                var dots = head.IndexOf("...", StringComparison.Ordinal);
                branch = dots >= 0 ? head[..dots] : head;
                upstream = dots >= 0 ? head[(dots + 3)..] : null;
                continue;
            }
            char x = line[0], y = line[1];
            var path = line[3..];
            string? original = null;
            if (x is 'R' or 'C' && i + 1 < parts.Length) original = parts[++i]; // -z puts the source path in the next field
            changes.Add(new RepoChange(repo.Id, repo.Name, path, x, y, original));
        }
        return new RepoStatus(repo.Id, repo.Name, repo.Path, branch, upstream, ahead, behind, changes, null);
    }

    public async Task<IReadOnlyList<RepoStatus>> StatusAllAsync() => await Task.WhenAll(Items.Select(StatusAsync));

    /// <summary>Unified diff of one file. Untracked files are shown as all-new.</summary>
    public async Task<string> DiffAsync(RepoEntry repo, string path, bool staged = false, bool untracked = false)
    {
        Shell.RunResult r;
        if (untracked) r = await GitAsync(repo.Path, "diff", "--no-index", "--", "/dev/null", path);
        else if (staged) r = await GitAsync(repo.Path, "diff", "--cached", "--", path);
        else r = await GitAsync(repo.Path, "diff", "--", path);
        var text = r.Stdout;
        if (text.Length == 0 && !untracked && !staged)
        {
            // Nothing unstaged: fall back to the staged diff, then to an untracked view.
            r = await GitAsync(repo.Path, "diff", "--cached", "--", path);
            text = r.Stdout;
            if (text.Length == 0 && File.Exists(System.IO.Path.Combine(repo.Path, path)))
                text = (await GitAsync(repo.Path, "diff", "--no-index", "--", "/dev/null", path)).Stdout;
        }
        return text.Length == 0 ? (r.Stderr.Length > 0 ? r.Stderr : "(no differences)") : text;
    }

    public async Task<string> DiffAllAsync(RepoEntry repo)
    {
        var r = await GitAsync(repo.Path, "diff", "HEAD");
        return r.ExitCode == 0 ? r.Stdout : (await GitAsync(repo.Path, "diff")).Stdout;
    }

    public async Task<IReadOnlyList<RepoCommit>> LogAsync(RepoEntry repo, int? count = null, string? rev = null)
    {
        string[] args = ["log", "-n", (count ?? _config.Settings.Repositories.LogCount).ToString(), "--pretty=format:%h%x1f%an%x1f%ad%x1f%s", "--date=format:%Y-%m-%d %H:%M"];
        var r = await GitAsync(repo.Path, rev is null ? args : [.. args, Rev(rev), "--"]);
        if (r.ExitCode != 0) return [];
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\x1f')).Where(p => p.Length == 4)
            .Select(p => new RepoCommit(p[0], p[1], p[2], p[3])).ToList();
    }

    /// <summary>How many commits the current branch has in all, or null when there are none or git fails.</summary>
    public async Task<int?> CommitCountAsync(RepoEntry repo)
    {
        var r = await GitAsync(repo.Path, "rev-list", "--count", "HEAD");
        return r.ExitCode == 0 && int.TryParse(r.Stdout.Trim(), out var n) ? n : null;
    }

    /// <summary>
    /// Where a commit sits in the history of the current branch: 0 is the newest. Null when the commit is not on
    /// the current branch (it may be on another one).
    /// </summary>
    public async Task<int?> CommitPositionAsync(RepoEntry repo, string commit)
    {
        if (commit.StartsWith('-')) throw new ArgumentException("Invalid commit.");
        if ((await GitAsync(repo.Path, "merge-base", "--is-ancestor", commit, "HEAD")).ExitCode != 0) return null;
        var r = await GitAsync(repo.Path, "rev-list", "--count", commit + "..HEAD");
        return r.ExitCode == 0 && int.TryParse(r.Stdout.Trim(), out var n) ? n : null;
    }

    public async Task<string> ShowAsync(RepoEntry repo, string commit)
    {
        if (commit.StartsWith('-')) throw new ArgumentException("Invalid commit.");
        var r = await GitAsync(repo.Path, "show", "--stat", "--patch", commit);
        return r.ExitCode == 0 ? r.Stdout : r.Stderr;
    }

    public async Task StageAsync(RepoEntry repo, string path) => Check(await GitAsync(repo.Path, "add", "--", path));
    public async Task UnstageAsync(RepoEntry repo, string path) => Check(await GitAsync(repo.Path, "restore", "--staged", "--", path));
    public async Task StageAllAsync(RepoEntry repo) => Check(await GitAsync(repo.Path, "add", "-A"));
    public async Task UnstageAllAsync(RepoEntry repo) => Check(await GitAsync(repo.Path, "reset", "-q"));

    /// <summary>Throws away the unstaged changes of a file. An untracked file is deleted.</summary>
    public async Task DiscardAsync(RepoEntry repo, string path, bool untracked)
    {
        if (untracked)
        {
            var full = PathSafety.Combine(repo.Path, path);
            if (Directory.Exists(full)) Directory.Delete(full, true); else File.Delete(full);
            Changed?.Invoke();
        }
        else Check(await GitAsync(repo.Path, "restore", "--worktree", "--", path));
    }

    /// <summary>
    /// Applies a patch made by <see cref="DiffDoc"/>. With <paramref name="cached"/> it goes to the index (stage,
    /// or unstage with <paramref name="reverse"/>); without, to the working tree (discard, with reverse).
    /// </summary>
    public async Task ApplyPatchAsync(RepoEntry repo, string patch, bool cached, bool reverse)
    {
        var file = Path.Combine(Path.GetTempPath(), $"avamux-{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(file, patch);
        try
        {
            var args = new List<string> { "apply", "--whitespace=nowarn" };
            if (cached) args.Add("--cached");
            if (reverse) args.Add("--reverse");
            args.Add(file);
            Check(await GitAsync(repo.Path, args.ToArray()));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    public async Task CommitAsync(RepoEntry repo, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A commit needs a message.");
        var r = await GitAsync(repo.Path, 60, "commit", "-m", message);
        if (r.ExitCode != 0) throw new InvalidOperationException((r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim());
        Changed?.Invoke();
    }

    /// <summary>Text of a file for the plain (non-diff) view: from the working tree, or from HEAD when it was deleted.</summary>
    public async Task<string> FileTextAsync(RepoEntry repo, string path, string? rev = null)
    {
        if (rev is null)
        {
            var full = PathSafety.Combine(repo.Path, path);
            if (File.Exists(full))
            {
                if (new FileInfo(full).Length > 4 * 1024 * 1024) return "(this file is too large to show)";
                return await File.ReadAllTextAsync(full);
            }
            rev = "HEAD";
        }
        var r = await GitAsync(repo.Path, "show", $"{Rev(rev)}:{path}");
        return r.ExitCode == 0 ? r.Stdout : r.Stderr.Trim();
    }

    // ---- branches ----

    public async Task<IReadOnlyList<RepoBranch>> BranchesAsync(RepoEntry repo, bool remotes = true)
    {
        var refs = remotes ? new[] { "refs/heads", "refs/remotes" } : ["refs/heads"];
        var r = await GitAsync(repo.Path, ["for-each-ref", "--sort=-committerdate",
            "--format=%(refname)%1f%(refname:short)%1f%(HEAD)%1f%(upstream:short)%1f%(upstream:track)%1f%(committerdate:relative)%1f%(subject)", .. refs]);
        if (r.ExitCode != 0) return [];
        var list = new List<RepoBranch>();
        foreach (var p in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\x1f')).Where(p => p.Length == 7))
        {
            if (p[0].EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            list.Add(new RepoBranch(p[1], p[0].StartsWith("refs/remotes/", StringComparison.Ordinal), p[2] == "*", p[3].Length > 0 ? p[3] : null, p[4].Trim('[', ']'), p[5], p[6]));
        }
        return list.OrderBy(b => b.IsRemote).ThenByDescending(b => b.IsCurrent).ToList();
    }

    public async Task<string?> CurrentBranchAsync(RepoEntry repo)
    {
        var r = await GitAsync(repo.Path, "branch", "--show-current");
        return r.ExitCode == 0 && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }

    /// <summary>Switches to a branch. A remote branch ("origin/x") becomes a local tracking branch "x".</summary>
    public async Task SwitchAsync(RepoEntry repo, RepoBranch branch)
    {
        if (!branch.IsRemote) { Check(await GitAsync(repo.Path, 60, "switch", Rev(branch.Name))); return; }
        var local = branch.Name[(branch.Name.IndexOf('/') + 1)..];
        var r = await GitAsync(repo.Path, 60, "switch", Rev(local));
        if (r.ExitCode != 0) r = await GitAsync(repo.Path, 60, "switch", "--track", Rev(branch.Name));
        Check(r);
    }

    public async Task SwitchAsync(RepoEntry repo, string branch) => Check(await GitAsync(repo.Path, 60, "switch", Rev(branch)));

    public async Task CreateBranchAsync(RepoEntry repo, string name, string? startPoint = null, bool switchTo = true)
    {
        string[] args = switchTo ? ["switch", "-c", Rev(name)] : ["branch", Rev(name)];
        Check(await GitAsync(repo.Path, startPoint is null ? args : [.. args, Rev(startPoint)]));
    }

    public async Task DeleteBranchAsync(RepoEntry repo, string name, bool force = false) => Check(await GitAsync(repo.Path, "branch", force ? "-D" : "-d", Rev(name)));
    public async Task MergeAsync(RepoEntry repo, string rev) => Check(await GitAsync(repo.Path, 120, "merge", "--no-edit", Rev(rev)));
    public async Task FetchAsync(RepoEntry repo) => Check(await GitAsync(repo.Path, 120, "fetch", "--all", "--prune"));
    public async Task PullAsync(RepoEntry repo) => Check(await GitAsync(repo.Path, 180, "pull", "--ff-only"));
    public async Task PushAsync(RepoEntry repo) => Check(await GitAsync(repo.Path, 180, "push"));

    // ---- commits ----

    public async Task<RepoCommitInfo?> CommitInfoAsync(RepoEntry repo, string commit)
    {
        var r = await GitAsync(repo.Path, "show", "-s", "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%ad%x1f%s%x1f%b", "--date=format:%Y-%m-%d %H:%M", Rev(commit));
        var p = r.Stdout.Split('\x1f');
        return r.ExitCode == 0 && p.Length >= 7 ? new RepoCommitInfo(p[0].Trim(), p[1], p[2], p[3], p[4], p[5], string.Join('\x1f', p.Skip(6)).Trim()) : null;
    }

    public async Task<IReadOnlyList<RepoCommitFile>> CommitFilesAsync(RepoEntry repo, string commit)
    {
        var r = await GitAsync(repo.Path, "diff-tree", "--no-commit-id", "--name-status", "-r", "--root", "-M", "-z", Rev(commit));
        if (r.ExitCode != 0) throw new InvalidOperationException(r.Stderr.Trim());
        var parts = r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<RepoCommitFile>();
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            var status = parts[i][..1];
            if (status is "R" or "C" && i + 2 < parts.Length) { files.Add(new RepoCommitFile(status, parts[i + 2], parts[i + 1])); i += 2; }
            else { files.Add(new RepoCommitFile(status, parts[i + 1], null)); i += 1; }
        }
        return files;
    }

    /// <summary>What one commit did to one file.</summary>
    public async Task<string> CommitFileDiffAsync(RepoEntry repo, string commit, string path)
    {
        var r = await GitAsync(repo.Path, "show", "--format=", "--patch", "-M", Rev(commit), "--", path);
        return r.ExitCode == 0 ? (r.Stdout.Length > 0 ? r.Stdout : "(no textual changes)") : r.Stderr;
    }

    public async Task CheckoutCommitAsync(RepoEntry repo, string commit) => Check(await GitAsync(repo.Path, 60, "switch", "--detach", Rev(commit)));
    public async Task CherryPickAsync(RepoEntry repo, string commit) => Check(await GitAsync(repo.Path, 60, "cherry-pick", Rev(commit)));
    public async Task RevertAsync(RepoEntry repo, string commit) => Check(await GitAsync(repo.Path, 60, "revert", "--no-edit", Rev(commit)));
    public async Task TagAsync(RepoEntry repo, string name, string commit) => Check(await GitAsync(repo.Path, "tag", Rev(name), Rev(commit)));

    /// <param name="mode">soft, mixed or hard.</param>
    public async Task ResetAsync(RepoEntry repo, string commit, string mode)
    {
        if (mode is not ("soft" or "mixed" or "hard")) throw new ArgumentException("Reset mode must be soft, mixed or hard.");
        Check(await GitAsync(repo.Path, 60, "reset", "--" + mode, Rev(commit)));
    }

    // ---- remote ----

    public async Task<string?> RemoteUrlAsync(RepoEntry repo)
    {
        var r = await GitAsync(repo.Path, "remote", "get-url", "origin");
        if (r.ExitCode == 0 && r.Stdout.Trim().Length > 0) return r.Stdout.Trim();
        var first = (await GitAsync(repo.Path, "remote")).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return null;
        r = await GitAsync(repo.Path, "remote", "get-url", first.Trim());
        return r.ExitCode == 0 && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }

    /// <summary>The page a browser can open for a git remote: git@host:owner/repo.git becomes https://host/owner/repo.</summary>
    public static string? ToWebUrl(string? remote)
    {
        var u = (remote ?? "").Trim();
        if (u.Length == 0) return null;
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Drop any user name or token embedded in the address.
            return Uri.TryCreate(u, UriKind.Absolute, out var http) ? $"{http.Scheme}://{http.Host}{(http.IsDefaultPort ? "" : ":" + http.Port)}{http.AbsolutePath}".TrimEnd('/') : null;
        }
        if (u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(u, UriKind.Absolute, out var ssh) ? $"https://{ssh.Host}{ssh.AbsolutePath}".TrimEnd('/') : null;
        var at = u.IndexOf('@');
        var colon = u.IndexOf(':', Math.Max(0, at));
        if (colon > 0 && !u.Contains("://")) return $"https://{u[(at + 1)..colon]}/{u[(colon + 1)..].TrimStart('/')}";
        return null;
    }

    private void Check(Shell.RunResult r)
    {
        if (r.TimedOut) throw new InvalidOperationException("git took too long and was stopped.");
        if (r.ExitCode != 0) throw new InvalidOperationException((r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim());
        Changed?.Invoke();
    }
}
