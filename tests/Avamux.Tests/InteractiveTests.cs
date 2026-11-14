using Avamux.Core.Interactive;

namespace Avamux.Tests;

public class InteractiveTests
{
    private static (ReplSession Session, List<(ReplOutputKind Kind, string Text)> Out) New()
    {
        var session = new ReplSession();
        var output = new List<(ReplOutputKind, string)>();
        session.Output += (k, t) => { lock (output) output.Add((k, t)); };
        return (session, output);
    }

    [Fact]
    public async Task Expressions_show_their_value_and_statements_do_not()
    {
        var (repl, output) = New();
        Assert.True((await repl.RunAsync("var x = 20;")).Ok);
        Assert.Empty(output);
        var r = await repl.RunAsync("x + 22");
        Assert.Equal((true, 42), (r.HasValue, r.Value));
        Assert.Equal((ReplOutputKind.Result, "42"), output.Single());

        output.Clear();
        await repl.RunAsync("new [] { \"a\", \"b\", \"c\" }.ToList().Select(x => x)");
        Assert.Equal("IEnumerable<string> { \"a\", \"b\", \"c\" }", output.Single().Text);
        output.Clear();
        await repl.RunAsync("new [] { \"a\", \"b\", \"c\" }.ToList()");
        Assert.Equal("List<string>(3) { \"a\", \"b\", \"c\" }", output.Single().Text);

        // A call that returns nothing shows nothing, with or without the semicolon.
        output.Clear();
        Assert.False((await repl.RunAsync("Print(\"hi\")")).HasValue);
        Assert.Equal((ReplOutputKind.Output, "hi"), output.Single());
    }

    [Fact]
    public async Task Declarations_carry_over_and_errors_leave_the_session_usable()
    {
        var (repl, output) = New();
        await repl.RunAsync("using System.Globalization;\nint Twice(int n) => n * 2;\nrecord Point(int X, int Y);");
        Assert.Equal("Point { X = 2, Y = 4 }", ReplFormatter.Format((await repl.RunAsync("new Point(Twice(1), Twice(2))")).Value));

        output.Clear();
        var bad = await repl.RunAsync("nothing + 1");
        Assert.False(bad.Ok);
        Assert.Equal(ReplOutputKind.Error, output.Single().Kind);
        Assert.Contains("CS0103", output.Single().Text);

        output.Clear();
        var thrown = await repl.RunAsync("var before = 1; int.Parse(\"x\")");
        Assert.False(thrown.Ok);
        Assert.StartsWith("FormatException:", output.Single().Text);
        Assert.Equal(3, (await repl.RunAsync("before + Twice(1)")).Value); // what ran before the exception is kept
        Assert.Contains(repl.Variables(), v => v.StartsWith("int before = 1"));

        repl.Reset();
        Assert.False((await repl.RunAsync("before")).Ok);
    }

    [Fact]
    public async Task Async_code_can_be_awaited_or_left_running()
    {
        var (repl, output) = New();
        var awaited = await repl.RunAsync("await Task.Delay(20); var n = await Task.Run(() => 7); n * 6");
        Assert.Equal(42, awaited.Value);

        output.Clear();
        var left = await repl.RunAsync("Task.Delay(50).ContinueWith(_ => \"done\")");
        Assert.True(left.Value is Task<string>);
        for (var i = 0; i < 100 && output.Count < 2; i++) await Task.Delay(20);
        Assert.StartsWith("Task<string> (", output[0].Text);
        Assert.Equal("Task<string> (RanToCompletion) = \"done\"", output[1].Text);

        // Stop gives the prompt back and cancels the token the code was given.
        var slow = repl.RunAsync("await Task.Delay(30000, Cancel); 1");
        for (var i = 0; i < 200 && !repl.Busy; i++) await Task.Delay(10);
        await Task.Delay(300);
        repl.Stop();
        Assert.True((await slow.WaitAsync(TimeSpan.FromSeconds(10))).Stopped);
        Assert.Equal(2, (await repl.RunAsync("1 + 1")).Value);
    }

    [Theory]
    [InlineData("1 + 1", true)]
    [InlineData("var x = 1;", true)]
    [InlineData("var x = 1", false)]
    [InlineData("if (true) {", false)]
    [InlineData("void F()\n{\n}", true)]
    [InlineData("new[] { 1, 2,", false)]
    [InlineData("", true)]
    public void Knows_when_more_input_is_needed(string code, bool complete) => Assert.Equal(complete, ReplSession.IsComplete(code));

