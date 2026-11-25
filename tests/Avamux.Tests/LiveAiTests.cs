using System.Diagnostics;
using Avamux.Core;
using Avamux.Core.Ai;
using Avamux.Plugins;

namespace Avamux.Tests;

/// <summary>
/// Opt in with AVAMUX_AI_TESTS=1: these run the real Claude Code (on Haiku, low effort, so they cost little) and the
/// real pi. Machine-specific things come from the environment, never from tracked files:
/// <list type="bullet">
/// <item>AVAMUX_PI_TEST_START / AVAMUX_PI_TEST_STOP: scripts that start and stop a local model server for pi (the tests
/// start it only when nothing answers on the port, and stop only what they started).</item>
/// <item>AVAMUX_PI_TEST_PORT (default 8080), AVAMUX_PI_TEST_MODEL (default: pi's own default model).</item>
/// <item>AVAMUX_RESEARCH_WIKI: a wiki folder to copy for the research test (default: a small one the test writes).</item>
/// </list>
/// </summary>
public sealed class LiveAiFactAttribute : FactAttribute
{
    public const string Why = "Live AI test: opt in with AVAMUX_AI_TESTS=1";
    public static bool On => Environment.GetEnvironmentVariable("AVAMUX_AI_TESTS") is "1" or "true" or "yes";
    public LiveAiFactAttribute() { if (!On) Skip = Why; Timeout = 600_000; }
}

/// <summary>A local model server for pi, started for the tests when none is running, and stopped again by whoever started it.</summary>
public sealed class LocalModel : IAsyncLifetime
{
    private bool _started;
    public int Port => int.TryParse(Environment.GetEnvironmentVariable("AVAMUX_PI_TEST_PORT"), out var p) ? p : 8080;
    public string? Problem { get; private set; }

    private async Task<bool> UpAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try { return (await http.GetAsync($"http://127.0.0.1:{Port}/health")).IsSuccessStatusCode; } catch { return false; }
    }

    public async Task InitializeAsync()
    {
        if (!LiveAiFactAttribute.On) return;
        if (await UpAsync()) return; // someone else's: used, and left running
        var start = Environment.GetEnvironmentVariable("AVAMUX_PI_TEST_START");
        if (string.IsNullOrEmpty(start)) { Problem = $"Nothing answers on :{Port} and AVAMUX_PI_TEST_START is not set."; return; }
        using var p = Process.Start(new ProcessStartInfo(start) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        _started = true;
        for (var i = 0; i < 300 && !await UpAsync(); i++) await Task.Delay(1000);
        if (!await UpAsync()) Problem = $"{start} ran, but nothing answers on :{Port}: {await p.StandardError.ReadToEndAsync()}";
    }

    public async Task DisposeAsync()
    {
        if (!_started || Environment.GetEnvironmentVariable("AVAMUX_PI_TEST_STOP") is not { Length: > 0 } stop) return;
        using var p = Process.Start(new ProcessStartInfo(stop) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
    }
}

/// <summary>Editing a message in the middle of a chat: the agent's own session branches there, so it sees the new text only.</summary>
internal static class LiveEdits
{
    public static async Task EditedMessageReplacesTheOldOneAsync(string provider, string? model, string? effort)
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        var chats = app.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = provider, Model = model, Effort = effort, WorkingDirectory = box.Root });
        async Task<string> Say(string text)
        {
            await chats.SendAsync(chat.Id, text);
            var reply = ChatService.Thread(chat).Last();
            Assert.Null(reply.Error);
            return reply.Text;
        }
        await Say("Hello! Reply with just: OK");
        await Say("Remember this: my favourite fruit is apple. Reply with just: OK");
        Assert.Contains("apple", await Say("What is my favourite fruit? Reply with one word."), StringComparison.OrdinalIgnoreCase);

        var second = ChatService.Thread(chat)[2];
        await chats.EditAndResendAsync(chat.Id, second.Id, "Remember this: my favourite fruit is mango. Reply with just: OK");
        Assert.Null(ChatService.Thread(chat).Last().Error);
        var answer = await Say("What is my favourite fruit? Reply with one word.");
        Assert.Contains("mango", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apple", answer, StringComparison.OrdinalIgnoreCase);
        // The branch kept what came before the edit, and the old version is still there to switch back to.
        var thread = ChatService.Thread(chat);
        Assert.Equal(6, thread.Count);
        Assert.Equal("Hello! Reply with just: OK", thread[0].Text);
        Assert.Equal(2, ChatService.Versions(chat, thread[2]).Count);
    }
}

