using System.Diagnostics;
using Avamux.Core;
using Avamux.Core.Ai;
using Avamux.Plugins;
using Avamux.Plugins.AiActions;

namespace Avamux.UiTests;

/// <summary>
/// The phase 1 acceptance test for Do Research…: asked "When was avamux browser script injection supposed to happen
/// for cross-origin iframes?", the agent finds "pre-navigation" in the 2026-11-19 daily note. Runs only with
/// AVAMUX_AI_TESTS=1, on a copy of the wiki (AVAMUX_RESEARCH_WIKI, else the real one; it is only ever copied).
/// pi needs a model server: AVAMUX_PI_RESEARCH_START / AVAMUX_PI_RESEARCH_STOP start and stop one (only when nothing
/// answers on AVAMUX_PI_TEST_PORT, default 8080).
/// </summary>
[Trait("Category", "AiLive")]
public class ResearchAcceptanceTests
{
    private const string Question = "When was avamux browser script injection supposed to happen for cross-origin iframes?";
    private static bool On => Environment.GetEnvironmentVariable("AVAMUX_AI_TESTS") is "1" or "true" or "yes";

    private static string CopyWiki(string root)
    {
        var source = Environment.GetEnvironmentVariable("AVAMUX_RESEARCH_WIKI") is { Length: > 0 } s ? s
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "avamux", "wiki");
        var target = Path.Combine(root, "data", "wiki");
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Where(d => !d.Contains("/.git")))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.Contains("/.git/")))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
        return target;
    }

    private static async Task<string> AskAsync(string provider, string? model, string? effort)
    {
        var root = Path.Combine(Path.GetTempPath(), "avamux-research-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "home"));
        try
        {
            var wiki = CopyWiki(root);
            await using var app = new AppServices(new AppPaths(Path.Combine(root, "data"), Path.Combine(root, "home"), root));
            Assert.Equal(wiki, app.Wiki.Root);
            var chats = app.Ai.Chats;
            var chat = chats.Create(new AiChatRequest { Title = "Research", ProviderId = provider, Model = model, Effort = effort, WorkingDirectory = wiki, SystemPrompt = Prompts.Research(wiki) });
            var send = chats.SendAsync(chat.Id, Question);
            var until = DateTime.UtcNow.AddMinutes(10);
            while (!send.IsCompleted && DateTime.UtcNow < until)
            {
                // As the user would: let it read and search (it asks before running commands), and leave questions unanswered.
                foreach (var r in chats.Requests(chat.Id))
                    await chats.AnswerAsync(chat.Id, r.Id, r.Request is AiPermissionRequest ? new AiAnswer { Decision = AiDecision.Allow } : new AiAnswer { Decision = AiDecision.Deny, Reason = "Use your judgement." });
                await Task.Delay(200);
            }
            await send;
            var reply = ChatService.Thread(chat).Last();
            Assert.Null(reply.Error);
            return reply.Text;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Claude_haiku_finds_pre_navigation_in_the_daily_note()
    {
        if (!On) return;
        var answer = await AskAsync("claude-code", "haiku", "low");
        Assert.Contains("pre-navigation", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-11-19", answer);
    }

    [Fact]
    public async Task Pi_with_a_local_model_finds_pre_navigation_in_the_daily_note()
    {
        if (!On) return;
        var port = int.TryParse(Environment.GetEnvironmentVariable("AVAMUX_PI_TEST_PORT"), out var p) ? p : 8080;
        async Task<bool> Up() { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }; try { return (await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode; } catch { return false; } }
        var started = false;
        if (!await Up())
        {
            var start = Environment.GetEnvironmentVariable("AVAMUX_PI_RESEARCH_START");
            Assert.False(string.IsNullOrEmpty(start), $"Nothing answers on :{port} and AVAMUX_PI_RESEARCH_START is not set.");
            using var proc = Process.Start(new ProcessStartInfo(start!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(6));
            started = true;
            for (var i = 0; i < 360 && !await Up(); i++) await Task.Delay(1000);
            Assert.True(await Up(), "The model server did not come up.");
        }
        try
        {
            var answer = await AskAsync("pi", Environment.GetEnvironmentVariable("AVAMUX_PI_TEST_MODEL"), null);
            Assert.Contains("pre-navigation", answer, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026-11-19", answer);
        }
        finally
        {
            if (started && Environment.GetEnvironmentVariable("AVAMUX_PI_RESEARCH_STOP") is { Length: > 0 } stop)
            {
                using var proc = Process.Start(new ProcessStartInfo(stop) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
        }
    }
}
