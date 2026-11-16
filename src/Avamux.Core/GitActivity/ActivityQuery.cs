using System.Globalization;

namespace Avamux.Core.GitActivity;

/// <summary>What the figures can be broken down by.</summary>
public enum ActivityDimension
{
    Repository, Contributor, Branch, Source,
    Language, FileType, FileCategory, Framework, FrameworkCategory, CodeOrTests,
    AiUse, AiTool, AiModel,
    Day, Week, Month, Weekday, Hour,
}

/// <summary>What is counted.</summary>
public enum ActivityMeasure { Commits, LinesChanged, LinesAdded, LinesDeleted, FilesChanged, Contributors }

public enum BranchScope { All, ExcludeMainline, OnlyMainline }
public enum AiScope { All, WithAi, WithoutAi, AgentOnly }

/// <summary>Which commits (and which parts of them) count.</summary>
public sealed class ActivityFilter
{
    /// <summary>The start; <see cref="DateTimeOffset.MinValue"/> is the beginning of time.</summary>
    public DateTimeOffset From { get; set; } = DateTimeOffset.Now.AddDays(-61);
    /// <summary>The end; <see cref="DateTimeOffset.MaxValue"/> is the present (and anything after it).</summary>
    public DateTimeOffset To { get; set; } = DateTimeOffset.MaxValue;
    /// <summary>Only contributors with a commit in this many days before the end of the range (0: everyone).</summary>
    public int PeopleActiveDays { get; set; }
    /// <summary>Only repositories with a commit in this many days before the end of the range (0: all of them).</summary>
    public int ReposActiveDays { get; set; }
    public bool OpenStart => From.Year <= 1;
    public bool OpenEnd => To.Year >= 9999;
    public BranchScope Branches { get; set; }
    public AiScope Ai { get; set; }
    public bool HideBots { get; set; } = true;
    public bool IncludeMerges { get; set; }
    /// <summary>Contributor keys left out (on top of the config's list).</summary>
    public HashSet<string> ExcludedPeople { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Only these values of a dimension: clicking a row or cell narrows the view this way.</summary>
    public Dictionary<ActivityDimension, HashSet<string>> Only { get; set; } = [];
    /// <summary>Words in the subject, a hash, or an author.</summary>
    public string? Text { get; set; }

    public ActivityFilter Clone() => new()
    {
        From = From, To = To, PeopleActiveDays = PeopleActiveDays, ReposActiveDays = ReposActiveDays, Branches = Branches, Ai = Ai, HideBots = HideBots, IncludeMerges = IncludeMerges, Text = Text,
        ExcludedPeople = new(ExcludedPeople, StringComparer.OrdinalIgnoreCase),
        Only = Only.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.Ordinal)),
    };
}

/// <summary>A commit that passed the filter, with the parts of it that count.</summary>
public sealed record Selected(ActivityCommit Commit, IReadOnlyList<ActivityPart> Parts, string Person)
{
    public int Added => Parts.Sum(p => p.Added);
    public int Deleted => Parts.Sum(p => p.Deleted);
    public int Files => Parts.Sum(p => p.Files);
}

/// <summary>Totals for a row (or cell): counted once per commit.</summary>
public sealed class Tally
{
    public int Commits, AiCommits, AgentCommits, Files;
    public long Added, Deleted;
    public HashSet<string>? People;
    public DateTimeOffset Last;

    public double Value(ActivityMeasure m) => m switch
    {
        ActivityMeasure.Commits => Commits, ActivityMeasure.LinesChanged => Added + Deleted, ActivityMeasure.LinesAdded => Added,
        ActivityMeasure.LinesDeleted => Deleted, ActivityMeasure.FilesChanged => Files, _ => People?.Count ?? 0,
    };
}

/// <summary>A grid of values: rows by one dimension, columns by another.</summary>
public sealed class ActivityMatrix
{
    public required ActivityDimension RowDimension { get; init; }
    public required ActivityDimension ColumnDimension { get; init; }
    public required ActivityMeasure Measure { get; init; }
    public required List<string> Rows { get; init; }
    public required List<string> Columns { get; init; }
    /// <summary>[row, column].</summary>
    public required double[,] Values { get; init; }
    public required Dictionary<string, Tally> RowTallies { get; init; }
    public required double[] ColumnTotals { get; init; }
    /// <summary>Rows left out because only the top ones are shown.</summary>
    public int HiddenRows { get; init; }
    public double Max { get; init; }
    public double Total { get; init; }
}

