using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Avamux.Core.GitActivity;

/// <summary>
/// Reads one repository's commits in the period. Remote repositories are kept as bare, shallow mirrors that hold
/// only the period (plus the files at its start, in "lines" detail), so hundreds of repositories stay cheap to keep;
/// a repository whose branch tips have not moved is neither fetched nor read again.
/// </summary>
public sealed class ActivityScanner
{
    private readonly GitActivityConfig _config;
    private readonly string _mirrors;
    private readonly AiDetector _ai;
    private readonly Regex[] _exclude;
    private readonly List<FrameworkRule> _frameworks;
    private readonly string _signature;
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromMinutes(20);

    public ActivityScanner(GitActivityConfig config, string mirrors)
    {
        _config = config;
        _mirrors = mirrors;
        _ai = new AiDetector(config.AiRules);
        _exclude = config.ExcludePaths.Where(p => p.Trim().Length > 0).Select(Glob.ToRegex).ToArray();
        _frameworks = [.. Frameworks.BuiltIn, .. config.Frameworks.Where(f => f.Name.Trim().Length > 0)];
        _signature = Signature(config);
        _markers = _frameworks.SelectMany(f => f.Markers.Select(m => (Depth: m.Count(c => c == '/'), Regex: Glob.ToRegex(m.Trim())))).ToList();
    }

    private readonly List<(int Depth, Regex Regex)> _markers;

    /// <summary>A file that says what a project uses (next.config.js, Chart.yaml).</summary>
    private bool IsMarker(string path)
    {
        if (_markers.Count == 0) return false;
        var parts = path.Split('/');
        foreach (var (depth, regex) in _markers)
            if (parts.Length > depth && regex.IsMatch(depth == 0 ? parts[^1] : string.Join('/', parts[^(depth + 1)..]))) return true;
        return false;
    }

    private bool IsManifest(string path) => Frameworks.IsManifest(path) && !Excluded(path);

    private bool IsProjectFile(string path) => IsManifest(path) || IsMarker(path);

