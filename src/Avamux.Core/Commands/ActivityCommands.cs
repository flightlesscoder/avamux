using System.Text.Json.Nodes;
using Avamux.Core.GitActivity;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    private void AddActivityCommands(AppServices s)
    {
        Add("activity config", "Print where the Git Activity settings file is, or its contents with --show.", [F("show", "Print the settings.")], a =>
            a.Bool("show") == true ? JsonUtil.ToNode(s.Activity.Config) : s.Activity.ConfigFile);
        Add("activity sources", "List the Git Activity sources and how many repositories each gave last time.", [], _ => new JsonArray(s.Activity.Config.Sources.Select(src =>
        {
            var st = s.Activity.SourceStatuses.FirstOrDefault(x => x.Id == src.Id);
            return (JsonNode)new JsonObject
            {
                ["id"] = src.Id, ["kind"] = src.Kind, ["name"] = src.Title, ["enabled"] = src.Enabled, ["repositories"] = st?.Repositories, ["error"] = st?.Error,
            };
        }).ToArray()));
        Add("activity add", "Add a Git Activity source: github <owner>, azure-devops <organization> [project], local <folder>, git <url>, or repositories.",
            [P("kind", "github, azure-devops, local, git or repositories.", true), P("target", "Owner, organisation, folder or clone URL."), P("project", "Azure DevOps project (empty: every project)."), O("name", "Shown name.")], a =>
            {
                var kind = ActivitySources.For(a.Require("kind")).Kind;
                var src = new ActivitySource { Kind = kind, Name = a.Str("name") ?? "" };
                var target = a.Str("target");
                switch (kind)
                {
                    case ActivitySource.GitHub: src.Owner = target ?? throw new ArgumentException("Give the organisation or user."); break;
                    case ActivitySource.AzureDevOps: src.Organization = target ?? throw new ArgumentException("Give the organisation."); src.Project = a.Str("project"); break;
                    case ActivitySource.Local: src.Path = target ?? throw new ArgumentException("Give the folder."); break;
                    case ActivitySource.Git: src.Repositories = [target ?? throw new ArgumentException("Give the clone URL.")]; break;
                }
                s.Activity.Update(c => c.Sources.Add(src));
                return JsonUtil.ToNode(s.Activity.Config.Sources[^1]);
            });
        Add("activity remove", "Remove a Git Activity source.", [P("id", "Source id (see activity sources).", true)], a =>
        {
            var id = a.Require("id");
            if (s.Activity.FindSource(id) is null) throw new KeyNotFoundException($"No source '{id}'.");
            s.Activity.Update(c => c.Sources.RemoveAll(x => x.Id == id));
            return "removed";
        });
        Add("activity repos", "List the repositories Git Activity reads, with what was found in each.", [O("filter", "Only names containing this.")], a =>
        {
            var f = a.Str("filter");
            var disabled = s.Activity.Config.DisabledRepositories.ToHashSet();
            return new JsonArray(s.Activity.Repos.Where(r => f is null || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).Select(r =>
            {
                var act = s.Activity.ActivityOf(r.Key);
                return (JsonNode)new JsonObject
                {
                    ["key"] = r.Key, ["name"] = r.Name, ["enabled"] = !disabled.Contains(r.Key), ["status"] = act?.Status, ["commits"] = act?.Commits.Count,
                    ["scanned"] = act?.ScannedAt?.ToString("yyyy-MM-dd HH:mm"), ["frameworks"] = act is null ? null : string.Join(", ", act.Frameworks), ["error"] = act?.Error,
                };
            }).ToArray());
        });
        Add("activity scan", "List the sources and read every repository (in the background). --wait waits for it to finish.",
            [F("wait", "Wait until it is done."), F("force", "Read everything again, even repositories that did not change."), F("no-discover", "Do not list the sources first.")], async a =>
            {
                var task = s.Activity.ScanAsync(discover: a.Bool("no-discover") != true, force: a.Bool("force") == true);
                // A whole organisation's history can take longer than a command may wait: report how far it got.
                if (a.Bool("wait") == true) { await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(4))); return JsonUtil.ToNode(s.Activity.Progress); }
                return "started";
            });
        Add("activity status", "Show whether Git Activity is reading, and how far it got.", [], _ => JsonUtil.ToNode(s.Activity.Progress));
        Add("activity stop", "Stop reading.", [], _ => { s.Activity.Cancel(); return "stopping"; });
        Add("activity top", "The top contributors, repositories, branches, languages, frameworks or AI tools of the period.",
            [P("by", "contributor, repository, branch, language, filetype, framework, aitool, aimodel, source…", true), O("count", "How many (default: topCount)."),
             O("days", "Days back (default: the configured period)."), O("from", "Start date (yyyy-MM-dd), or all."), O("to", "End date (yyyy-MM-dd), or now."), O("measure", "commits, lines, added, deleted, files or contributors."),
             F("no-mainline", "Leave out mainline branches."), O("people-active", "Only contributors with a commit in the last N days of the range."),
             O("repos-active", "Only repositories with a commit in the last N days of the range."), O("ai", "all, with, without or agent.")], a =>
            {
                s.Activity.EnsureLoaded();
                var engine = s.Activity.Engine();
                var dim = Dimension(a.Require("by"));
                var filter = new ActivityFilter
                {
                    From = From(a, s), To = To(a), HideBots = s.Activity.Config.HideBots,
                    PeopleActiveDays = a.Int("people-active") ?? s.Activity.Config.ActivePeopleDays, ReposActiveDays = a.Int("repos-active") ?? s.Activity.Config.ActiveReposDays,
                    Branches = a.Bool("no-mainline") == true ? BranchScope.ExcludeMainline : BranchScope.All,
                    Ai = a.Str("ai")?.ToLowerInvariant() switch { "with" => AiScope.WithAi, "without" => AiScope.WithoutAi, "agent" => AiScope.AgentOnly, _ => AiScope.All },
                };
                var measure = a.Str("measure")?.ToLowerInvariant() switch
                {
                    "lines" => ActivityMeasure.LinesChanged, "added" => ActivityMeasure.LinesAdded, "deleted" => ActivityMeasure.LinesDeleted,
                    "files" => ActivityMeasure.FilesChanged, "contributors" or "people" => ActivityMeasure.Contributors, _ => ActivityMeasure.Commits,
                };
                var rows = engine.Filter(filter);
                return new JsonArray(engine.Top(rows, dim, measure, filter, a.Int("count") ?? s.Activity.Config.TopCount).Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name, ["value"] = t.Value, ["share"] = Math.Round(t.Share * 100, 1), ["commits"] = t.Tally.Commits, ["added"] = t.Tally.Added, ["deleted"] = t.Tally.Deleted,
                    ["contributors"] = t.Tally.People?.Count, ["aiCommits"] = t.Tally.AiCommits, ["trend"] = Math.Round(t.Trend * 100), ["mainline"] = dim == ActivityDimension.Branch ? t.Mainline : null,
                }).ToArray());
            });
        Add("activity summary", "Totals for the period: commits, people, repositories, lines, AI use.",
            [O("days", "Days back (default: the configured period)."), O("from", "Start date (yyyy-MM-dd), or all."), O("to", "End date (yyyy-MM-dd), or now."),
             O("people-active", "Only contributors with a commit in the last N days of the range."), O("repos-active", "Only repositories with a commit in the last N days of the range.")], a =>
        {
            s.Activity.EnsureLoaded();
            var engine = s.Activity.Engine();
            var filter = new ActivityFilter
            {
                From = From(a, s), To = To(a), HideBots = s.Activity.Config.HideBots,
                PeopleActiveDays = a.Int("people-active") ?? s.Activity.Config.ActivePeopleDays, ReposActiveDays = a.Int("repos-active") ?? s.Activity.Config.ActiveReposDays,
            };
            return JsonUtil.ToNode(engine.Summarize(engine.Filter(filter)));
        });
    }

    private static DateTimeOffset From(Args a, AppServices s) => a.Str("from") is { } f ? DateArg(f, DateTimeOffset.MinValue)
        : a.Int("days") is { } d ? DateTimeOffset.Now.Date.AddDays(-d) : GitActivityService.SinceOf(s.Activity.Config) is var since && ActivityScanner.AllTime(since) ? DateTimeOffset.MinValue : since;

    private static DateTimeOffset To(Args a) => a.Str("to") is { } t ? DateArg(t, DateTimeOffset.MaxValue) is var d && d != DateTimeOffset.MaxValue ? d.AddDays(1).AddTicks(-1) : d : DateTimeOffset.MaxValue;

    private static DateTimeOffset DateArg(string text, DateTimeOffset open) =>
        text.Trim().ToLowerInvariant() is "all" or "beginning" or "now" or "present" or "" ? open
        : DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var d) ? new DateTimeOffset(d.Date, d.Offset)
        : throw new ArgumentException($"'{text}' is not a date (yyyy-MM-dd), all or now.");

    internal static ActivityDimension Dimension(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        return t switch
        {
            "contributor" or "contributors" or "people" or "person" or "author" or "authors" => ActivityDimension.Contributor,
            "repo" or "repos" or "repository" or "repositories" => ActivityDimension.Repository,
            "branch" or "branches" => ActivityDimension.Branch,
            "language" or "languages" or "lang" => ActivityDimension.Language,
            "filetype" or "filetypes" or "ext" or "extension" => ActivityDimension.FileType,
            "kind" or "category" or "filecategory" => ActivityDimension.FileCategory,
            "framework" or "frameworks" => ActivityDimension.Framework,
            "frameworkkind" or "frameworkcategory" => ActivityDimension.FrameworkCategory,
            "tests" or "codeortests" => ActivityDimension.CodeOrTests,
            "ai" or "aiuse" => ActivityDimension.AiUse,
            "aitool" or "aitools" or "tool" => ActivityDimension.AiTool,
            "aimodel" or "aimodels" or "model" or "models" => ActivityDimension.AiModel,
            "source" or "sources" => ActivityDimension.Source,
            "day" or "days" => ActivityDimension.Day, "week" or "weeks" => ActivityDimension.Week, "month" or "months" => ActivityDimension.Month,
            "weekday" or "weekdays" => ActivityDimension.Weekday, "hour" or "hours" => ActivityDimension.Hour,
            _ => throw new ArgumentException($"Unknown breakdown '{text}'."),
        };
    }
}