    [Fact]
    public void Values_are_written_like_a_repl_writes_them()
    {
        Assert.Equal("null", ReplFormatter.Format(null));
        Assert.Equal("\"a\\\"b\\n\"", ReplFormatter.Format("a\"b\n"));
        Assert.Equal("1.5", ReplFormatter.Format(1.5));
        Assert.Equal("int[3] { 1, 2, 3 }", ReplFormatter.Format(new[] { 1, 2, 3 }));
        Assert.Equal("Dictionary<string, int>(2) { [\"a\"] = 1, [\"b\"] = 2 }", ReplFormatter.Format(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }));
        Assert.Equal("(1, \"two\")", ReplFormatter.Format((1, "two")));
        Assert.Equal("{ Name = \"x\", Size = 3 }", ReplFormatter.Format(new { Name = "x", Size = 3 }));
        Assert.Equal("IEnumerable<int> { 0, 1, 2 }", ReplFormatter.Format(Enumerable.Range(0, 3).Where(_ => true)));
        Assert.EndsWith("…\n}", ReplFormatter.Format(Enumerable.Range(0, 500)));
        Assert.Equal("anonymous[1] { { Name = \"a\", Tags = string[1] { \"x\" } } }", ReplFormatter.Format(new[] { new { Name = "a", Tags = new[] { "x" } } }));
        Assert.Contains("\n  ", ReplFormatter.Format(Enumerable.Range(0, 60).Select(i => "item " + i).ToList())); // long lists go one per line
    }

    [Fact]
    public async Task The_avamux_api_runs_commands()
    {
        using var box = new Sandbox();
        await using var services = new Avamux.Core.AppServices(box.Paths);
        var repl = new ReplSession(new ReplGlobals(new AvamuxApi(services)));
        var errors = new List<string>();
        repl.Output += (k, t) => { if (k == ReplOutputKind.Error) errors.Add(t); };
        var r = await repl.RunAsync("(await avamux.Command(\"settings get\", new { key = \"terminal.scrollback\" }))?.ToString()");
        Assert.True(r.Ok, string.Join("\n", errors));
        Assert.Contains("10000", r.Value as string);
        Assert.Contains("terminal new", (IEnumerable<string>)(await repl.RunAsync("avamux.Commands()")).Value!);
        Assert.Equal(0, (await repl.RunAsync("avamux.Terminals.Sessions.Count")).Value);
    }
}

public class WikiGitTests
{
    [Fact]
    public async Task The_wiki_is_a_repository_everything_git_works_on()
    {
        using var box = new Sandbox();
        await using var services = new Avamux.Core.AppServices(box.Paths);
        var (wiki, repos) = (services.Wiki, services.Repos);
        var repo = wiki.Repo;

        // Known by id and by name, without being in the user's list.
        Assert.Empty(repos.Items);
        Assert.Equal(wiki.Root, repos.Get("wiki").Path);
        Assert.Equal(wiki.Root, repos.Get(Avamux.Core.Wiki.WikiService.RepoId).Path);

        Assert.False(await repos.IsRepositoryAsync(repo));
        await repos.InitAsync(repo);
        Assert.True(await repos.IsRepositoryAsync(repo));
        foreach (var (key, value) in new[] { ("user.email", "t@example.com"), ("user.name", "T"), ("commit.gpgsign", "false") })
            Assert.Equal(0, (await Avamux.Core.Shell.RunAsync("git", ["config", key, value], wiki.Root)).ExitCode);

        async Task Commit(string text, string message)
        {
            wiki.Write("Home.md", text);
            await repos.StageAllAsync(repo);
            await repos.CommitAsync(repo, message);
        }
        await Commit("one", "first");
        await Commit("two", "second");
        wiki.Write("Other.md", "other");
        await repos.StageAllAsync(repo);
        await repos.CommitAsync(repo, "unrelated");

        // The history of one page leaves out commits that did not touch it.
        var log = await repos.FileLogAsync(repo, "Home.md");
        Assert.Equal(["second", "first"], log.Select(c => c.Subject));
        Assert.Equal("one", await repos.FileAtAsync(repo, "Home.md", log[1].Hash));
        Assert.Null(await repos.FileAtAsync(repo, "Other.md", log[1].Hash));

        // Restoring a version changes the page and leaves that as a change to commit.
        var changes = 0;
        repos.Changed += () => changes++;
        await repos.RestoreFileAsync(repo, "Home.md", log[1].Hash);
        Assert.Equal("one", wiki.Read("Home.md"));
        Assert.True(changes > 0);
        Assert.Equal("Home.md", (await repos.StatusAsync(repo)).Changes.Single().Path);

        // Branches: a page written on one is not there on the other.
        await repos.CommitAsync(repo, "back to one");
        var trunk = (await repos.StatusAsync(repo)).Branch!;
        await repos.CreateBranchAsync(repo, "draft");
        await Commit("draft text", "on the draft");
        await repos.SwitchAsync(repo, trunk);
        Assert.Equal("one", wiki.Read("Home.md"));
        await repos.SwitchAsync(repo, "draft");
        Assert.Equal("draft text", wiki.Read("Home.md"));
    }
}