    /// <summary>The settings that change what is read from a repository: when they change, everything is read again (from the mirrors, without fetching).</summary>
    public static string Signature(GitActivityConfig c)
    {
        var text = JsonUtil.Line(JsonUtil.ToNode(new { v = 5, c.ExcludePaths, c.AiRules, c.Frameworks, c.MainlineBranches }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>True when the period has no start: everything is read.</summary>
    public static bool AllTime(DateTimeOffset since) => since.Year <= 1;

    public bool IsMainline(string branch, string? defaultBranch) =>
        string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase) || Glob.Any(_config.MainlineBranches, branch);

    public string MirrorPath(ActivityRepo repo)
    {
        var safe = Regex.Replace(repo.Name, @"[^\w.\-]+", "_").Trim('_');
        if (safe.Length > 60) safe = safe[..60];
        return Path.Combine(_mirrors, $"{safe}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repo.Key)))[..10].ToLowerInvariant()}.git");
    }

    /// <summary>Brings the repository's activity up to date. <paramref name="previous"/> is what was read last time, if anything.</summary>
    public async Task<RepoActivity> ScanAsync(ActivityRepo repo, IDictionary<string, string> env, DateTimeOffset since, RepoActivity? previous, Action<string>? step, CancellationToken ct)
    {
        var result = new RepoActivity { Key = repo.Key, Signature = _signature, Since = since, ScannedAt = DateTimeOffset.Now };
        var lines = !string.Equals(_config.Detail, "files", StringComparison.OrdinalIgnoreCase) && !(_config.MaxLinesRepoMb > 0 && repo.SizeKb > _config.MaxLinesRepoMb * 1024L);
        result.Mode = lines ? "lines" : "files";
        var fresh = previous is not null && previous.Since <= since && previous.Status != "error" && previous.Mode == result.Mode;

        // Untouched since before the period (the host says so): nothing to fetch.
        if (repo.PushedAt is { } pushed && pushed < since && !AllTime(since) && repo.LocalPath is null)
        {
            result.Status = "inactive";
            result.Refs = "pushed:" + pushed.ToUnixTimeSeconds();
            return result;
        }

        string gitDir;
        if (repo.LocalPath is { } local)
        {
            gitDir = local;
            if (env.ContainsKey("AVAMUX_FETCH"))
            {
                step?.Invoke("fetching");
                await Git.RunAsync(local, ["fetch", "--all", "--prune", "--quiet"], env, null, TimeSpan.FromMinutes(5), ct);
            }
        }
        else
        {
            if (repo.CloneUrl is null) throw new InvalidOperationException("No clone URL.");
            gitDir = MirrorPath(repo);
            step?.Invoke("checking");
            var remote = await Git.RunAsync(_mirrors, ["ls-remote", "--heads", repo.CloneUrl], env, null, TimeSpan.FromMinutes(2), ct);
            if (remote.ExitCode != 0) throw new InvalidOperationException(Git.Problem(remote));
            var remoteRefs = RefsSignature(remote.Stdout);
            if (remoteRefs.Length == 0) { result.Status = "inactive"; result.Refs = "empty"; return result; }
            // The same branch tips as last time: nothing new to fetch or read.
            if (fresh && previous!.Refs == remoteRefs && (previous.Status == "inactive" || previous.Signature == _signature && Directory.Exists(gitDir)))
                return Reuse(previous, since);
            step?.Invoke("fetching");
            var fetched = await FetchMirrorAsync(repo, gitDir, lines, since, env, ct);
            if (!fetched)
            {
                // Nothing on any branch since the start of the period.
                result.Status = "inactive"; result.Refs = remoteRefs;
                return result;
            }
        }

        step?.Invoke("reading");
        var refs = await RefsAsync(gitDir, repo.LocalPath is not null, ct);
        result.Refs = RefsSignature(string.Join('\n', refs.Select(r => $"{r.Sha}\t{r.Name}")));
        result.Branches = refs.Select(r => r.Branch).Distinct().Count();
        if (repo.LocalPath is not null && fresh && previous!.Refs == result.Refs && previous.Signature == _signature) return Reuse(previous, since);
        if (refs.Count == 0) { result.Status = "inactive"; return result; }

        var absGit = repo.LocalPath is null ? gitDir : (await Git.RunAsync(gitDir, ["rev-parse", "--absolute-git-dir"], env, null, TimeSpan.FromSeconds(20), ct)).Stdout.Trim();
        var shallow = ReadShallow(absGit);
        var tip = refs.FirstOrDefault(r => string.Equals(r.Branch, repo.DefaultBranch, StringComparison.OrdinalIgnoreCase))
                  ?? _config.MainlineBranches.Select(m => refs.FirstOrDefault(r => Glob.Match(m, r.Branch))).FirstOrDefault(r => r is not null)
                  ?? refs[0];
        var map = await FrameworksAsync(gitDir, tip.Name, lines, env, ct);
        result.Frameworks = [.. map.All];

        var mainline = refs.Where(r => IsMainline(r.Branch, repo.DefaultBranch ?? tip.Branch)).ToList();
        var feature = refs.Except(mainline).ToList();
        // What was read before and is still valid: only the commits that are new since then need reading.
        var incremental = fresh && previous!.Signature == _signature && previous.Tips.Count > 0 && previous.Status != "inactive";
        result.Tips = refs.Select(r => r.Sha).Distinct().ToList();
        Dictionary<string, ActivityCommit> commits;
        try { commits = await ReadAsync(gitDir, refs, mainline, feature, since, lines, shallow, incremental ? previous!.Tips : [], env, step, ct); }
        catch (InvalidOperationException) when (incremental)
        {
            // A tip read last time is gone (history was rewritten and cleaned up): read everything again.
            incremental = false;
            commits = await ReadAsync(gitDir, refs, mainline, feature, since, lines, shallow, [], env, step, ct);
        }
        if (incremental)
            foreach (var c in previous!.Commits) commits.TryAdd(c.Hash, c);
        result.Commits = commits.Values.Where(c => AllTime(since) || c.When >= since).OrderByDescending(c => c.When).ToList();
        foreach (var c in result.Commits) c.RepoKey = repo.Key;
        // Frameworks only seen on other branches (a .tf file in a feature branch) count too.
        result.Frameworks = result.Frameworks.Concat(result.Commits.SelectMany(c => c.Parts).SelectMany(p => p.Frameworks.Split('|', StringSplitOptions.RemoveEmptyEntries)))
            .Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        result.Status = result.Commits.Count == 0 ? "inactive" : "ok";
        return result;
    }

    private static RepoActivity Reuse(RepoActivity previous, DateTimeOffset since)
    {
        previous.ScannedAt = DateTimeOffset.Now;
        previous.Commits = previous.Commits.Where(c => AllTime(since) || c.When >= since).ToList();
        previous.Since = since;
        return previous;
    }

    private async Task<bool> FetchMirrorAsync(ActivityRepo repo, string dir, bool lines, DateTimeOffset since, IDictionary<string, string> env, CancellationToken ct)
    {
        // A mirror made for "files" has no file contents: reading lines from it would download them one by one.
        if (Directory.Exists(dir) && lines && File.Exists(Path.Combine(dir, "avamux-files-only"))) Directory.Delete(dir, true);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            Git.Check(await Git.RunAsync(dir, ["init", "--bare", "--quiet"], env, null, TimeSpan.FromSeconds(30), ct));
            Git.Check(await Git.RunAsync(dir, ["remote", "add", "origin", repo.CloneUrl!], env, null, TimeSpan.FromSeconds(30), ct));
            Git.Check(await Git.RunAsync(dir, ["config", "remote.origin.fetch", "+refs/heads/*:refs/heads/*"], env, null, TimeSpan.FromSeconds(30), ct));
            Git.Check(await Git.RunAsync(dir, ["config", "gc.auto", "0"], env, null, TimeSpan.FromSeconds(30), ct));
            if (!lines)
            {
                Git.Check(await Git.RunAsync(dir, ["config", "remote.origin.promisor", "true"], env, null, TimeSpan.FromSeconds(30), ct));
                Git.Check(await Git.RunAsync(dir, ["config", "remote.origin.partialclonefilter", "blob:none"], env, null, TimeSpan.FromSeconds(30), ct));
                await File.WriteAllTextAsync(Path.Combine(dir, "avamux-files-only"), "", ct);
            }
        }
        else Git.Check(await Git.RunAsync(dir, ["remote", "set-url", "origin", repo.CloneUrl!], env, null, TimeSpan.FromSeconds(30), ct));
        List<string> args = ["fetch", "--prune", "--no-tags", "--quiet"];
        // All of history: a mirror that was kept shallow is filled in.
        if (AllTime(since)) { if (File.Exists(Path.Combine(dir, "shallow"))) args.Add("--unshallow"); }
        else args.Add($"--shallow-since={since.AddDays(-1):yyyy-MM-dd}");
        if (!lines) args.Add("--filter=blob:none");
        args.Add("origin");
        var r = await Git.RunAsync(dir, args, env, null, FetchTimeout, ct);
        if (r.ExitCode != 0 && r.Stderr.Contains("no commits selected for shallow requests", StringComparison.OrdinalIgnoreCase)) return false;
        Git.Check(r);
        if (!AllTime(since))
        {
            // The cut must fall before the period, so the period's first commits have their parents: what they
            // changed can be counted, and what the projects used just before the period is known. A branch made from
            // an old commit leaves the cut inside the period: move the date back until it is not (or give up).
            foreach (var margin in new[] { 30, 120, 400 })
            {
                var cut = ReadShallow(dir);
                if (cut.Count == 0) break;
                var dates = await Git.RunAsync(dir, ["log", "--no-walk", "--stdin", "--format=%cI"], env, string.Join('\n', cut) + "\n", TimeSpan.FromMinutes(1), ct);
                if (!dates.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(d => DateTimeOffset.TryParse(d, out var t) && t >= since)) break;
                List<string> back = ["fetch", "--no-tags", "--quiet", $"--shallow-since={since.AddDays(-margin):yyyy-MM-dd}"];
                if (!lines) back.Add("--filter=blob:none");
                back.Add("origin");
                Git.Check(await Git.RunAsync(dir, back, env, null, FetchTimeout, ct));
            }
        }
        return true;
    }

    public sealed record GitRef(string Sha, string Name, string Branch);

    private static async Task<List<GitRef>> RefsAsync(string gitDir, bool local, CancellationToken ct)
    {
        string[] args = local ? ["for-each-ref", "--format=%(objectname)%09%(refname)", "refs/heads", "refs/remotes"] : ["for-each-ref", "--format=%(objectname)%09%(refname)", "refs/heads"];
        var r = await Git.RunAsync(gitDir, args, null, null, TimeSpan.FromMinutes(1), ct);
        Git.Check(r);
        var list = new List<GitRef>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var name = line[(tab + 1)..];
            if (name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            var branch = name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name[11..]
                : name.StartsWith("refs/remotes/", StringComparison.Ordinal) && name.IndexOf('/', 13) is var slash and > 0 ? name[(slash + 1)..] : name;
            list.Add(new GitRef(line[..tab], name, branch));
        }
        return list;
    }

    private static string RefsSignature(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.EndsWith("/HEAD")).Order(StringComparer.Ordinal).ToList();
        return lines.Count == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..24];
    }

    private static HashSet<string> ReadShallow(string gitDir)
    {
        try
        {
            var f = Path.Combine(gitDir, "shallow");
            return File.Exists(f) ? File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal) : [];
        }
        catch { return []; }
    }

    private bool Excluded(string path) => _exclude.Any(r => r.IsMatch(path));

    private async Task<FrameworkMap> FrameworksAsync(string gitDir, string tip, bool lines, IDictionary<string, string> env, CancellationToken ct)
    {
        try
        {
            var tree = await Git.RunAsync(gitDir, ["ls-tree", "-r", "--name-only", "-z", tip], env, null, TimeSpan.FromMinutes(2), ct);
            if (tree.ExitCode != 0) return FrameworkMap.Empty;
            var paths = tree.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var manifests = paths.Where(p => Frameworks.IsManifest(p) && !Excluded(p)).Take(lines ? 400 : 120).ToList();
            var read = new Dictionary<string, (HashSet<string>, HashSet<string>)>(StringComparer.Ordinal);
            if (manifests.Count > 0)
            {
                var blobs = await Git.CatFilesAsync(gitDir, manifests.Select(m => $"{tip}:{m}").ToList(), env, ct);
                for (var i = 0; i < manifests.Count; i++)
                    if (blobs[i] is { } text) read[manifests[i]] = Frameworks.Read(manifests[i], text);
            }
            return new FrameworkMap(_frameworks, read, paths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.Log.Debug("activity", $"Frameworks of {gitDir}: {ex.Message}");
            return FrameworkMap.Empty;
        }
    }

    private const char Rs = '\x1e', Us = '\x1f', End = '\x1d';

    private async Task<Dictionary<string, ActivityCommit>> ReadAsync(string gitDir, List<GitRef> refs, List<GitRef> mainline, List<GitRef> feature, DateTimeOffset since, bool lines,
        HashSet<string> shallow, List<string> readBefore, IDictionary<string, string> env, Action<string>? step, CancellationToken ct)
    {
        var raws = new List<RawCommit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var old = readBefore.Select(t => "^" + t).ToList();
        // Mainline branches, then feature branches without what a mainline branch already has, so work in flight is
        // credited to the branch it is being done on. Both oldest first, so each commit's projects can be worked
        // out from its parent's.
        if (mainline.Count > 0)
            await LogAsync(gitDir, [.. mainline.Select(r => r.Name), .. old], since, lines, refs, shallow, raws, seen, env, ct);
        if (feature.Count > 0)
            await LogAsync(gitDir, [.. feature.Select(r => r.Name), .. mainline.Select(r => "^" + r.Name), .. old], since, lines, refs, shallow, raws, seen, env, ct);
        // Merges change no lines of their own, but they do bring manifests in from the branch merged: read only
        // which files they changed (no contents, which is what makes diffing merges slow).
        var merges = raws.Where(r => r.Commit.Merge).ToDictionary(r => r.Commit.Hash, StringComparer.Ordinal);
        if (merges.Count > 0)
        {
            if (mainline.Count > 0) await MergeChangesAsync(gitDir, [.. mainline.Select(r => r.Name), .. old], since, merges, env, ct);
            if (feature.Count > 0) await MergeChangesAsync(gitDir, [.. feature.Select(r => r.Name), .. mainline.Select(r => "^" + r.Name), .. old], since, merges, env, ct);
        }
        step?.Invoke("frameworks");
        await AttributeAsync(gitDir, raws, lines, env, ct);
        return raws.ToDictionary(r => r.Commit.Hash, r => r.Commit, StringComparer.Ordinal);
    }

    /// <summary>A commit as read from git, before its changes are credited to languages and frameworks.</summary>
    private sealed class RawCommit(ActivityCommit commit, string? firstParent)
    {
        public ActivityCommit Commit { get; } = commit;
        public string? FirstParent { get; } = firstParent;
        public List<(string Path, int Added, int Deleted)> Files { get; } = [];
        /// <summary>Changes to manifests and marker files: git's status letter, the old and new path, the new contents' id.</summary>
        public List<(char Status, string Old, string New, string Sha)> Project { get; } = [];
    }

    /// <summary>What a repository's projects use at one commit: every manifest (read), and the marker files that are there.</summary>
    private sealed class ProjectState(Dictionary<string, (HashSet<string>, HashSet<string>)> manifests, HashSet<string> markers)
    {
        public static readonly ProjectState Empty = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        public Dictionary<string, (HashSet<string>, HashSet<string>)> Manifests { get; } = manifests;
        public HashSet<string> Markers { get; } = markers;
        private FrameworkMap? _map;
        public FrameworkMap Map(IReadOnlyList<FrameworkRule> rules) => _map ??= new FrameworkMap(rules, Manifests, Markers.ToList());
    }

    private async Task LogAsync(string gitDir, List<string> revs, DateTimeOffset since, bool lines, List<GitRef> refs, HashSet<string> shallow,
        List<RawCommit> into, HashSet<string> seen, IDictionary<string, string> env, CancellationToken ct)
    {
        var byRef = refs.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().Branch, StringComparer.Ordinal);
        List<string> args = ["log", "--stdin", "--source", "--no-color", "--reverse", "--topo-order", "--raw", "--no-abbrev", "--no-diff-merges",
            "--format=%x1e%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%S%x1f%s%x1f%b%x1d"];
        if (!AllTime(since)) args.Add($"--since={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        args.AddRange(lines ? ["--numstat", "-M"] : ["--no-renames"]);
        RawCommit? current = null;
        string[]? head = null;
        var body = new StringBuilder();
        var inBody = false;

        void Finish()
        {
            if (current is not null && seen.Add(current.Commit.Hash)) into.Add(current);
            current = null;
        }

        void Start(string[] f, string text)
        {
            var finding = _ai.Detect(f[2], f[3], f[5], f[6], f[8], text);
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            current = new RawCommit(new ActivityCommit
            {
                Hash = f[0], Merge = parents.Length > 1, AuthorName = f[2], AuthorEmail = f[3], When = DateTimeOffset.TryParse(f[4], out var w) ? w : DateTimeOffset.MinValue,
                Branch = byRef.TryGetValue(f[7], out var b) ? b : f[7].Replace("refs/heads/", ""), Subject = f[8], Boundary = shallow.Contains(f[0]),
                AiTools = finding.Tools, AiModels = finding.Models, Agent = finding.AgentAuthored, Bot = finding.Bot,
                PersonName = finding.HumanName, PersonEmail = finding.HumanEmail,
            }, parents.FirstOrDefault());
        }

        void Line(string line)
        {
            if (inBody)
            {
                var e = line.IndexOf(End);
                if (e < 0) { body.Append(line).Append('\n'); return; }
                body.Append(line, 0, e);
                inBody = false;
                Start(head!, body.ToString());
                return;
            }
            if (line.Length > 0 && line[0] == Rs)
            {
                Finish();
                var e = line.IndexOf(End);
                var text = e >= 0 ? line[1..e] : line[1..];
                var f = text.Split(Us);
                if (f.Length < 10) { head = null; return; }
                // The body is the last field and may run over many lines.
                f = [.. f[..9], string.Join(Us, f[9..])];
                if (e >= 0) Start(f, f[9]);
                else { head = f[..9]; body.Clear().Append(f[9]).Append('\n'); inBody = true; }
                return;
            }
            if (current is null || line.Length == 0) return;
            if (line[0] == ':')
            {
                // ":100644 100644 <old id> <new id> M\tpath" (renames and copies have two paths).
                var tab = line.IndexOf('\t');
                if (tab < 0) return;
                var meta = line[1..tab].Split(' ');
                if (meta.Length < 5 || meta[4].Length == 0) return;
                var paths = line[(tab + 1)..].Split('\t');
                var status = meta[4][0];
                var oldPath = paths[0];
                var newPath = paths.Length > 1 ? paths[1] : paths[0];
                if (IsProjectFile(oldPath) || IsProjectFile(newPath)) current.Project.Add((status, oldPath, newPath, meta[3]));
                if (!lines && !Excluded(newPath)) current.Files.Add((newPath, 0, 0));
                return;
            }
            if (!lines) return;
            var t1 = line.IndexOf('\t');
            var t2 = t1 < 0 ? -1 : line.IndexOf('\t', t1 + 1);
            if (t2 < 0) return;
            int.TryParse(line.AsSpan(0, t1), out var added);
            int.TryParse(line.AsSpan(t1 + 1, t2 - t1 - 1), out var deleted);
            var path = RenamedTo(line[(t2 + 1)..]);
            if (!Excluded(path)) current.Files.Add((path, added, deleted));
        }

        var run = await Git.StreamAsync(gitDir, args, string.Join('\n', revs) + "\n", Line, env, TimeSpan.FromMinutes(20), ct);
        Finish();
        if (run.ExitCode != 0) throw new InvalidOperationException(Git.Problem(run));
    }

    private async Task MergeChangesAsync(string gitDir, List<string> revs, DateTimeOffset since, Dictionary<string, RawCommit> merges, IDictionary<string, string> env, CancellationToken ct)
    {
        List<string> args = ["log", "--stdin", "--merges", "--no-color", "--raw", "--no-abbrev", "--no-renames", "--diff-merges=first-parent", "--format=%x1e%H"];
        if (!AllTime(since)) args.Add($"--since={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        RawCommit? current = null;
        var run = await Git.StreamAsync(gitDir, args, string.Join('\n', revs) + "\n", line =>
        {
            if (line.Length > 0 && line[0] == Rs) { current = merges.GetValueOrDefault(line[1..].Trim()); return; }
            if (current is null || line.Length == 0 || line[0] != ':') return;
            var tab = line.IndexOf('\t');
            if (tab < 0) return;
            var meta = line[1..tab].Split(' ');
            var path = line[(tab + 1)..];
            if (meta.Length >= 5 && meta[4].Length > 0 && IsProjectFile(path)) current.Project.Add((meta[4][0], path, path, meta[3]));
        }, env, TimeSpan.FromMinutes(20), ct);
        if (run.ExitCode != 0) throw new InvalidOperationException(Git.Problem(run));
    }

    /// <summary>
    /// Credits each commit's files to languages and to the frameworks its repository used at that commit: the
    /// projects are followed through history (each commit's from its parent's, plus the manifests it changed), so
    /// work done before a move from Angular to React counts as Angular and work after it as React.
    /// </summary>
    private async Task AttributeAsync(string gitDir, List<RawCommit> raws, bool lines, IDictionary<string, string> env, CancellationToken ct)
    {
        var known = raws.Select(r => r.Commit.Hash).ToHashSet(StringComparer.Ordinal);
        var states = new Dictionary<string, ProjectState>(StringComparer.Ordinal);
        // Where history enters the period (a branch's last commit before it, say): what the projects were then.
        foreach (var p in raws.Select(r => r.FirstParent).OfType<string>().Where(p => !known.Contains(p)).Distinct().Take(lines ? 300 : 60))
            states[p] = await StateAtAsync(gitDir, p, lines, env, ct);
        // The manifests changed in the period, read in one go.
        var ids = raws.SelectMany(r => r.Project).Where(c => c.Status != 'D' && IsManifest(c.New)).Select(c => c.Sha).Distinct().Take(lines ? 20000 : 500).ToList();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            var read = await Git.CatFilesAsync(gitDir, ids, env, ct);
            for (var i = 0; i < ids.Count; i++) if (read[i] is { } t) texts[ids[i]] = t;
        }
        foreach (var r in raws)
        {
            var parent = r.FirstParent is { } fp && states.TryGetValue(fp, out var ps) ? ps : ProjectState.Empty;
            var state = r.Project.Count == 0 ? parent : Apply(parent, r.Project, texts);
            states[r.Commit.Hash] = state;
            if (r.Commit.Boundary || r.Commit.Merge) { r.Commit.Parts = []; continue; }
            var map = state.Map(_frameworks);
            var parts = new Dictionary<(string, string, bool), ActivityPart>();
            foreach (var (path, added, deleted) in r.Files)
            {
                var key = (Languages.KeyFor(path), map.For(path), Languages.IsTest(path));
                if (!parts.TryGetValue(key, out var part)) parts[key] = part = new ActivityPart { Key = key.Item1, Frameworks = key.Item2, Test = key.Item3 };
                part.Files++; part.Added += added; part.Deleted += deleted;
            }
            r.Commit.Parts = parts.Values.ToList();
        }
    }

    private ProjectState Apply(ProjectState parent, List<(char Status, string Old, string New, string Sha)> changes, Dictionary<string, string> texts)
    {
        var manifests = new Dictionary<string, (HashSet<string>, HashSet<string>)>(parent.Manifests, StringComparer.Ordinal);
        var markers = new HashSet<string>(parent.Markers, StringComparer.Ordinal);
        foreach (var (status, oldPath, newPath, sha) in changes)
        {
            if (status is 'D' or 'R') { manifests.Remove(oldPath); markers.Remove(oldPath); }
            if (status == 'D') continue;
            if (IsManifest(newPath) && texts.TryGetValue(sha, out var text)) manifests[newPath] = Frameworks.Read(newPath, text);
            if (IsMarker(newPath)) markers.Add(newPath);
        }
        return new ProjectState(manifests, markers);
    }

    /// <summary>The projects as they were at one commit, read from its files.</summary>
    private async Task<ProjectState> StateAtAsync(string gitDir, string commit, bool lines, IDictionary<string, string> env, CancellationToken ct)
    {
        try
        {
            var tree = await Git.RunAsync(gitDir, ["ls-tree", "-r", "--name-only", "-z", commit], env, null, TimeSpan.FromMinutes(2), ct);
            if (tree.ExitCode != 0) return ProjectState.Empty;
            var paths = tree.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var manifests = paths.Where(IsManifest).Take(lines ? 400 : 120).ToList();
            var read = new Dictionary<string, (HashSet<string>, HashSet<string>)>(StringComparer.Ordinal);
            if (manifests.Count > 0)
            {
                var blobs = await Git.CatFilesAsync(gitDir, manifests.Select(m => $"{commit}:{m}").ToList(), env, ct);
                for (var i = 0; i < manifests.Count; i++) if (blobs[i] is { } text) read[manifests[i]] = Frameworks.Read(manifests[i], text);
            }
            return new ProjectState(read, paths.Where(IsMarker).ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.Log.Debug("activity", $"Projects at {commit} in {gitDir}: {ex.Message}");
            return ProjectState.Empty;
        }
    }

    /// <summary>"old => new" and "dir/{old => new}/file" become the new path.</summary>
    public static string RenamedTo(string path)
    {
        var arrow = path.IndexOf(" => ", StringComparison.Ordinal);
        if (arrow < 0) return path;
        var open = path.LastIndexOf('{', arrow);
        var close = path.IndexOf('}', arrow);
        if (open >= 0 && close > arrow)
            return (path[..open] + path[(arrow + 4)..close] + path[(close + 1)..]).Replace("//", "/").TrimStart('/');
        return path[(arrow + 4)..];
    }
}

/// <summary>git, run without prompts, with output streamed or collected.</summary>
public static class Git
{
    private static readonly Dictionary<string, string> BaseEnv = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_OPTIONAL_LOCKS"] = "0", ["LC_ALL"] = "C.UTF-8", ["GCM_INTERACTIVE"] = "never", ["GIT_ASKPASS"] = "", ["SSH_ASKPASS"] = "",
        ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes",
    };

    public static void Check(Shell.RunResult r)
    {
        if (r.ExitCode != 0) throw new InvalidOperationException(Problem(r));
    }

    public static string Problem(Shell.RunResult r)
    {
        if (r.TimedOut) return "git took too long and was stopped.";
        var text = (r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim();
        // Keep the line that says what went wrong, not every progress line before it.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useful = lines.Where(l => l.StartsWith("fatal:") || l.StartsWith("error:") || l.StartsWith("remote:")).ToList();
        var msg = string.Join(" ", useful.Count > 0 ? useful : lines.TakeLast(2));
        return msg.Length > 400 ? msg[..400] + "…" : msg.Length > 0 ? msg : $"git failed (exit {r.ExitCode}).";
    }

    private static ProcessStartInfo Start(string cwd, IEnumerable<string> args, IDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = cwd };
        foreach (var a in new[] { "-c", "core.quotepath=off", "-c", "color.ui=never", "-c", "log.showSignature=false", "-c", "diff.renameLimit=1000" }.Concat(args)) psi.ArgumentList.Add(a);
        psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var (k, v) in BaseEnv) psi.Environment[k] = v;
        if (env is not null) foreach (var (k, v) in env) if (!k.StartsWith("AVAMUX_", StringComparison.Ordinal)) psi.Environment[k] = v;
        return psi;
    }

    public static async Task<Shell.RunResult> RunAsync(string cwd, IEnumerable<string> args, IDictionary<string, string>? env, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var output = new StringBuilder();
        return await StreamAsync(cwd, args, stdin, l => output.Append(l).Append('\n'), env, timeout, ct, output);
    }

    /// <summary>Runs git and hands each line of its output to <paramref name="onLine"/> as it arrives.</summary>
    public static async Task<Shell.RunResult> StreamAsync(string cwd, IEnumerable<string> args, string? stdin, Action<string> onLine, IDictionary<string, string>? env,
        TimeSpan timeout, CancellationToken ct, StringBuilder? collected = null)
    {
        Directory.CreateDirectory(cwd);
        using var p = new Process { StartInfo = Start(cwd, args, env) };
        p.Start();
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            var writer = Task.Run(async () =>
            {
                try { if (stdin is not null) await p.StandardInput.WriteAsync(stdin); }
                catch (IOException) { /* git stopped reading */ }
                finally { try { p.StandardInput.Close(); } catch { } }
            }, CancellationToken.None);
            while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } line) onLine(line);
            await p.WaitForExitAsync(cts.Token);
            await writer;
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(true); } catch { }
            if (!timedOut) throw;
        }
        var e = await stderr.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(x => x.IsCompletedSuccessfully ? x.Result : "");
        return new Shell.RunResult(timedOut ? -1 : p.ExitCode, collected?.ToString() ?? "", e, timedOut);
    }

    /// <summary>The text of several files (as rev:path), null for any that is missing or too big.</summary>
    public static async Task<string?[]> CatFilesAsync(string cwd, IReadOnlyList<string> specs, IDictionary<string, string>? env, CancellationToken ct, int maxBytes = 512 * 1024)
    {
        var result = new string?[specs.Count];
        using var p = new Process { StartInfo = Start(cwd, ["cat-file", "--batch"], env) };
        p.Start();
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var writer = Task.Run(async () =>
        {
            try { foreach (var s in specs) await p.StandardInput.WriteAsync(s + "\n"); }
            catch (IOException) { }
            finally { try { p.StandardInput.Close(); } catch { } }
        }, CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        var stream = new BufferedStream(p.StandardOutput.BaseStream);
        try
        {
            for (var i = 0; i < specs.Count; i++)
            {
                var header = await ReadLineAsync(stream, cts.Token);
                if (header is null) break;
                var parts = header.Split(' ');
                if (parts.Length < 3 || !long.TryParse(parts[2], out var size)) continue;   // "<spec> missing"
                var bytes = new byte[size];
                await stream.ReadExactlyAsync(bytes, cts.Token);
                stream.ReadByte();   // the newline after the contents
                if (parts[1] == "blob" && size <= maxBytes) result[i] = Encoding.UTF8.GetString(bytes);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { try { p.Kill(true); } catch { } }
        catch (EndOfStreamException) { }
        await writer;
        try { await p.WaitForExitAsync(ct); } catch (InvalidOperationException) { }
        return result;
    }

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
    }
}
