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

/// <summary>What git is in the middle of, when it stopped for conflicts.</summary>
public enum MergeOp { None, Merge, Rebase, CherryPick, Revert }

/// <summary>
/// Whether a repository is in the middle of a merge (or rebase, cherry-pick, revert), which files still conflict,
/// and what to call the two sides. "Ours" is what git keeps as stage 2: the branch being merged into, or during a
/// rebase the branch being rebased onto. "Theirs" is stage 3: what is being brought in.
/// </summary>
public sealed record MergeState(MergeOp Op, string OursLabel, string TheirsLabel, IReadOnlyList<string> Conflicts)
{
    public static readonly MergeState None = new(MergeOp.None, "HEAD", "incoming", []);
    public bool InProgress => Op != MergeOp.None;
    /// <summary>True when there is something for the merge tool to do or to finish.</summary>
    public bool Active => InProgress || Conflicts.Count > 0;
    public string Verb => Op switch { MergeOp.Rebase => "rebase", MergeOp.CherryPick => "cherry-pick", MergeOp.Revert => "revert", _ => "merge" };
    public string Title => Op switch
    {
        MergeOp.Merge => $"Merging {TheirsLabel} into {OursLabel}", MergeOp.Rebase => $"Rebasing {TheirsLabel} onto {OursLabel}",
        MergeOp.CherryPick => $"Cherry-picking {TheirsLabel} onto {OursLabel}", MergeOp.Revert => $"Reverting {TheirsLabel} on {OursLabel}",
        _ => "Unresolved conflicts",
    };
}

/// <summary>The three versions git keeps of a conflicted file. A side is null where the file does not exist on it.</summary>
public sealed record ConflictFile(string Path, string? Base, string? Ours, string? Theirs, bool Binary)
{
    /// <summary>True when both sides have text that can be merged line by line.</summary>
    public bool Mergeable => !Binary && Ours is not null && Theirs is not null;
}

