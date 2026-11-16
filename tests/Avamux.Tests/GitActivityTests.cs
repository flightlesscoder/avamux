using Avamux.Core;
using Avamux.Core.GitActivity;

namespace Avamux.Tests;

public class GitActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "avamux-activity-" + Guid.NewGuid().ToString("N")[..8]);

    public GitActivityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static void Git(string dir, params string[] args) => GitEnv(dir, null, args);

    private static void GitEnv(string dir, Dictionary<string, string>? env, params string[] args)
    {
        var r = Shell.RunAsync("git", args, dir, env, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Stderr}");
    }

    private static void Commit(string dir, string author, string email, DateTimeOffset when, string message, params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            Git(dir, "add", path);
        }
        var date = when.ToString("yyyy-MM-ddTHH:mm:sszzz");
        GitEnv(dir, new() { ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_EMAIL"] = email, ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_NAME"] = author,
            ["GIT_COMMITTER_EMAIL"] = email, ["GIT_COMMITTER_DATE"] = date }, "commit", "-q", "--allow-empty", "-m", message);
    }

    /// <summary>A repository with an old commit, main work, a feature branch, AI-assisted and agent commits, a .NET API and a Next.js app.</summary>
    private string MakeRepo()
    {
        var dir = Path.Combine(_root, "org-repo");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        var now = DateTimeOffset.Now;
        Commit(dir, "Old Timer", "old@x.com", now.AddDays(-400), "ancient", ("README.md", "hello\n"));
        Commit(dir, "Ana Dev", "ana@x.com", now.AddDays(-30), "api project",
            ("api/Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="MediatR" Version="12.0.0" /><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup></Project>"""),
            ("api/Program.cs", "var a = 1;\nvar b = 2;\n"));
        Commit(dir, "Ana Dev", "ana@x.com", now.AddDays(-20), "web app",
            ("web/package.json", """{ "dependencies": { "next": "15.0.0", "react": "19.0.0" } }"""), ("web/app/page.tsx", "export default function P() { return null }\n"),
            ("web/package-lock.json", new string('x', 1000)));
        Commit(dir, "Ben Coder", "ben@x.com", now.AddDays(-10), "Add handler\n\nCo-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>\n\n🤖 Generated with [Claude Code](https://claude.com/claude-code)",
            ("api/Handler.cs", "class H {}\nclass I {}\nclass J {}\n"), ("api/Tests/HandlerTests.cs", "class T {}\n"));
        Git(dir, "switch", "-q", "-c", "feature/login");
        Commit(dir, "Copilot", "198982749+Copilot@users.noreply.github.com", now.AddDays(-3), "Add login page\n\nCo-authored-by: Ana Dev <ana@x.com>",
            ("web/app/login/page.tsx", "export default function L() { return null }\n"), ("infra/main.tf", "resource \"x\" \"y\" {}\n"));
        Git(dir, "switch", "-q", "main");
        Commit(dir, "dependabot[bot]", "49699333+dependabot[bot]@users.noreply.github.com", now.AddDays(-2), "Bump next", ("web/package.json", """{ "dependencies": { "next": "15.0.1", "react": "19.0.0" } }"""));
        return dir;
    }

    private static GitActivityConfig Config() => new() { Days = 61 };

    [Fact]
    public async Task A_local_repository_is_read_with_branches_people_AI_languages_and_frameworks()
    {
        var dir = MakeRepo();
        var config = Config();
        var repo = new ActivityRepo { Key = "local:" + dir, Kind = ActivitySource.Local, Name = "org-repo", LocalPath = dir };
        var scanner = new ActivityScanner(config, Path.Combine(_root, "mirrors"));
        var since = DateTimeOffset.Now.AddDays(-61);
        var result = await scanner.ScanAsync(repo, new Dictionary<string, string>(), since, null, null, CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Equal(5, result.Commits.Count);   // the 400-day-old commit is outside the period
        Assert.Contains("Next.js", result.Frameworks);
        Assert.Contains("MediatR", result.Frameworks);
        Assert.Contains("ASP.NET Core", result.Frameworks);
        Assert.Contains("Terraform", result.Frameworks);

        var login = result.Commits.Single(c => c.Subject == "Add login page");
        Assert.Equal("feature/login", login.Branch);
        Assert.True(login.Agent);
        Assert.Equal(["GitHub Copilot"], login.AiTools);
        Assert.Equal("Ana Dev", login.PersonName);   // the agent's commit belongs to the person it worked with

        var handler = result.Commits.Single(c => c.Subject == "Add handler");
        Assert.Equal("main", handler.Branch);
        Assert.Equal(["Claude Code"], handler.AiTools);
        Assert.Contains("Claude Opus 4.5", handler.AiModels);
        Assert.False(handler.Agent);
        Assert.Equal(4, handler.Added);
        Assert.Contains(handler.Parts, p => p.Test && p.Key == ".cs");
        Assert.All(handler.Parts, p => Assert.Contains("MediatR", p.Frameworks.Split('|')));
        // A test framework claims the tests, not the code they test.
        Assert.Contains("xUnit", handler.Parts.Single(p => p.Test).Frameworks.Split('|'));
        Assert.DoesNotContain("xUnit", handler.Parts.Single(p => !p.Test).Frameworks.Split('|'));

        var web = result.Commits.Single(c => c.Subject == "web app");
        Assert.DoesNotContain(web.Parts, p => p.Key == ".json" && p.Added >= 1000 || p.Key == "package-lock.json");   // lock files are not counted
        Assert.Contains(web.Parts, p => p.Key == ".tsx" && p.Frameworks.Contains("Next.js") && !p.Frameworks.Contains("MediatR"));
        Assert.True(result.Commits.Single(c => c.Subject == "Bump next").Bot);

        // The same tips again: nothing is read again.
        var again = await scanner.ScanAsync(repo, new Dictionary<string, string>(), since, result, null, CancellationToken.None);
        Assert.Same(result, again);

        var engine = new ActivityEngine(result.Commits, new Dictionary<string, ActivityRepo> { [repo.Key] = repo }, config);
        var filter = new ActivityFilter { From = since };
        var rows = engine.Filter(filter);
        Assert.Equal(4, rows.Count);   // the bot is hidden
        var people = engine.Top(rows, ActivityDimension.Contributor, ActivityMeasure.Commits, filter, 10);
        Assert.Equal("Ana Dev", people[0].Name);
        Assert.Equal(3, people[0].Tally.Commits);
        var frameworks = engine.Top(rows, ActivityDimension.Framework, ActivityMeasure.LinesAdded, filter, 30).Select(t => t.Name).ToList();
        Assert.Contains("React", frameworks);
        Assert.Contains("Terraform", frameworks);

        filter.Branches = BranchScope.ExcludeMainline;
        Assert.Single(engine.Filter(filter));
        filter.Branches = BranchScope.All;
        filter.Ai = AiScope.WithoutAi;
        Assert.Equal(2, engine.Filter(filter).Count);
        filter.Ai = AiScope.All;
        filter.Only[ActivityDimension.Language] = ["TSX"];
        var tsx = engine.Filter(filter);
        Assert.Equal(2, tsx.Count);
        Assert.All(tsx, s => Assert.All(s.Parts, p => Assert.Equal(".tsx", p.Key)));

        var heat = engine.Pivot(engine.Filter(new ActivityFilter { From = since }), ActivityDimension.Contributor, ActivityDimension.Weekday, ActivityMeasure.Commits, filter);
        Assert.Equal(7, heat.Columns.Count);
        Assert.Equal(4, heat.Total);
    }

    [Fact]
    public async Task A_remote_repository_is_mirrored_shallowly_and_inactive_ones_are_not_fetched()
    {
        var dir = MakeRepo();
        var config = Config();
        var mirrors = Path.Combine(_root, "mirrors");
        var repo = new ActivityRepo { Key = "git:test", Kind = ActivitySource.Git, Name = "org/repo", CloneUrl = dir, DefaultBranch = "main" };
        var scanner = new ActivityScanner(config, mirrors);
        var since = DateTimeOffset.Now.AddDays(-61);
        var result = await scanner.ScanAsync(repo, new Dictionary<string, string>(), since, null, null, CancellationToken.None);
        Assert.Equal("ok", result.Status);
        Assert.Equal(5, result.Commits.Count);
        Assert.DoesNotContain(result.Commits, c => c.Subject == "ancient");
        // The mirror reaches one commit below the period, so every commit in it is counted in full.
        Assert.DoesNotContain(result.Commits, c => c.Boundary);
        Assert.Equal(4, result.Commits.Single(c => c.Subject == "Add handler").Added);
        Assert.Contains(result.Commits, c => c.Subject == "Add login page" && c.Branch == "feature/login");

        var quiet = Path.Combine(_root, "quiet");
        Directory.CreateDirectory(quiet);
        Git(quiet, "init", "-q", "-b", "main");
        Commit(quiet, "A", "a@x.com", DateTimeOffset.Now.AddDays(-300), "old", ("a.txt", "a"));
        var quietRepo = new ActivityRepo { Key = "git:quiet", Kind = ActivitySource.Git, Name = "quiet", CloneUrl = quiet };
        var q = await scanner.ScanAsync(quietRepo, new Dictionary<string, string>(), since, null, null, CancellationToken.None);
        Assert.Equal("inactive", q.Status);
        Assert.Empty(q.Commits);

        var pushedLongAgo = new ActivityRepo { Key = "git:p", Kind = ActivitySource.GitHub, Name = "p", CloneUrl = "https://invalid.example/x.git", PushedAt = DateTimeOffset.Now.AddDays(-100) };
        Assert.Equal("inactive", (await scanner.ScanAsync(pushedLongAgo, new Dictionary<string, string>(), since, null, null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Frameworks_are_those_of_the_repository_when_each_commit_was_made()
    {
        var dir = Path.Combine(_root, "switch");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        var now = DateTimeOffset.Now;
        Commit(dir, "A", "a@x.com", now.AddDays(-200), "angular app", ("web/package.json", """{ "dependencies": { "@angular/core": "17.0.0" } }"""), ("web/src/app.ts", "a\n"));
        Commit(dir, "A", "a@x.com", now.AddDays(-40), "angular work", ("web/src/app.ts", "a\nb\n"));
        Git(dir, "switch", "-q", "-c", "feature/old");
        Commit(dir, "B", "b@x.com", now.AddDays(-35), "angular on a branch", ("web/src/other.ts", "x\n"));
        Git(dir, "switch", "-q", "main");
        Commit(dir, "A", "a@x.com", now.AddDays(-20), "move to react", ("web/package.json", """{ "dependencies": { "react": "19.0.0" } }"""), ("web/src/App.tsx", "r\n"));
        Commit(dir, "A", "a@x.com", now.AddDays(-10), "react work", ("web/src/App.tsx", "r\ns\n"));
        Git(dir, "switch", "-q", "-c", "feature/vue");
        Commit(dir, "C", "c@x.com", now.AddDays(-6), "to vue", ("web/package.json", """{ "dependencies": { "vue": "3.0.0" } }"""));
        Git(dir, "switch", "-q", "main");
        Commit(dir, "A", "a@x.com", now.AddDays(-5), "react side work", ("web/src/side.ts", "s\n"));
        GitEnv(dir, new() { ["GIT_AUTHOR_NAME"] = "A", ["GIT_AUTHOR_EMAIL"] = "a@x.com", ["GIT_COMMITTER_NAME"] = "A", ["GIT_COMMITTER_EMAIL"] = "a@x.com" }, "merge", "-q", "--no-ff", "-m", "merge vue", "feature/vue");
        Commit(dir, "A", "a@x.com", now.AddDays(-1), "vue work", ("web/src/App.vue", "v\n"));

        var since = now.AddDays(-61);
        foreach (var repo in new[]
                 {
                     new ActivityRepo { Key = "local:" + dir, Kind = ActivitySource.Local, Name = "switch", LocalPath = dir },
                     new ActivityRepo { Key = "git:switch", Kind = ActivitySource.Git, Name = "switch", CloneUrl = dir, DefaultBranch = "main" },
                 })
        {
            var result = await new ActivityScanner(Config(), Path.Combine(_root, "mirrors")).ScanAsync(repo, new Dictionary<string, string>(), since, null, null, CancellationToken.None);
            string Fw(string subject) => string.Join('|', result.Commits.Single(c => c.Subject == subject).Parts.Select(p => p.Frameworks).Distinct());
            Assert.Equal("Angular", Fw("angular work"));
            Assert.Equal("Angular", Fw("angular on a branch"));   // the branch was made before the move
            Assert.Equal("React", Fw("move to react"));
            Assert.Equal("React", Fw("react work"));
            Assert.DoesNotContain(result.Commits, c => c.Boundary);
            Assert.Equal("React", Fw("react side work"));
            Assert.Equal("Vue", Fw("vue work"));   // the move came in through a merge
        }
    }

    [Fact]
    public async Task A_second_read_only_reads_the_new_commits()
    {
        var dir = MakeRepo();
        var since = DateTimeOffset.Now.AddDays(-61);
        foreach (var repo in new[]
                 {
                     new ActivityRepo { Key = "local:" + dir, Kind = ActivitySource.Local, Name = "inc", LocalPath = dir },
                     new ActivityRepo { Key = "git:inc", Kind = ActivitySource.Git, Name = "inc", CloneUrl = dir, DefaultBranch = "main" },
                 })
        {
            var scanner = new ActivityScanner(Config(), Path.Combine(_root, "mirrors-" + repo.Kind));
            var first = await scanner.ScanAsync(repo, new Dictionary<string, string>(), since, null, null, CancellationToken.None);
            var handler = first.Commits.Single(c => c.Subject == "Add handler");
            var count = first.Commits.Count;
            Commit(dir, "Ana Dev", "ana@x.com", DateTimeOffset.Now.AddHours(-1), "more work", ("api/More.cs", "class M {}\n"));
            var second = await scanner.ScanAsync(repo, new Dictionary<string, string>(), since, first, null, CancellationToken.None);
            Assert.Equal(count + 1, second.Commits.Count);
            Assert.Same(handler, second.Commits.Single(c => c.Subject == "Add handler"));   // kept, not read again
            Assert.Contains("MediatR", second.Commits.Single(c => c.Subject == "more work").Parts.Single().Frameworks.Split('|'));
            Git(dir, "reset", "-q", "--hard", "HEAD~1");
        }
    }

    [Fact]
    public async Task All_of_history_can_be_read_and_shown_with_open_ends()
    {
        var dir = MakeRepo();
        var scanner = new ActivityScanner(Config(), Path.Combine(_root, "mirrors"));
        var repo = new ActivityRepo { Key = "git:all", Kind = ActivitySource.Git, Name = "all", CloneUrl = dir, DefaultBranch = "main" };
        var recent = await scanner.ScanAsync(repo, new Dictionary<string, string>(), DateTimeOffset.Now.AddDays(-61), null, null, CancellationToken.None);
        Assert.DoesNotContain(recent.Commits, c => c.Subject == "ancient");
        var all = await scanner.ScanAsync(repo, new Dictionary<string, string>(), DateTimeOffset.MinValue, recent, null, CancellationToken.None);
        Assert.Contains(all.Commits, c => c.Subject == "ancient" && !c.Boundary);
        Assert.False(File.Exists(Path.Combine(scanner.MirrorPath(repo), "shallow")));

        var engine = new ActivityEngine(all.Commits, new Dictionary<string, ActivityRepo> { [repo.Key] = repo }, Config());
        var everything = new ActivityFilter { From = DateTimeOffset.MinValue, To = DateTimeOffset.MaxValue, HideBots = false };
        var rows = engine.Filter(everything);
        Assert.Equal(6, rows.Count);
        var months = engine.Pivot(rows, ActivityDimension.Repository, ActivityDimension.Month, ActivityMeasure.Commits, everything);
        Assert.InRange(months.Columns.Count, 13, 16);   // from the first commit's month to this one
        // Gone quiet: Old Timer's last commit is 400 days old; in a range ending 100 days ago, nobody committed in its last 200 days.
        var recentPeople = new ActivityFilter { From = DateTimeOffset.MinValue, To = DateTimeOffset.MaxValue, HideBots = false, PeopleActiveDays = 90 };
        Assert.DoesNotContain(engine.Filter(recentPeople), r => r.Commit.AuthorName == "Old Timer");
        Assert.Equal(5, engine.Filter(recentPeople).Count);
        var quietRepos = new ActivityFilter { From = DateTimeOffset.MinValue, To = DateTimeOffset.Now.AddDays(-100), HideBots = false, ReposActiveDays = 200 };
        Assert.Empty(engine.Filter(quietRepos));
        quietRepos.ReposActiveDays = 400;
        Assert.Single(engine.Filter(quietRepos));
        var before = new ActivityFilter { From = DateTimeOffset.MinValue, To = DateTimeOffset.Now.AddDays(-100), HideBots = false };
        Assert.Equal(["ancient"], engine.Filter(before).Select(r => r.Commit.Subject));
    }

    [Fact]
    public void Quiet_weekdays_and_hours_are_rows_with_zero_totals()
    {
        // One commit: six weekdays and 23 hours with nothing in them still have to be drawable rows.
        var repo = new ActivityRepo { Key = "r", Name = "r" };
        var commit = new ActivityCommit { Hash = "a", RepoKey = "r", PersonName = "Ana", PersonEmail = "a@x", AuthorName = "Ana", When = DateTimeOffset.Now.AddDays(-1), Branch = "main",
            Parts = [new ActivityPart { Key = ".cs", Files = 1, Added = 3 }] };
        var engine = new ActivityEngine([commit], new Dictionary<string, ActivityRepo> { ["r"] = repo }, Config());
        var f = new ActivityFilter { From = DateTimeOffset.Now.AddDays(-30) };
        var rows = engine.Filter(f);
        var map = engine.Pivot(rows, ActivityDimension.Weekday, ActivityDimension.Hour, ActivityMeasure.Commits, f);
        Assert.Equal(7, map.Rows.Count);
        Assert.All(map.Rows, r => Assert.True(map.RowTallies.ContainsKey(r)));
        Assert.Equal(1, map.Rows.Sum(r => map.RowTallies[r].Commits));
        Assert.Equal(24, engine.Top(rows, ActivityDimension.Hour, ActivityMeasure.Commits, f, 30).Count);
        var days = engine.Pivot(rows, ActivityDimension.Day, ActivityDimension.Repository, ActivityMeasure.Commits, f);
        Assert.InRange(days.Rows.Count, 30, 32);
        Assert.All(days.Rows, r => Assert.True(days.RowTallies.ContainsKey(r)));
    }

    [Theory]
    [InlineData("Ana", "ana@x.com", "Fix\n\nCo-authored-by: Cursor Agent <cursoragent@cursor.com>", "Cursor", false)]
    [InlineData("Ana (aider)", "ana@x.com", "aider: fix the thing\n\nCo-authored-by: aider (anthropic/claude-sonnet-4) <noreply@aider.chat>", "Aider", false)]
    [InlineData("devin-ai-integration[bot]", "158243242+devin-ai-integration[bot]@users.noreply.github.com", "Add x\n\nCo-Authored-By: Ana <ana@x.com>", "Devin", true)]
    [InlineData("Ana", "ana@x.com", "Upgrade the gpt-4 client library", null, false)]
    [InlineData("Ana", "ana@x.com", "Port\n\nAssisted-by: Gemini 2.5 Pro", "AI (other)", false)]
    public void AI_help_is_told_apart_from_the_person(string author, string email, string message, string? tool, bool agent)
    {
        var lines = message.Split('\n', 2);
        var f = new AiDetector().Detect(author, email, author, email, lines[0], lines.Length > 1 ? lines[1] : "");
        if (tool is null) Assert.Empty(f.Tools); else Assert.Contains(tool, f.Tools);
        Assert.Equal(agent, f.AgentAuthored);
        Assert.Equal("Ana", f.HumanName);
    }

    [Fact]
    public void Models_are_named_the_way_people_write_them()
    {
        Assert.Contains("Claude Opus 4.5", AiDetector.Models("Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"));
        Assert.Contains("Claude Sonnet 4", AiDetector.Models("aider (anthropic/claude-sonnet-4)"));
        Assert.Contains("GPT-5", AiDetector.Models("Co-authored-by: gpt-5 <x>"));
        Assert.Contains("Gemini 2.5 Pro", AiDetector.Models("Gemini 2.5 Pro"));
    }

    [Theory]
    [InlineData("src/App/Program.cs", "C#")]
    [InlineData("Module1.vb", "VB.NET")]
    [InlineData("Views/Home/Index.cshtml", "Razor")]
    [InlineData("MainPage.xaml", "XAML")]
    [InlineData("App.csproj", "MSBuild")]
    [InlineData("App.sln", "Visual Studio")]
    [InlineData("web/app/page.tsx", "TSX")]
    [InlineData("a.component.ts", "TypeScript")]
    [InlineData("Dockerfile", "Docker")]
    [InlineData("db/schema.sql", "SQL")]
    [InlineData("build.ps1", "PowerShell")]
    [InlineData("main.bicep", "Bicep")]
    [InlineData("data.parquet", "Parquet / Arrow")]
    [InlineData("notebook.ipynb", "Notebook")]
    public void File_types_have_languages(string path, string language) =>
        Assert.Equal(language, Languages.Lookup(Languages.KeyFor(path)).Language);

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/2026.11", true)]
    [InlineData("releases/v1/hotfix", true)]
    [InlineData("feature/release-notes", false)]
    [InlineData("Develop", true)]
    public void Mainline_branches_are_recognised_by_name(string branch, bool mainline) =>
        Assert.Equal(mainline, new ActivityScanner(new GitActivityConfig(), "/tmp/x").IsMainline(branch, null));

    [Fact]
    public void Renamed_files_count_under_their_new_name()
    {
        Assert.Equal("src/new/a.cs", ActivityScanner.RenamedTo("src/{old => new}/a.cs"));
        Assert.Equal("b.cs", ActivityScanner.RenamedTo("a.cs => b.cs"));
        Assert.Equal("src/a.cs", ActivityScanner.RenamedTo("src/{ => }/a.cs"));
    }

    [Fact]
    public void Identities_merge_by_alias_name_and_github_noreply_address()
    {
        var ids = new Identities(new GitActivityConfig { MergeByName = false, Aliases = { ["Ana"] = ["ana@home.com"] } });
        Assert.Equal("Ana", ids.Key("Ana D", "ana@home.com"));
        Assert.Equal("ana@github", ids.Key("x", "12345+Ana@users.noreply.github.com"));
    }
}