[Trait("Category", "AiLive")]
public class LiveClaudeTests
{
    private static async Task<AppServices> App(Sandbox box)
    {
        var app = new AppServices(box.Paths);
        await Task.CompletedTask;
        return app;
    }

    [LiveAiFact]
    public async Task Claude_haiku_answers_a_prompt_without_tools()
    {
        using var box = new Sandbox();
        await using var app = await App(box);
        var r = await app.Ai.RunAsync(new AiRunRequest { Prompt = "Reply with exactly the word: pong", ProviderId = "claude-code", Model = "haiku", Effort = "low" });
        Assert.True(r.Ok, r.Error);
        Assert.Contains("pong", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(r.ProviderSessionId);
    }

    [LiveAiFact]
    public Task Claude_sees_an_edited_message_and_not_the_old_one() => LiveEdits.EditedMessageReplacesTheOldOneAsync("claude-code", "haiku", "low");

    [LiveAiFact]
    public async Task Claude_asks_its_question_and_its_permission_through_the_chat()
    {
        using var box = new Sandbox();
        await using var app = await App(box);
        var chats = app.Ai.Chats;
        var chat = chats.Create(new AiChatRequest { ProviderId = "claude-code", Model = "haiku", Effort = "low", WorkingDirectory = box.Root });
        var send = chats.SendAsync(chat.Id, "First use the AskUserQuestion tool to ask me which colour I prefer (red or blue). Then create a file named colour.txt containing my answer, using the Write tool.");
        var asked = false;
        var allowed = false;
        var until = DateTime.UtcNow.AddMinutes(4);
        while (!send.IsCompleted && DateTime.UtcNow < until)
        {
            foreach (var r in chats.Requests(chat.Id))
            {
                if (r.Request is AiQuestionRequest q)
                {
                    asked = true;
                    await chats.AnswerAsync(chat.Id, r.Id, new AiAnswer { Answers = q.Questions.ToDictionary(x => x.Question, _ => "blue") });
                }
                else if (r.Request is AiPermissionRequest)
                {
                    allowed = true;
                    await chats.AnswerAsync(chat.Id, r.Id, new AiAnswer { Decision = AiDecision.Allow });
                }
            }
            await Task.Delay(200);
        }
        await send;
        Assert.True(asked, "No question came.");
        Assert.True(allowed, "No permission prompt came.");
        Assert.Equal("blue", File.ReadAllText(Path.Combine(box.Root, "colour.txt")).Trim().ToLowerInvariant());
    }
}

[Trait("Category", "AiLive")]
public class LivePiTests(LocalModel model) : IClassFixture<LocalModel>
{
    [LiveAiFact]
    public async Task Pi_answers_with_the_local_model()
    {
        Assert.True(model.Problem is null, model.Problem);
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        var chat = app.Ai.Chats.Create(new AiChatRequest { ProviderId = "pi", Model = Environment.GetEnvironmentVariable("AVAMUX_PI_TEST_MODEL"), WorkingDirectory = box.Root });
        await app.Ai.Chats.SendAsync(chat.Id, "Reply with exactly the word: pong");
        var reply = ChatService.Thread(chat).Last();
        Assert.Null(reply.Error);
        Assert.Contains("pong", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(reply.ProviderSession);
        Assert.True(app.Ai.Chats.Get(chat.Id).ContextTokens > 0);
    }

    [LiveAiFact]
    public async Task Pi_sees_an_edited_message_and_not_the_old_one()
    {
        Assert.True(model.Problem is null, model.Problem);
        await LiveEdits.EditedMessageReplacesTheOldOneAsync("pi", Environment.GetEnvironmentVariable("AVAMUX_PI_TEST_MODEL"), null);
    }
}
