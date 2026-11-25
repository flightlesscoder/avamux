using System.Text.Json.Nodes;
using Avamux.Core;
using Avamux.Core.Ai;
using Avamux.Plugins;

namespace Avamux.Tests;

/// <summary>The AI providers against stand-ins that speak Claude Code's and pi's protocols (tests/Shared/fake-*.py).</summary>
public class AiTests
{
    public static string Fake(string name) => Path.Combine(AppRun.RepoRoot, "tests", "Shared", name);

    /// <summary>A sandboxed app with a fake Claude Code and a fake pi, logging their arguments to <c>Log</c>.</summary>
    public sealed class Box : IAsyncDisposable
    {
        public Sandbox Sandbox { get; } = new();
        public AppServices App { get; }
        public string Log { get; }

        public Box()
        {
            App = new AppServices(Sandbox.Paths);
            Log = Path.Combine(Sandbox.Root, "agent.log");
            App.Ai.Put(new AiProviderConfig { Id = "claude", Kind = ClaudeCodeProvider.KindId, Name = "Fake Claude", Command = Fake("fake-claude.py"), Env = { ["FAKE_AGENT_LOG"] = Log } });
            App.Ai.Put(new AiProviderConfig { Id = "pi-fake", Kind = PiProvider.KindId, Name = "Fake pi", Command = Fake("fake-pi.py"), Env = { ["FAKE_AGENT_LOG"] = Log } });
        }

        public List<JsonNode> Calls() => File.Exists(Log) ? File.ReadAllLines(Log).Select(l => JsonNode.Parse(l)!).ToList() : [];
        public List<string> Args(int i = -1) { var calls = Calls().Where(c => c is JsonArray).ToList(); return calls[i < 0 ? calls.Count + i : i]!.AsArray().Select(a => a!.GetValue<string>()).ToList(); }

        public async ValueTask DisposeAsync() { await App.DisposeAsync(); Sandbox.Dispose(); }
    }