/// <summary>Configured git repositories, inspected through the git command line.</summary>
public sealed class RepoService
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly object _gate = new();
    private RepoState _state;
    private readonly List<RepoEntry> _builtIn = [];

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
                ?? _state.Items.FirstOrDefault(r => r.Path == System.IO.Path.GetFullPath(idOrName))
                ?? _builtIn.FirstOrDefault(r => r.Id == idOrName)
                ?? _builtIn.FirstOrDefault(r => string.Equals(r.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Makes a folder avamux itself owns (the wiki) known by id and name, so everything that works on a
    /// repository works on it, without it appearing in the user's list of repositories.
    /// </summary>
    public void AddBuiltIn(RepoEntry repo) { lock (_gate) { _builtIn.RemoveAll(r => r.Id == repo.Id); _builtIn.Add(repo); } }

    /// <summary>True when the folder is the top of a git repository of its own (not merely somewhere inside one).</summary>
    public async Task<bool> IsRepositoryAsync(RepoEntry repo)
    {
        if (!Directory.Exists(repo.Path)) return false;
        var r = await GitAsync(repo.Path, "rev-parse", "--show-toplevel");
        return r.ExitCode == 0 && SamePath(r.Stdout.Trim(), repo.Path);
    }

    private static bool SamePath(string a, string b)
    {
        static string Real(string p)
        {
            p = System.IO.Path.GetFullPath(p);
            try { return (Directory.ResolveLinkTarget(p, true)?.FullName ?? p).TrimEnd('/'); } catch { return p.TrimEnd('/'); }
        }
        return a.TrimEnd('/') == b.TrimEnd('/') || Real(a) == Real(b) || System.IO.Path.GetFullPath(a).TrimEnd('/') == System.IO.Path.GetFullPath(b).TrimEnd('/');
    }

    /// <summary>Makes the folder a git repository. Nothing is committed.</summary>
    public async Task InitAsync(RepoEntry repo)
    {
        Directory.CreateDirectory(repo.Path);
        Check(await GitAsync(repo.Path, "init"));
    }

    /// <summary>The commits that changed one file, newest first, following it through renames.</summary>
    public async Task<IReadOnlyList<RepoCommit>> FileLogAsync(RepoEntry repo, string path, int count = 200)
    {
        var r = await GitAsync(repo.Path, "log", "--follow", "-n", count.ToString(), "--pretty=format:%h%x1f%an%x1f%ad%x1f%s", "--date=format:%Y-%m-%d %H:%M", "--", path);
        if (r.ExitCode != 0) return [];
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\x1f')).Where(p => p.Length == 4)
            .Select(p => new RepoCommit(p[0], p[1], p[2], p[3])).ToList();
    }

    /// <summary>Puts a file back the way it was in a commit, in the working tree and the index. Nothing is committed.</summary>
    public async Task RestoreFileAsync(RepoEntry repo, string path, string commit) =>
        Check(await GitAsync(repo.Path, "checkout", Rev(commit), "--", path));

    /// <summary>A file's text as it was in a commit. Null when the commit does not have it.</summary>
    public async Task<string?> FileAtAsync(RepoEntry repo, string path, string commit)
    {
        var r = await GitAsync(repo.Path, "show", $"{Rev(commit)}:{path}");
        return r.ExitCode == 0 ? r.Stdout : null;
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

    // ---- merging ----

    private readonly Dictionary<string, string> _gitDirs = [];

    private async Task<string?> GitDirAsync(RepoEntry repo)
    {
        lock (_gate) if (_gitDirs.TryGetValue(repo.Path, out var known) && Directory.Exists(known)) return known;
        var r = await GitAsync(repo.Path, "rev-parse", "--absolute-git-dir");
        var dir = r.ExitCode == 0 ? r.Stdout.Trim() : null;
        if (dir is not null) lock (_gate) _gitDirs[repo.Path] = dir;
        return dir;
    }

    /// <summary>What git is in the middle of in a repository, and which files conflict.</summary>
    public async Task<MergeState> MergeStateAsync(RepoEntry repo)
    {
        try
        {
            if (await GitDirAsync(repo) is not { } dir) return MergeState.None;
            string? Read(string name) { try { var f = System.IO.Path.Combine(dir, name); return File.Exists(f) ? File.ReadAllText(f).Trim() : null; } catch { return null; } }
            static string Short(string? hash) => hash is { Length: > 8 } ? hash[..8] : hash ?? "?";
            static string Branch(string? name) => name is null ? "?" : name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name[11..] : name;

            var rebaseDir = Directory.Exists(System.IO.Path.Combine(dir, "rebase-merge")) ? "rebase-merge" : Directory.Exists(System.IO.Path.Combine(dir, "rebase-apply")) ? "rebase-apply" : null;
            var op = rebaseDir is not null ? MergeOp.Rebase : Read("MERGE_HEAD") is not null ? MergeOp.Merge
                : Read("CHERRY_PICK_HEAD") is not null ? MergeOp.CherryPick : Read("REVERT_HEAD") is not null ? MergeOp.Revert : MergeOp.None;

            var unmerged = await GitAsync(repo.Path, "diff", "--name-only", "--diff-filter=U", "-z");
            var conflicts = unmerged.ExitCode == 0 ? unmerged.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList() : [];
            if (op == MergeOp.None) return conflicts.Count == 0 ? MergeState.None : MergeState.None with { Conflicts = conflicts };

            var head = await CurrentBranchAsync(repo);
            if (head is null) { var h = await GitAsync(repo.Path, "rev-parse", "--short", "HEAD"); head = h.ExitCode == 0 ? h.Stdout.Trim() : "HEAD"; }
            string ours = head, theirs;
            switch (op)
            {
                case MergeOp.Merge:
                    // "Merge branch 'x' into y", "Merge remote-tracking branch 'origin/x'", "Merge commit 'abc'".
                    var msg = Read("MERGE_MSG") ?? "";
                    int q1 = msg.IndexOf('\''), q2 = q1 < 0 ? -1 : msg.IndexOf('\'', q1 + 1);
                    theirs = q2 > q1 && msg.StartsWith("Merge", StringComparison.Ordinal) ? msg[(q1 + 1)..q2] : Short(Read("MERGE_HEAD"));
                    break;
                case MergeOp.Rebase:
                    // While rebasing, git's "ours" is the branch being built on and "theirs" the commit being replayed.
                    ours = Short(Read(rebaseDir + "/onto"));
                    theirs = Branch(Read(rebaseDir + "/head-name"));
                    if (theirs is "?" or "detached HEAD") theirs = Short(Read(rebaseDir + "/orig-head") ?? Read("ORIG_HEAD"));
                    break;
                case MergeOp.CherryPick: theirs = Short(Read("CHERRY_PICK_HEAD")); break;
                default: theirs = Short(Read("REVERT_HEAD")); break;
            }
            return new MergeState(op, ours, theirs, conflicts);
        }
        catch (Exception ex) { Logging.Log.Debug("repos", "Could not read the merge state: " + ex.Message); return MergeState.None; }
    }

    /// <summary>The common ancestor's, our and their version of a conflicted file, as git has them in its index.</summary>
    public async Task<ConflictFile> ConflictAsync(RepoEntry repo, string path)
    {
        const int limit = 4 * 1024 * 1024;
        var binary = false;
        async Task<string?> Stage(int n)
        {
            var r = await GitAsync(repo.Path, "show", $":{n}:{path}");
            if (r.ExitCode != 0) return null;
            if (r.Stdout.Length > limit || r.Stdout.AsSpan(0, Math.Min(r.Stdout.Length, 8000)).Contains('\0')) binary = true;
            return r.Stdout;
        }
        var (b, o, t) = (await Stage(1), await Stage(2), await Stage(3));
        return new ConflictFile(path, b, o, t, binary);
    }

    /// <summary>Writes the merged text of a conflicted file and tells git the conflict is settled.</summary>
    public async Task ResolveAsync(RepoEntry repo, string path, string text)
    {
        var full = PathSafety.Combine(repo.Path, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text, new System.Text.UTF8Encoding(false));
        Check(await GitAsync(repo.Path, "add", "--", path));
    }

    /// <summary>Settles a conflicted file with one side's whole version. Where that side deleted the file, it is deleted.</summary>
    public async Task ResolveWithSideAsync(RepoEntry repo, string path, bool theirs)
    {
        var r = await GitAsync(repo.Path, "checkout", theirs ? "--theirs" : "--ours", "--", path);
        Check(r.ExitCode == 0 ? await GitAsync(repo.Path, "add", "--", path) : await GitAsync(repo.Path, "rm", "-q", "--", path));
    }

    /// <summary>Carries on with the merge, rebase, cherry-pick or revert once its conflicts are settled, and reports where that leaves things.</summary>
    public async Task<MergeState> ContinueMergeAsync(RepoEntry repo)
    {
        var state = await MergeStateAsync(repo);
        if (!state.InProgress) return state;
        if (state.Conflicts.Count > 0) throw new InvalidOperationException($"{state.Conflicts.Count} file{(state.Conflicts.Count == 1 ? " still has" : "s still have")} conflicts.");
        // The message git prepared is used as it is: no editor is opened.
        var r = await GitAsync(repo.Path, 120, "-c", "core.editor=true", state.Verb, "--continue");
        var after = await MergeStateAsync(repo);
        Changed?.Invoke();
        // A rebase that stops at its next commit's conflicts has not failed.
        if (r.ExitCode != 0 && after.Conflicts.Count == 0) throw new InvalidOperationException((r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim());
        return after;
    }

    /// <summary>Gives up the merge, rebase, cherry-pick or revert and puts the repository back where it was before.</summary>
    public async Task AbortMergeAsync(RepoEntry repo)
    {
        var state = await MergeStateAsync(repo);
        if (!state.InProgress) throw new InvalidOperationException("Nothing is in progress to abort.");
        Check(await GitAsync(repo.Path, 60, state.Verb, "--abort"));
    }

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
