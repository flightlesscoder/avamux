using System.Text.Json.Nodes;
using Avamux.Core;
using Avamux.Core.Ai;

namespace Avamux.Tests;

/// <summary>Kokoro speech with a stand-in kokoro_onnx (tests/Shared/fake-kokoro) and a player that plays nothing.</summary>
[Collection("speech")] // the stand-in's log and the player come from the environment
public class SpeechTests
{
    private static readonly string KokoroLog = Path.Combine(Path.GetTempPath(), $"avamux-fake-kokoro-{Environment.ProcessId}.log");

    static SpeechTests()
    {
        Environment.SetEnvironmentVariable("FAKE_KOKORO_LOG", KokoroLog);
        Environment.SetEnvironmentVariable("AVAMUX_SPEECH_PLAYER", "true");
    }

    private static (Sandbox Box, AppServices App) Make(bool enabled = true, bool configured = true)
    {
        File.WriteAllText(KokoroLog, "");
        var box = new Sandbox();
        var app = new AppServices(box.Paths);
        if (configured)
        {
            var model = Path.Combine(box.Root, "kokoro.onnx");
            var voices = Path.Combine(box.Root, "voices.bin");
            File.WriteAllText(model, "");
            File.WriteAllText(voices, "");
            app.Config.Set("ai.tts.python", JsonValue.Create(AiTests.Fake("fake-python.sh")));
            app.Config.Set("ai.tts.model", JsonValue.Create(model));
            app.Config.Set("ai.tts.voices", JsonValue.Create(voices));
        }
        if (enabled) app.Config.Set("ai.tts.enabled", JsonValue.Create(true));
        return (box, app);
    }

    private static List<string> Said() => File.ReadAllLines(KokoroLog).Where(l => l.StartsWith("say ")).ToList();

    private static async Task WaitFor(Func<bool> condition, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(25); }
    }

    private static bool Alive(int pid) { try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; } catch { return false; } }

    [Fact]
    public async Task It_is_off_by_default_and_starts_nothing()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        Assert.False(app.Speech.Enabled);
        Assert.Equal(SpeechState.Off, app.Speech.State);
        Assert.Contains("switched off", await app.Speech.SpeakAsync("Hello."));
        Assert.Null(app.Speech.HelperPid);
        Assert.Equal(3, app.Speech.Problems().Count); // nothing set up either
    }

    [Fact]
    public async Task Missing_files_are_named()
    {
        var (box, app) = Make(configured: false);
        using var _ = box;
        await using var __ = app;
        app.Config.Set("ai.tts.python", JsonValue.Create("/no/such/python"));
        var problems = app.Speech.Problems();
        Assert.Contains(problems, p => p.Contains("/no/such/python"));
        Assert.False(app.Speech.Configured);
        Assert.NotNull(await app.Speech.SpeakAsync("Hello."));
        Assert.Null(app.Speech.HelperPid);
    }

    [Fact]
    public async Task Speaks_sentence_by_sentence_leaving_out_code_and_what_follows_a_rule()
    {
        var (box, app) = Make();
        using var _ = box;
        await using var __ = app;
        var played = 0;
        app.Speech.Played += _ => Interlocked.Increment(ref played);
        var error = await app.Speech.SpeakAsync("Hello there. A **bold** [link](https://x.y) here!\n\n```\ncode();\n```\nLast one\n\n---\nNot spoken.", tag: "m1");
        Assert.Null(error);
        try { await WaitFor(() => played == 3 && app.Speech.State == SpeechState.Ready); }
        catch (TimeoutException) { Assert.Fail($"played {played}, state {app.Speech.State}, said [{string.Join(" | ", Said())}], error {app.Speech.LastError}, log [{File.ReadAllText(KokoroLog)}]"); }
        Assert.Equal(["say af_nova 1.05 Hello there.", "say af_nova 1.05 A bold link here!", "say af_nova 1.05 Last one"], Said());
        Assert.Contains("af_heart", app.Speech.Voices);
        Assert.DoesNotContain("jf_alpha", app.Speech.Voices); // English voices only
        Assert.NotNull(app.Speech.HelperPid);
    }

    [Fact]
    public async Task A_reply_is_spoken_as_it_streams_in()
    {
        var (box, app) = Make();
        using var _ = box;
        await using var __ = app;
        Assert.Null(await app.Speech.StartAsync());
        var spoken = app.Speech.Stream("reply");
        spoken.Update("The first sentence is");
        await Task.Delay(300);
        Assert.Empty(Said());
        spoken.Update("The first sentence is done. And the sec");
        await WaitFor(() => Said().Count == 1);
        spoken.Update("The first sentence is done. And the second.");
        spoken.Finish();
        await WaitFor(() => Said().Count == 2);
        Assert.Equal("say af_nova 1.05 And the second.", Said()[1]);
    }

    [Fact]
    public async Task Switching_it_off_ends_the_helper()
    {
        var (box, app) = Make();
        using var _ = box;
        await using var __ = app;
        Assert.Null(await app.Speech.StartAsync());
        var pid = app.Speech.HelperPid!.Value;
        Assert.True(Alive(pid));
        app.Config.Set("ai.tts.enabled", JsonValue.Create(false));
        await WaitFor(() => !Alive(pid));
        Assert.Equal(SpeechState.Off, app.Speech.State);
        Assert.Null(app.Speech.HelperPid);
    }

    [Fact]
    public async Task Quitting_ends_the_helper()
    {
        var (box, app) = Make();
        using var _ = box;
        Assert.Null(await app.Speech.StartAsync());
        var pid = app.Speech.HelperPid!.Value;
        await app.DisposeAsync();
        await WaitFor(() => !Alive(pid), 5000);
    }

    [Fact]
    public void Markdown_is_made_speakable()
    {
        Assert.Equal("Title\nitem one\ncode inline\n", SpeechService.Speakable("# Title\n- item **one**\n`code` inline\n```\nskipped\n```"));
    }
}

/// <summary>Opt in with AVAMUX_TTS_TESTS=1 and AVAMUX_TTS_PYTHON / AVAMUX_TTS_MODEL / AVAMUX_TTS_VOICES: the real Kokoro, played to nothing.</summary>
[Collection("speech")]
public class LiveSpeechTests
{
    [Fact]
    public async Task Real_kokoro_makes_speech()
    {
        if (Environment.GetEnvironmentVariable("AVAMUX_TTS_TESTS") is not ("1" or "true")) return;
        Environment.SetEnvironmentVariable("AVAMUX_SPEECH_PLAYER", "true");
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        foreach (var (key, env) in new[] { ("ai.tts.python", "AVAMUX_TTS_PYTHON"), ("ai.tts.model", "AVAMUX_TTS_MODEL"), ("ai.tts.voices", "AVAMUX_TTS_VOICES") })
            app.Config.Set(key, JsonValue.Create(Environment.GetEnvironmentVariable(env)));
        app.Config.Set("ai.tts.enabled", JsonValue.Create(true));
        var played = new TaskCompletionSource<string>();
        app.Speech.Played += f => played.TrySetResult(f);
        Assert.Null(await app.Speech.SpeakAsync("Hello from avamux."));
        var file = await played.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.EndsWith(".wav", file);
        Assert.Contains("af_nova", app.Speech.Voices);
    }
}