    private static async Task<List<AiEvent>> Send(IAiSession session, string text, Func<AiEvent, Task>? on = null)
    {
        var events = new List<AiEvent>();
        void Handler(AiEvent e) { lock (events) events.Add(e); if (on is not null) _ = on(e); }
        session.Event += Handler;
        try { await session.SendAsync(new AiUserMessage { Text = text }).WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { session.Event -= Handler; }
        lock (events) return [.. events];
    }

    private static string Text(IEnumerable<AiEvent> events) => string.Concat(events.OfType<AiTextDelta>().Select(d => d.Text)).Trim();

    [Fact]
    public async Task Claude_streams_text_and_thinking_and_reports_context_and_its_session()
    {
        await using var b = new Box();
        await using var s = b.App.Ai.Start("claude", "haiku", "low", b.Sandbox.Root, systemPrompt: "Be brief.");
        var events = await Send(s, "hello there");
        Assert.Equal("Echo: hello there", Text(events));
        Assert.Contains(events, e => e is AiThinkingStarted);
        Assert.Equal("Let me think.", string.Concat(events.OfType<AiThinkingDelta>().Select(d => d.Text)));
        var usage = events.OfType<AiUsage>().Last();
        Assert.Equal(1520, usage.ContextTokens);
        Assert.Equal(200000, usage.ContextWindow);
        var finished = events.OfType<AiTurnFinished>().Single();
        Assert.True(finished.Ok);
        Assert.StartsWith("assistant-", finished.ProviderMessageId);
        Assert.Equal("", events.OfType<AiTurnStarted>().Single().ForkToken); // the first message: nothing to branch from
        Assert.NotNull(s.ProviderSessionId);
        var args = b.Args();
        Assert.Contains("--permission-prompt-tool", args);
        Assert.Equal("haiku", args[args.IndexOf("--model") + 1]);
        Assert.Equal("low", args[args.IndexOf("--effort") + 1]);
        Assert.Equal(s.ProviderSessionId, args[args.IndexOf("--session-id") + 1]);
        Assert.Equal("Be brief.", args[args.IndexOf("--append-system-prompt") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args); // nothing skipped unless set

        // The same process carries on: a second message, and the token to branch from is the last reply.
        var second = await Send(s, "again");
        Assert.Equal(finished.ProviderMessageId, second.OfType<AiTurnStarted>().Single().ForkToken);
        Assert.Single(b.Calls().Where(c => c is JsonArray));
    }

    [Fact]
    public async Task Claude_permission_prompts_are_answered_from_avamux()
    {
        await using var b = new Box();
        await using var s = b.App.Ai.Start("claude", null, null, b.Sandbox.Root);
        var events = await Send(s, "please write", e => e is AiPermissionRequest p ? s.AnswerAsync(p.Id, new AiAnswer { Decision = AiDecision.Allow }) : Task.CompletedTask);
        var request = events.OfType<AiPermissionRequest>().Single();
        Assert.Equal("Write", request.Tool);
        Assert.Equal("Write /tmp/x.txt", request.Summary);
        Assert.Contains(events, e => e is AiRequestClosed c && c.Id == request.Id);
        Assert.Equal("Write /tmp/x.txt", events.OfType<AiToolStarted>().Single().Summary);
        Assert.Equal("File written", events.OfType<AiToolFinished>().Single().Output);
        Assert.Equal("written", Text(events));

        var denied = await Send(s, "write again", e => e is AiPermissionRequest p ? s.AnswerAsync(p.Id, new AiAnswer { Decision = AiDecision.Deny, Reason = "not now" }) : Task.CompletedTask);
        Assert.Equal("denied: not now", Text(denied));
    }

    [Fact]
    public async Task Claude_allowed_for_the_session_is_not_asked_again()
    {
        await using var b = new Box();
        await using var s = b.App.Ai.Start("claude", null, null, b.Sandbox.Root);
        await Send(s, "write", e => e is AiPermissionRequest p ? s.AnswerAsync(p.Id, new AiAnswer { Decision = AiDecision.AllowForSession }) : Task.CompletedTask);
        var again = await Send(s, "write more");
        Assert.DoesNotContain(again, e => e is AiPermissionRequest);
        Assert.Equal("written", Text(again));
    }

    [Fact]
    public async Task Claude_questions_get_their_answers()
    {
        await using var b = new Box();
        await using var s = b.App.Ai.Start("claude", null, null, b.Sandbox.Root);
        var events = await Send(s, "ask me", e => e is AiQuestionRequest q
            ? s.AnswerAsync(q.Id, new AiAnswer { Answers = { [q.Questions[0].Question] = q.Questions[0].Options[1].Label } }) : Task.CompletedTask);
        var question = events.OfType<AiQuestionRequest>().Single().Questions.Single();
        Assert.Equal("Which colour?", question.Question);
        Assert.Equal(["Red", "Blue"], question.Options.Select(o => o.Label));
        Assert.Equal("You chose Blue", Text(events));
    }

    [Fact]
    public async Task Claude_stops_when_asked_and_reports_errors()
    {
        await using var b = new Box();
        await using var s = b.App.Ai.Start("claude", null, null, b.Sandbox.Root);
        var started = new TaskCompletionSource();
        var events = new List<AiEvent>();
        s.Event += e => { lock (events) events.Add(e); if (e is AiTextDelta) started.TrySetResult(); };
        var send = s.SendAsync(new AiUserMessage { Text = "slow please" });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await s.AbortAsync();
        await send.WaitAsync(TimeSpan.FromSeconds(20));
        lock (events)
        {
            Assert.Equal("stopped", events.OfType<AiTurnFinished>().Last().StopReason);
            Assert.DoesNotContain(events, e => e is AiError);
        }

        var failed = await Send(s, "fail now");
        Assert.Equal("Something broke", failed.OfType<AiError>().Single().Message);
        Assert.False(failed.OfType<AiTurnFinished>().Single().Ok);
    }

    [Fact]
    public async Task Claude_full_access_tools_off_and_restrictions_become_arguments()
    {
        await using var b = new Box();
        var c = b.App.Ai.GetConfig("claude");
        c.FullAccess = true;
        c.AllowedTools = ["Read", "Grep"];
        c.ExtraFolders = ["/tmp"];
        b.App.Ai.Put(c);
        await using (var s = b.App.Ai.Start("claude", null, null, b.Sandbox.Root, noTools: true, ephemeral: true)) await Send(s, "hi");
        var args = b.Args();
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("Read,Grep", args[args.IndexOf("--allowedTools") + 1]);
        Assert.Equal("/tmp", args[args.IndexOf("--add-dir") + 1]);
    }

    [Fact]
    public async Task Claude_branches_by_resuming_at_a_message()
    {
        await using var b = new Box();
        await using var s = b.App.Ai.Start("claude", null, null, b.Sandbox.Root, resume: "orig-session", fork: true, forkAt: "assistant-abc");
        await Send(s, "branch");
        var args = b.Args();
        Assert.Equal("orig-session", args[args.IndexOf("--resume") + 1]);
        Assert.Contains("--fork-session", args);
        Assert.Equal("assistant-abc", args[args.IndexOf("--resume-session-at") + 1]);
        Assert.NotEqual("orig-session", s.ProviderSessionId); // a new conversation id
    }

    [Fact]
    public async Task Pi_streams_reports_tools_and_context_and_asks_through_its_extension()
    {
        await using var b = new Box();
        await using var s = b.App.Ai.Start("pi-fake", "Local/small", "high", b.Sandbox.Root, stateDirectory: b.Sandbox.Root);
        var events = await Send(s, "hello");
        Assert.Equal("Echo: hello", Text(events));
        Assert.Equal("Thinking it over.", string.Concat(events.OfType<AiThinkingDelta>().Select(d => d.Text)));
        Assert.Equal("read Home.md", events.OfType<AiToolStarted>().Single().Summary);
        Assert.Equal("# Home", events.OfType<AiToolFinished>().Single().Output);
        var usage = events.OfType<AiUsage>().Single();
        Assert.Equal(4200, usage.ContextTokens);
        Assert.Equal(92160, usage.ContextWindow);
        Assert.Equal("#0", events.OfType<AiTurnStarted>().Single().ForkToken);
        var args = b.Args();
        Assert.Equal("rpc", args[args.IndexOf("--mode") + 1]);
        Assert.Equal("Local/small", args[args.IndexOf("--model") + 1]);
        Assert.Equal("high", args[args.IndexOf("--thinking") + 1]);
        Assert.Equal(b.App.Ai.PiExtensionPath, args[args.IndexOf("-e") + 1]);
        Assert.True(File.Exists(b.App.Ai.PiExtensionPath));
        Assert.EndsWith(".jsonl", s.ProviderSessionId);

        var asked = await Send(s, "ask me", e => e is AiQuestionRequest q ? s.AnswerAsync(q.Id, new AiAnswer { Answers = { ["Which colour?"] = "Red" } }) : Task.CompletedTask);
        Assert.Equal("Which colour?", asked.OfType<AiQuestionRequest>().Single().Questions[0].Question);
        Assert.Equal("You chose Red", Text(asked));
        Assert.Equal("#1", asked.OfType<AiTurnStarted>().Single().ForkToken);

        var perm = await Send(s, "perm", e => e is AiPermissionRequest p ? s.AnswerAsync(p.Id, new AiAnswer { Decision = AiDecision.Deny }) : Task.CompletedTask);
        Assert.Equal("bash ls", perm.OfType<AiPermissionRequest>().Single().Summary);
        Assert.Equal("permission: Deny", Text(perm));

        await s.SetEffortAsync("low");
        await s.SetModelAsync("Local/small");
        Assert.Contains(b.Calls(), c => c is JsonObject o && o.ContainsKey("set_thinking_level"));
        Assert.Contains(b.Calls(), c => c is JsonObject o && o.ContainsKey("set_model"));
    }

    [Fact]
    public async Task Pi_asks_before_tools_only_when_set_to_and_forks_a_copy()
    {
        await using var b = new Box();
        var c = b.App.Ai.GetConfig("pi-fake");
        c.Options["confirmTools"] = "bash,write";
        b.App.Ai.Put(c);
        await using (var s = b.App.Ai.Start("pi-fake", null, null, b.Sandbox.Root, resume: "/tmp/orig.jsonl", fork: true, forkAt: "#1", stateDirectory: b.Sandbox.Root))
            await Send(s, "hello");
        Assert.Contains(b.Calls(), x => x is JsonObject o && o["confirm"]?.GetValue<string>() == "bash,write");
        var args = b.Args();
        Assert.Equal("/tmp/orig.jsonl", args[args.IndexOf("--fork") + 1]);
    }

    [Fact]
    public async Task A_one_off_run_uses_the_actions_default_without_tools()
    {
        await using var b = new Box();
        b.App.Config.Set("ai.actionsProvider", JsonValue.Create("claude"));
        var result = await b.App.Ai.RunAsync(new AiRunRequest { Prompt = "summarise this" });
        Assert.True(result.Ok, result.Error);
        Assert.Equal("Echo: summarise this", result.Text);
        Assert.Equal("claude", result.ProviderId);
        var args = b.Args();
        Assert.Equal("haiku", args[args.IndexOf("--model") + 1]); // ai.actionsModel
        Assert.Equal("low", args[args.IndexOf("--effort") + 1]);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.NotNull(result.ProviderSessionId);
    }

    [Fact]
    public async Task A_missing_program_is_a_clear_error()
    {
        await using var b = new Box();
        b.App.Ai.Put(new AiProviderConfig { Id = "nothing", Kind = ClaudeCodeProvider.KindId, Name = "Nothing", Command = "/no/such/claude" });
        var result = await b.App.Ai.RunAsync(new AiRunRequest { Prompt = "x", ProviderId = "nothing" });
        Assert.False(result.Ok);
        Assert.Contains("/no/such/claude", result.Error);
    }

    [Fact]
    public void Command_templates_expand_into_arguments_and_drop_empty_groups()
    {
        var v = new Dictionary<string, string> { ["prompt"] = "say \"hi\" & rm -rf", ["model"] = "" };
        Assert.Equal(["exec", "--json", "say \"hi\" & rm -rf"], CommandProvider.Expand("exec --json [--model {model}] {prompt}", v));
        v["model"] = "o4";
        Assert.Equal(["exec", "--json", "--model", "o4", "say \"hi\" & rm -rf"], CommandProvider.Expand("exec --json [--model {model}] {prompt}", v));
        Assert.Equal("hello", CommandProvider.TextOf((JsonObject)JsonNode.Parse("""{"type":"item.completed","item":{"type":"agent_message","text":"hello"}}""")!));
        Assert.Equal("hi", CommandProvider.TextOf((JsonObject)JsonNode.Parse("""{"type":"message","role":"assistant","content":"hi","delta":true}""")!));
        Assert.Null(CommandProvider.TextOf((JsonObject)JsonNode.Parse("""{"type":"user","text":"mine"}""")!));
    }

    [Fact]
    public async Task Providers_are_kept_in_a_file_with_two_defaults()
    {
        using var box = new Sandbox();
        await using (var app = new AppServices(box.Paths))
        {
            Assert.Equal(["claude-code", "pi"], app.Ai.Configs.Select(c => c.Id));
            app.Ai.Put(new AiProviderConfig { Kind = CommandProvider.KindId, Name = "My Codex", Command = "codex", Options = { ["template"] = "exec {prompt}" } });
        }
        await using (var app = new AppServices(box.Paths))
        {
            var mine = app.Ai.GetConfig("My Codex");
            Assert.Equal("my-codex", mine.Id);
            Assert.Equal("exec {prompt}", mine.Option("template"));
            Assert.Throws<ArgumentException>(() => app.Ai.Put(new AiProviderConfig { Kind = "nope", Name = "x" }));
        }
    }
}