public sealed record TopEntry(string Key, string Name, double Value, double Share, Tally Tally, double[] Weekly, double Trend, bool Mainline, string? Detail);

public sealed record ActivitySummary(int Commits, int People, int Repositories, int Branches, long Added, long Deleted, int Files, int AiCommits, int AgentCommits,
    int AiPeople, int Languages, int Frameworks, string? BusiestDay, string? BusiestHour, DateTimeOffset? First, DateTimeOffset? Last);

/// <summary>
/// Filters and adds up commits: the one place every view of Git Activity gets its numbers from, so the heatmap, the
/// leaderboards and the totals always agree.
/// </summary>
public sealed class ActivityEngine
{
    public static readonly string[] WeekdayKeys = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    public const string None = "(none)";

    private readonly IReadOnlyList<ActivityCommit> _commits;
    private readonly IReadOnlyDictionary<string, ActivityRepo> _repos;
    private readonly GitActivityConfig _config;
    private readonly Func<string, string> _sourceName;
    private readonly Identities _ids;
    private readonly Dictionary<string, string> _frameworkCategory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _personName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string?), bool> _mainline = new();
    private readonly HashSet<string> _configExcluded;

    public ActivityEngine(IReadOnlyList<ActivityCommit> commits, IReadOnlyDictionary<string, ActivityRepo> repos, GitActivityConfig config, Func<string, string>? sourceName = null)
    {
        _commits = commits;
        _repos = repos;
        _config = config;
        _sourceName = sourceName ?? (s => s);
        _ids = new Identities(config);
        foreach (var f in Frameworks.BuiltIn.Concat(config.Frameworks)) if (f.Name.Length > 0) _frameworkCategory[f.Name] = f.Category;
        _configExcluded = new HashSet<string>(config.ExcludedContributors.Select(e => e.Trim()).Where(e => e.Length > 0), StringComparer.OrdinalIgnoreCase);
        // Shown names: the name used most often under each key.
        foreach (var g in commits.GroupBy(c => PersonKey(c), StringComparer.OrdinalIgnoreCase))
            _personName[g.Key] = g.GroupBy(c => c.PersonName).OrderByDescending(n => n.Count()).First().Key;
    }

    public IReadOnlyList<ActivityCommit> AllCommits => _commits;

    public string PersonKey(ActivityCommit c) => _ids.Key(c.PersonName, c.PersonEmail);

    public bool IsMainline(ActivityCommit c)
    {
        var def = _repos.TryGetValue(c.RepoKey, out var r) ? r.DefaultBranch : null;
        lock (_mainline)
        {
            if (_mainline.TryGetValue((c.Branch, def), out var hit)) return hit;
            return _mainline[(c.Branch, def)] = string.Equals(c.Branch, def, StringComparison.OrdinalIgnoreCase) || Glob.Any(_config.MainlineBranches, c.Branch);
        }
    }

    public static bool PartLevel(ActivityDimension d) => d is ActivityDimension.Language or ActivityDimension.FileType or ActivityDimension.FileCategory
        or ActivityDimension.Framework or ActivityDimension.FrameworkCategory or ActivityDimension.CodeOrTests;

    public static bool IsTime(ActivityDimension d) => d is ActivityDimension.Day or ActivityDimension.Week or ActivityDimension.Month or ActivityDimension.Weekday or ActivityDimension.Hour;

    public static string Label(ActivityDimension d) => d switch
    {
        ActivityDimension.Repository => "Repository", ActivityDimension.Contributor => "Contributor", ActivityDimension.Branch => "Branch", ActivityDimension.Source => "Source",
        ActivityDimension.Language => "Language", ActivityDimension.FileType => "File type", ActivityDimension.FileCategory => "Kind of file",
        ActivityDimension.Framework => "Framework", ActivityDimension.FrameworkCategory => "Framework kind", ActivityDimension.CodeOrTests => "Code or tests",
        ActivityDimension.AiUse => "AI use", ActivityDimension.AiTool => "AI tool", ActivityDimension.AiModel => "AI model",
        ActivityDimension.Day => "Day", ActivityDimension.Week => "Week", ActivityDimension.Month => "Month", ActivityDimension.Weekday => "Weekday", _ => "Hour of day",
    };

    public static string Label(ActivityMeasure m) => m switch
    {
        ActivityMeasure.Commits => "Commits", ActivityMeasure.LinesChanged => "Lines changed", ActivityMeasure.LinesAdded => "Lines added",
        ActivityMeasure.LinesDeleted => "Lines removed", ActivityMeasure.FilesChanged => "Files changed", _ => "Contributors",
    };

    /// <summary>The values of a dimension for a commit (and, for file dimensions, one part of it).</summary>
    public IEnumerable<string> Keys(ActivityDimension d, ActivityCommit c, ActivityPart? part)
    {
        switch (d)
        {
            case ActivityDimension.Repository: yield return c.RepoKey; break;
            case ActivityDimension.Contributor: yield return PersonKey(c); break;
            case ActivityDimension.Branch: yield return c.RepoKey + "\u001f" + c.Branch; break;
            case ActivityDimension.Source: yield return _repos.TryGetValue(c.RepoKey, out var r) ? r.SourceId : ""; break;
            case ActivityDimension.Language: if (part is not null) yield return Languages.Lookup(part.Key, _config.Languages).Language; break;
            case ActivityDimension.FileType: if (part is not null) yield return part.Key; break;
            case ActivityDimension.FileCategory: if (part is not null) yield return part.Test ? Languages.Test : Languages.Lookup(part.Key, _config.Languages).Category; break;
            case ActivityDimension.CodeOrTests: if (part is not null) yield return part.Test ? "Tests" : "Code"; break;
            case ActivityDimension.Framework:
                if (part is null) break;
                if (part.Frameworks.Length == 0) yield return None;
                else foreach (var f in part.Frameworks.Split('|')) yield return f;
                break;
            case ActivityDimension.FrameworkCategory:
                if (part is null) break;
                if (part.Frameworks.Length == 0) { yield return None; break; }
                foreach (var cat in part.Frameworks.Split('|').Select(f => _frameworkCategory.GetValueOrDefault(f, Frameworks.Library)).Distinct()) yield return cat;
                break;
            case ActivityDimension.AiUse: yield return c.Agent ? "AI agent" : c.WithAi ? "AI-assisted" : "No AI"; break;
            case ActivityDimension.AiTool:
                if (c.AiTools.Length == 0) yield return "No AI"; else foreach (var t in c.AiTools) yield return t;
                break;
            case ActivityDimension.AiModel:
                if (c.AiModels.Length > 0) foreach (var m in c.AiModels) yield return m;
                else yield return c.WithAi ? "(model not named)" : "No AI";
                break;
            case ActivityDimension.Day: yield return c.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case ActivityDimension.Week: yield return WeekOf(c.When.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case ActivityDimension.Month: yield return c.When.ToString("yyyy-MM", CultureInfo.InvariantCulture); break;
            case ActivityDimension.Weekday: yield return WeekdayKeys[((int)c.When.DayOfWeek + 6) % 7]; break;
            case ActivityDimension.Hour: yield return c.When.Hour.ToString("00", CultureInfo.InvariantCulture); break;
        }
    }

    public static DateTime WeekOf(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>How a key is shown.</summary>
    public string Display(ActivityDimension d, string key)
    {
        switch (d)
        {
            case ActivityDimension.Repository: return _repos.TryGetValue(key, out var r) ? r.Name : key;
            case ActivityDimension.Contributor: return _personName.GetValueOrDefault(key, key);
            case ActivityDimension.Branch:
                var sep = key.IndexOf('\u001f');
                return sep < 0 ? key : $"{key[(sep + 1)..]}  ·  {Display(ActivityDimension.Repository, key[..sep])}";
            case ActivityDimension.Source: return _sourceName(key);
            case ActivityDimension.FileType: return key.StartsWith('.') ? $"{key}  ({Languages.Lookup(key, _config.Languages).Language})" : key;
            case ActivityDimension.Week:
                return DateTime.TryParse(key, CultureInfo.InvariantCulture, out var w) ? w.ToString("d MMM", CultureInfo.CurrentCulture) : key;
            case ActivityDimension.Day:
                return DateTime.TryParse(key, CultureInfo.InvariantCulture, out var day) ? day.ToString("ddd d MMM", CultureInfo.CurrentCulture) : key;
            case ActivityDimension.Month:
                return DateTime.TryParse(key + "-01", CultureInfo.InvariantCulture, out var mo) ? mo.ToString("MMM yyyy", CultureInfo.CurrentCulture) : key;
            case ActivityDimension.Hour: return key + ":00";
            default: return key;
        }
    }

    /// <summary>The branch part of a Branch key, and the repository part.</summary>
    public static (string Repo, string Branch) SplitBranch(string key)
    {
        var sep = key.IndexOf('\u001f');
        return sep < 0 ? ("", key) : (key[..sep], key[(sep + 1)..]);
    }

    public string FrameworkCategory(string framework) => _frameworkCategory.GetValueOrDefault(framework, Frameworks.Library);

    // ---- filtering ----

    public List<Selected> Filter(ActivityFilter f)
    {
        var partFilters = f.Only.Where(kv => PartLevel(kv.Key) && kv.Value.Count > 0).ToList();
        var commitFilters = f.Only.Where(kv => !PartLevel(kv.Key) && kv.Value.Count > 0).ToList();
        var text = f.Text?.Trim();
        // Gone quiet: contributors and repositories whose last commit (up to the end of the range) is older than asked.
        var end = f.OpenEnd ? DateTimeOffset.Now : f.To;
        Dictionary<string, DateTimeOffset>? lastPerson = null, lastRepo = null;
        if (f.PeopleActiveDays > 0 || f.ReposActiveDays > 0)
        {
            lastPerson = new(StringComparer.OrdinalIgnoreCase);
            lastRepo = new(StringComparer.Ordinal);
            foreach (var c in _commits)
            {
                if (c.When > end) continue;
                var p = PersonKey(c);
                if (!lastPerson.TryGetValue(p, out var lp) || c.When > lp) lastPerson[p] = c.When;
                if (!lastRepo.TryGetValue(c.RepoKey, out var lr) || c.When > lr) lastRepo[c.RepoKey] = c.When;
            }
        }
        var peopleCut = end.AddDays(-f.PeopleActiveDays);
        var reposCut = end.AddDays(-f.ReposActiveDays);
        var list = new List<Selected>(_commits.Count / 2);
        foreach (var c in _commits)
        {
            if (c.When < f.From || c.When > f.To) continue;
            if (c.Merge && !f.IncludeMerges) continue;
            if (c.Bot && f.HideBots) continue;
            switch (f.Ai)
            {
                case AiScope.WithAi when !c.WithAi: continue;
                case AiScope.WithoutAi when c.WithAi: continue;
                case AiScope.AgentOnly when !c.Agent: continue;
            }
            if (f.Branches != BranchScope.All && IsMainline(c) != (f.Branches == BranchScope.OnlyMainline)) continue;
            var person = PersonKey(c);
            if (f.PeopleActiveDays > 0 && lastPerson!.GetValueOrDefault(person) < peopleCut) continue;
            if (f.ReposActiveDays > 0 && lastRepo!.GetValueOrDefault(c.RepoKey) < reposCut) continue;
            if (f.ExcludedPeople.Contains(person) || _configExcluded.Count > 0 && (_configExcluded.Contains(person) || _configExcluded.Contains(c.PersonName) || _configExcluded.Contains(c.PersonEmail) || _configExcluded.Contains(c.AuthorEmail))) continue;
            if (!string.IsNullOrEmpty(text) && !(c.Subject.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Hash.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                    || c.PersonName.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Branch.Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
            var ok = true;
            foreach (var (dim, values) in commitFilters)
                if (!Keys(dim, c, null).Any(values.Contains)) { ok = false; break; }
            if (!ok) continue;
            IReadOnlyList<ActivityPart> parts = c.Parts;
            if (partFilters.Count > 0)
            {
                parts = c.Parts.Where(p => partFilters.All(pf => Keys(pf.Key, c, p).Any(pf.Value.Contains))).ToList();
                if (parts.Count == 0) continue;
            }
            list.Add(new Selected(c, parts, person));
        }
        return list;
    }

    // ---- adding up ----

    /// <summary>Breaks the selection down by two dimensions. Only the top <paramref name="maxRows"/> rows are kept (time rows are all kept).</summary>
    public ActivityMatrix Pivot(IReadOnlyList<Selected> rows, ActivityDimension rowDim, ActivityDimension colDim, ActivityMeasure measure, ActivityFilter f, int maxRows = 30)
    {
        var people = measure == ActivityMeasure.Contributors;
        var cells = new Dictionary<(string R, string C), Tally>();
        var rowTallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var colTallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var partLevel = PartLevel(rowDim) || PartLevel(colDim);
        var local = new Dictionary<(string R, string C), (int Files, long Added, long Deleted)>();
        var localRows = new Dictionary<string, (int Files, long Added, long Deleted)>(StringComparer.Ordinal);
        var localCols = new Dictionary<string, (int Files, long Added, long Deleted)>(StringComparer.Ordinal);

        foreach (var s in rows)
        {
            local.Clear(); localRows.Clear(); localCols.Clear();
            void Add(string r, string c, int files, long added, long deleted)
            {
                var v = local.GetValueOrDefault((r, c));
                local[(r, c)] = (v.Files + files, v.Added + added, v.Deleted + deleted);
            }
            if (partLevel)
            {
                foreach (var p in s.Parts)
                    foreach (var r in Keys(rowDim, s.Commit, p))
                        foreach (var c in Keys(colDim, s.Commit, p)) Add(r, c, p.Files, p.Added, p.Deleted);
            }
            else
            {
                int files = s.Files; long added = s.Added, deleted = s.Deleted;
                foreach (var r in Keys(rowDim, s.Commit, null))
                    foreach (var c in Keys(colDim, s.Commit, null)) Add(r, c, files, added, deleted);
            }
            foreach (var ((r, c), v) in local)
            {
                Count(cells, (r, c), v);
                localRows[r] = Sum(localRows.GetValueOrDefault(r), v);
                localCols[c] = Sum(localCols.GetValueOrDefault(c), v);
            }
            foreach (var (r, v) in localRows) Count(rowTallies, r, v);
            foreach (var (c, v) in localCols) Count(colTallies, c, v);

            void Count<TKey>(Dictionary<TKey, Tally> into, TKey key, (int Files, long Added, long Deleted) v) where TKey : notnull
            {
                if (!into.TryGetValue(key, out var t)) into[key] = t = new Tally { People = people || typeof(TKey) == typeof(string) ? new(StringComparer.OrdinalIgnoreCase) : null };
                t.Commits++;
                if (s.Commit.WithAi) t.AiCommits++;
                if (s.Commit.Agent) t.AgentCommits++;
                t.Files += v.Files; t.Added += v.Added; t.Deleted += v.Deleted;
                t.People?.Add(s.Person);
                if (s.Commit.When > t.Last) t.Last = s.Commit.When;
            }
        }

        var columns = Ordered(colDim, colTallies, f, measure, int.MaxValue, out _);
        var rowKeys = Ordered(rowDim, rowTallies, f, measure, maxRows, out var hidden);
        var values = new double[rowKeys.Count, columns.Count];
        double max = 0;
        var colIndex = columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.Ordinal);
        var rowIndex = rowKeys.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i, StringComparer.Ordinal);
        foreach (var ((r, c), t) in cells)
        {
            if (!rowIndex.TryGetValue(r, out var ri) || !colIndex.TryGetValue(c, out var ci)) continue;
            var v = values[ri, ci] = t.Value(measure);
            if (v > max) max = v;
        }
        return new ActivityMatrix
        {
            RowDimension = rowDim, ColumnDimension = colDim, Measure = measure, Rows = rowKeys, Columns = columns, Values = values, Max = max, HiddenRows = hidden,
            RowTallies = rowTallies, ColumnTotals = columns.Select(c => colTallies.TryGetValue(c, out var t) ? t.Value(measure) : 0).ToArray(),
            Total = rowTallies.Values.Sum(t => t.Value(measure)),
        };
    }

    private static (int, long, long) Sum((int Files, long Added, long Deleted) a, (int Files, long Added, long Deleted) b) => (a.Files + b.Files, a.Added + b.Added, a.Deleted + b.Deleted);

    /// <summary>The keys of a dimension in reading order: time in time order (every step, even empty ones), the rest largest first.</summary>
    private static List<string> Ordered(ActivityDimension d, Dictionary<string, Tally> tallies, ActivityFilter f, ActivityMeasure m, int max, out int hidden)
    {
        hidden = 0;
        switch (d)
        {
            case ActivityDimension.Weekday: return [.. WeekdayKeys];
            case ActivityDimension.Hour: return Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
            case ActivityDimension.Day or ActivityDimension.Week or ActivityDimension.Month:
                // Every step of the range, empty ones too; an open end stops at the first or last commit (or today).
                DateTime KeyDate(string k) => DateTime.TryParse(d == ActivityDimension.Month ? k + "-01" : k, CultureInfo.InvariantCulture, out var x) ? x : DateTime.Today;
                var first = tallies.Count > 0 ? KeyDate(tallies.Keys.Min(StringComparer.Ordinal)!) : DateTime.Today;
                var last = tallies.Count > 0 ? KeyDate(tallies.Keys.Max(StringComparer.Ordinal)!) : DateTime.Today;
                var start = f.OpenStart ? first : f.From.Date;
                var to = f.OpenEnd ? (last > DateTime.Today ? last : DateTime.Today) : f.To.Date;
                if (to > DateTime.Today && last <= DateTime.Today) to = DateTime.Today;
                var list = new List<string>();
                if (d == ActivityDimension.Day) for (var x = start; x <= to; x = x.AddDays(1)) list.Add(x.ToString("yyyy-MM-dd"));
                else if (d == ActivityDimension.Week) for (var x = WeekOf(start); x <= to; x = x.AddDays(7)) list.Add(x.ToString("yyyy-MM-dd"));
                else for (var x = new DateTime(start.Year, start.Month, 1); x <= to; x = x.AddMonths(1)) list.Add(x.ToString("yyyy-MM"));
                return list;
            default:
                var ordered = tallies.OrderByDescending(kv => kv.Value.Value(m)).ThenByDescending(kv => kv.Value.Commits).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key).ToList();
                hidden = Math.Max(0, ordered.Count - max);
                return ordered.Take(max).ToList();
        }
    }

    /// <summary>The top entries of a dimension, each with its weekly figures and how it is trending.</summary>
    public List<TopEntry> Top(IReadOnlyList<Selected> rows, ActivityDimension dim, ActivityMeasure measure, ActivityFilter f, int count)
    {
        var m = Pivot(rows, dim, ActivityDimension.Week, measure, f, count);
        var total = m.Total;
        var list = new List<TopEntry>();
        for (var i = 0; i < m.Rows.Count; i++)
        {
            var key = m.Rows[i];
            var weekly = Enumerable.Range(0, m.Columns.Count).Select(c => m.Values[i, c]).ToArray();
            var tally = m.RowTallies[key];
            var value = tally.Value(measure);
            list.Add(new TopEntry(key, Display(dim, key), value, total > 0 ? value / total : 0, tally, weekly, Trend(weekly),
                dim == ActivityDimension.Branch && IsMainlineKey(key), Detail(dim, key)));
        }
        return list;
    }

    private bool IsMainlineKey(string branchKey)
    {
        var (repo, branch) = SplitBranch(branchKey);
        var def = _repos.TryGetValue(repo, out var r) ? r.DefaultBranch : null;
        return string.Equals(branch, def, StringComparison.OrdinalIgnoreCase) || Glob.Any(_config.MainlineBranches, branch);
    }

    private string? Detail(ActivityDimension dim, string key) => dim switch
    {
        ActivityDimension.Framework => FrameworkCategory(key),
        _ => null,
    };

    /// <summary>The later half of the period against the earlier half: +1 is twice as much, -0.5 half as much. Partial last weeks count in proportion.</summary>
    public static double Trend(double[] weekly)
    {
        if (weekly.Length < 2) return 0;
        var half = weekly.Length / 2;
        var early = weekly[..half].Sum();
        var late = weekly[(weekly.Length - half)..].Sum();
        if (early <= 0) return late > 0 ? 1 : 0;
        return (late - early) / early;
    }

    public ActivitySummary Summarize(IReadOnlyList<Selected> rows)
    {
        if (rows.Count == 0) return new ActivitySummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null);
        var people = rows.Select(r => r.Person).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var aiPeople = rows.Where(r => r.Commit.WithAi).Select(r => r.Person).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var languages = rows.SelectMany(r => r.Parts).Select(p => Languages.Lookup(p.Key, _config.Languages).Language).Distinct().Count();
        var frameworks = rows.SelectMany(r => r.Parts).SelectMany(p => p.Frameworks.Split('|', StringSplitOptions.RemoveEmptyEntries)).Distinct().Count();
        var day = rows.GroupBy(r => ((int)r.Commit.When.DayOfWeek + 6) % 7).MaxBy(g => g.Count())!.Key;
        var hour = rows.GroupBy(r => r.Commit.When.Hour).MaxBy(g => g.Count())!.Key;
        return new ActivitySummary(rows.Count, people, rows.Select(r => r.Commit.RepoKey).Distinct().Count(), rows.Select(r => r.Commit.RepoKey + "\u001f" + r.Commit.Branch).Distinct().Count(),
            rows.Sum(r => (long)r.Added), rows.Sum(r => (long)r.Deleted), rows.Sum(r => r.Files), rows.Count(r => r.Commit.WithAi), rows.Count(r => r.Commit.Agent), aiPeople,
            languages, frameworks, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)((day + 1) % 7)), $"{hour:00}:00",
            rows.Min(r => r.Commit.When), rows.Max(r => r.Commit.When));
    }
}
