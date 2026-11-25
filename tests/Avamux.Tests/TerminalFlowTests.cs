using Avamux.Core.Terminal;

namespace Avamux.Tests;

/// <summary>Flow control between a terminal's program and its view (what keeps Ctrl+C from waiting behind a backlog).</summary>
public class TerminalFlowTests
{
    private static Task<bool> WaitsAsync(ViewFlow flow, int ms = 300)
    {
        var t = Task.Run(flow.WaitForView);
        return Task.WhenAny(t, Task.Delay(ms)).ContinueWith(r => r.Result != t);
    }

    [Fact]
    public async Task A_view_that_keeps_up_is_never_waited_for()
    {
        var flow = new ViewFlow(1000, 100, TimeSpan.FromSeconds(10));
        flow.Queued(1000);
        Assert.False(await WaitsAsync(flow));
    }

    [Fact]
    public async Task A_view_behind_holds_the_reader_until_it_is_back_under_the_low_mark()
    {
        var flow = new ViewFlow(1000, 100, TimeSpan.FromSeconds(10));
        flow.Queued(5000);
        var wait = Task.Run(flow.WaitForView);
        await Task.Delay(150);
        Assert.False(wait.IsCompleted);
        flow.Acked(4000); // 1000 left: still over the low mark
        await Task.Delay(150);
        Assert.False(wait.IsCompleted);
        flow.Acked(950);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(50, flow.Outstanding);
    }

    [Fact]
    public async Task A_view_too_slow_to_catch_up_is_let_go_until_it_has_caught_up()
    {
        var flow = new ViewFlow(1000, 100, TimeSpan.FromMilliseconds(200));
        flow.Queued(5000);
        var started = DateTime.UtcNow;
        await Task.Run(flow.WaitForView).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(150));
        Assert.True(flow.Free);
        // Let go: more output does not wait, even while acknowledgements trickle in.
        flow.Queued(5000);
        flow.Acked(10);
        Assert.False(await WaitsAsync(flow));
        // Caught up: held back again.
        flow.Acked(flow.Outstanding);
        Assert.False(flow.Free);
        flow.Queued(5000);
        Assert.True(await WaitsAsync(flow, 100));
        flow.Close();
    }

    [Fact]
    public async Task Hiding_or_closing_the_view_releases_the_reader()
    {
        foreach (var release in new Action<ViewFlow>[] { f => f.SetHidden(true), f => f.Close() })
        {
            var flow = new ViewFlow(1000, 100, TimeSpan.FromSeconds(30));
            flow.Queued(5000);
            var wait = Task.Run(flow.WaitForView);
            await Task.Delay(100);
            Assert.False(wait.IsCompleted);
            release(flow);
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(await WaitsAsync(flow), "it must not wait again either");
        }
    }

    [Fact]
    public async Task A_program_writing_fast_is_held_at_the_pace_of_its_view()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var done = Path.Combine(Path.GetTempPath(), "avamux-flow-" + Guid.NewGuid().ToString("N"));
        using var pty = Pty.Spawn("sh", ["-c", $"head -c 4000000 /dev/zero | od -v; echo > {done}"], "/",
            new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" }, 80, 24);
        var flow = new ViewFlow(64 * 1024, 16 * 1024, TimeSpan.FromSeconds(30));
        var reader = new Thread(() =>
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = pty.Read(buf)) > 0) { flow.Queued(n); flow.WaitForView(); }
        }) { IsBackground = true };
        reader.Start();
        try
        {
            // The view acknowledges nothing: the program writes what fits in the pty and then has to wait.
            await Task.Delay(1500);
            Assert.False(File.Exists(done), "the program ran ahead of its view");
            Assert.InRange(flow.Outstanding, 64 * 1024, 64 * 1024 + 64 * 1024);
            // The view catches up as fast as output comes: the program finishes.
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(done) && DateTime.UtcNow < until) { flow.Acked(flow.Outstanding); await Task.Delay(1); }
            Assert.True(File.Exists(done), "the program did not finish once its view kept up");
        }
        finally
        {
            flow.Close();
            pty.Signal(9);
            File.Delete(done);
        }
    }
}
