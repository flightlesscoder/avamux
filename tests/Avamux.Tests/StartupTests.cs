using System.Text.Json.Nodes;
using Avamux.TestSupport;
using Xunit.Abstractions;

namespace Avamux.Tests;

/// <summary>A startup test: runs when load tests are opted in, there is a display, and the app is built.</summary>
public sealed class StartupFactAttribute : FactAttribute
{
    public StartupFactAttribute()
    {
        if (!Load.On) Skip = Load.Why;
        else if (!AppRun.HasDisplay) Skip = "Startup tests start the real app and need a display (scripts/load-tests.sh provides a virtual one).";
        else if (AppRun.Binary is null) Skip = "Build src/Avamux first (scripts/load-tests.sh does).";
    }
}

/// <summary>
/// Startup and shutdown of the real app, started as a process over a sandbox: how long until it answers commands,
/// shows its first frame, runs a first terminal command, loads plugins, renders a wiki page and an edit to it, and
/// how long it takes to quit (and whether it leaves anything running). Each scenario runs a few times
/// (AVAMUX_STARTUP_RUNS, default 3); the median is checked against its budget, and the slowest is reported too, the
/// first run being the coldest. The app's own timeline (`avamux app timeline`) breaks each start down further.
/// </summary>
[Trait("Category", "Startup")]
public class StartupTests(ITestOutputHelper output)
{
    private static int Runs => int.TryParse(Environment.GetEnvironmentVariable("AVAMUX_STARTUP_RUNS"), out var n) && n > 0 ? n : 3;

    /// <summary>Records the median and slowest of several runs of one measurement, and checks the median.</summary>
    private void Check(string what, IReadOnlyList<double> runs, double? budgetMs, string? note = null, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        var sorted = runs.OrderBy(x => x).ToList();
        var median = sorted[sorted.Count / 2];
        var sample = new Load.Sample(median, 0, 0, 0);
        var detail = $"{(runs.Count > 1 ? $"median of {runs.Count}, slowest {sorted[^1]:0}, first {runs[0]:0}" : "one run")}{(note is null ? "" : "; " + note)}";
        var (ok, line) = Load.Report(test, what, sample, budgetMs, detail);
        output.WriteLine(line);
        Assert.True(ok, $"{what}: median {median:0} ms, over the budget of {budgetMs} ms");
    }

    private void Note(string what, double value, string unit, double? limit = null, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        var (ok, line) = Load.Report(test, $"{what} ({unit})", new Load.Sample(value, 0, 0, 0), limit);
        output.WriteLine(line);
        Assert.True(ok, $"{what}: {value:0} {unit}, over the limit of {limit}");
    }

    /// <summary>Where each run's milestones fell, by name, for the report.</summary>
    private static void Collect(Dictionary<string, List<double>> into, IEnumerable<(string Name, double Ms, string? Detail)> marks, params string[] names)
    {
        foreach (var name in names)
            if (marks.FirstOrDefault(m => m.Name == name) is { Name: not null } m)
                (into.TryGetValue(name, out var list) ? list : into[name] = []).Add(m.Ms);
    }

    // ---- startup ----

    [StartupFact]
    public void Startup_to_the_first_terminal_command()
    {
        var answering = new List<double>();
        var command = new List<double>();
        var memory = new List<double>();
        var marks = new Dictionary<string, List<double>>();
        for (var run = 0; run < Runs; run++)
        {
            using var app = new AppRun();
            app.Start();
            answering.Add(app.WaitUntilAnswering());
            // The start page is the terminals: wait for its first tab, then run something in it and wait for the answer.
            var id = "";
            app.WaitFor("a terminal tab", () => (id = (app.Send("terminal list") as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>() ?? "") != "");
            app.WaitForMark("terminal view ready");
            app.Send("terminal send", new JsonObject { ["id"] = id, ["text"] = "echo $((6*7))-ready", ["enter"] = true });
            command.Add(app.WaitFor("the command's output", () => (app.Send("terminal read", new JsonObject { ["id"] = id, ["raw"] = true })?.ToString() ?? "").Contains("42-ready")));
            Thread.Sleep(500);
            memory.Add(app.ResidentMb());
            Collect(marks, app.Timeline(), "main", "ui toolkit ready", "services created", "services started", "window created", "commands ready",
                "terminal started", "first terminal output", "window opened", "first frame", "terminal view ready");
        }
        foreach (var (name, values) in marks) Check($"timeline: {name}", values, budgetMs: null, note: "ms since the process started");
        Check("answers commands", answering, budgetMs: 2000);
        Check("first frame", marks["first frame"], budgetMs: 2000);
        Check("terminal ready to type into", marks["terminal view ready"], budgetMs: 2500);
        Check("first terminal command run and its output back", command, budgetMs: 3000, note: "from launch: echo typed into the first tab, answer read back");
        Note("memory after startup", memory.OrderBy(x => x).ElementAt(memory.Count / 2), "MB resident", limit: 800);
    }

    [StartupFact]
    public void Startup_with_a_watch_plugin_and_opening_a_page_plugin()
    {
        var root = AppRun.RepoRoot;
        var watchDll = Directory.GetFiles(Path.Combine(root, "samples", "watch-plugin", "bin"), "SampleWatchPlugin.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        var pageDll = Directory.GetFiles(Path.Combine(root, "samples", "page-plugin", "bin"), "SamplePagePlugin.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Assert.True(watchDll is not null && pageDll is not null, "Build the samples first (dotnet build avamux.slnx).");

        var without = new List<double>();
        var with = new List<double>();
        var watchLoaded = new List<double>();
        var pageOpen = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            // The same start without any plugin, to compare with.
            using (var plain = new AppRun())
            {
                plain.Start();
                plain.WaitUntilAnswering();
                without.Add(plain.WaitForMark("services created"));
                plain.Quit();
            }

            using var app = new AppRun();
            File.Copy(watchDll!, Path.Combine(app.Paths.PluginsDir, Path.GetFileName(watchDll!)));
            app.Start();
            app.WaitUntilAnswering();
            with.Add(app.WaitForMark("services created"));
            watchLoaded.Add(app.WaitForMark("watch plugins loaded", detail: "1"));
            Assert.Contains("http-status", app.Send("watch kinds")!.ToJsonString());

            // A page plugin: added, then opened; the DLL is loaded when the page is first shown.
            app.Send("page add", new JsonObject { ["name"] = "Sample", ["kind"] = "plugin", ["assembly"] = pageDll });
            var before = app.Timeline().Max(m => m.Ms);
            var asked = app.SinceLaunch.Elapsed.TotalMilliseconds;
            app.Send("page open", new JsonObject { ["id"] = "Sample" });
            var loaded = app.WaitForMark("plugin page loaded", after: before, detail: "Sample");
            pageOpen.Add(app.SinceLaunch.Elapsed.TotalMilliseconds - asked);
            Assert.True(loaded > before);
            app.Quit();
        }
        Check("services ready, no plugins", without, budgetMs: null, note: "ms since the process started");
        Check("services ready, with the sample watch plugin", with, budgetMs: without.Order().ElementAt(without.Count / 2) + 300, note: "the plugin folder is loaded while services start");
        Check("watch plugin loaded", watchLoaded, budgetMs: 1000, note: "ms since the process started");
        Check("open the sample page plugin until it is loaded", pageOpen, budgetMs: 750, note: "the DLL is loaded on first open");
    }

    [StartupFact]
    public void Startup_to_the_first_wiki_page_and_an_edit_rendered()
    {
        var opened = new List<double>();
        var preview = new List<double>();
        var rendered = new List<double>();
        var edit = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            using var app = new AppRun();
            app.Settings(new JsonObject { ["ui"] = new JsonObject { ["startPage"] = "wiki" } });
            File.WriteAllText(Path.Combine(app.Paths.WikiDir, "Home.md"), "# Home\n\n" + Load.Markdown(20_000));
            app.Start();
            app.WaitUntilAnswering();
            opened.Add(app.WaitForMark("wiki page opened"));
            preview.Add(app.WaitForMark("wiki preview ready"));
            var first = app.WaitForMark("wiki rendered");
            rendered.Add(first);
            // An edit from outside (as an agent or the CLI makes one): the page reloads and the preview renders it.
            var asked = app.SinceLaunch.Elapsed.TotalMilliseconds;
            var mark = app.Timeline().Max(m => m.Ms);
            app.Send("wiki write", new JsonObject { ["path"] = "Home.md", ["content"] = "# Home\n\nEdited.\n" });
            app.WaitForMark("wiki rendered", after: mark + 0.1);
            edit.Add(app.SinceLaunch.Elapsed.TotalMilliseconds - asked);
            app.Quit();
        }
        Check("wiki page open in the editor", opened, budgetMs: 2000, note: "ms since the process started; the wiki is the start page");
        Check("wiki preview ready", preview, budgetMs: 2500, note: "ms since the process started");
        Check("first wiki page rendered", rendered, budgetMs: 3000, note: "ms since the process started");
        Check("an edit rendered in the preview", edit, budgetMs: 500, note: "`wiki write` until the preview shows it");
    }

    [StartupFact]
    public void Startup_over_a_big_wiki()
    {
        var count = Load.N(20_000);
        var rendered = new List<double>();
        var answering = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            using var app = new AppRun();
            app.Settings(new JsonObject { ["ui"] = new JsonObject { ["startPage"] = "wiki" } });
            var folder = Path.Combine(app.Paths.WikiDir, "Attachments");
            Directory.CreateDirectory(folder);
            for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(folder, $"file {i:00000}.png"), []);
            for (var i = 0; i < 300; i++) File.WriteAllText(Path.Combine(app.Paths.WikiDir, $"Note {i}.md"), "# Note\n");
            app.Start();
            answering.Add(app.WaitUntilAnswering());
            rendered.Add(app.WaitForMark("wiki rendered"));
            app.Quit();
        }
        Check($"answers commands, wiki of {count} files", answering, budgetMs: 3000);
        Check($"first wiki page rendered, wiki of {count} files", rendered, budgetMs: 4000, note: "the folder tree lists them as the page opens");
    }

    // ---- terminal output ----

    /// <summary>
    /// A program pouring out output (like `find /` or `yes`) must not run ahead of the view: when it does, megabytes
    /// queue up, and after Ctrl+C the screen keeps scrolling for seconds although the program has already stopped.
    /// Measured with the CRT effect on, the heaviest view to draw.
    /// </summary>
    [StartupFact]
    public void A_flood_of_output_stops_on_screen_soon_after_ctrl_c()
    {
        var behind = new List<double>();
        var stopped = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            using var app = new AppRun();
            app.Settings(new JsonObject { ["terminal"] = new JsonObject { ["crt"] = true, ["cursorPulse"] = true } });
            app.Start();
            app.WaitUntilAnswering();
            var id = "";
            app.WaitFor("a terminal tab", () => (id = (app.Send("terminal list") as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>() ?? "") != "");
            app.WaitForMark("terminal view ready");
            // The program ignores Ctrl+C itself and says when the one producing the output (which does not) has ended.
            app.Send("terminal send", new JsonObject { ["id"] = id, ["enter"] = true, ["text"] =
                "python3 -c \"import signal,subprocess; signal.signal(signal.SIGINT, signal.SIG_IGN); " +
                "p=subprocess.Popen(['yes','avamux flood test line, long enough to fill a good part of the row'], preexec_fn=lambda: signal.signal(signal.SIGINT, signal.SIG_DFL)); " +
                "p.wait(); print('flood' + '-over')\"" });
            Thread.Sleep(2000);
            behind.Add((app.Send("terminal stats", new JsonObject { ["id"] = id })?["behind"]?.GetValue<double>() ?? -1) / 1024);
            var sent = app.SinceLaunch.Elapsed.TotalMilliseconds;
            app.Send("terminal send", new JsonObject { ["id"] = id, ["text"] = "^C", ["keys"] = true });
            var shown = app.WaitFor("the end of the flood on screen",
                () => (app.Send("terminal read", new JsonObject { ["id"] = id, ["lines"] = 3 })?.ToString() ?? "").Contains("flood-over"), timeoutMs: 60_000);
            stopped.Add(shown - sent);
        }
        Note("output waiting to be shown, 2 s into the flood", behind.Max(), "KB", limit: 2048);
        Check("Ctrl+C until the flood stops on screen", stopped, budgetMs: 1500, note: "CRT effect on; the view acknowledges what it has shown and the program waits for it");
    }

    // ---- the command line and a second start ----

    [StartupFact]
    public void The_command_line_and_a_second_start()
    {
        using var app = new AppRun();
        app.Start();
        app.WaitUntilAnswering();
        var cli = new List<double>();
        var second = new List<double>();
        for (var i = 0; i < Math.Max(5, Runs); i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var p = app.Start("app", "ping");
            Assert.True(p.WaitForExit(15_000));
            cli.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(0, p.ExitCode);
        }
        for (var i = 0; i < Runs; i++)
        {
            // Starting the app again while it runs: it brings the running one forward and leaves.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var p = app.Start("--gui");
            Assert.True(p.WaitForExit(15_000));
            second.Add(watch.Elapsed.TotalMilliseconds);
        }
        Check("`avamux app ping` from the command line", cli, budgetMs: 1000, note: "a whole CLI process: .NET start, connect, answer");
        Check("a second start hands over to the running app", second, budgetMs: 1000);
        Assert.False(app.Process!.HasExited);
    }

    // ---- shutdown ----

    [StartupFact]
    public void Shutdown_when_idle()
    {
        var quit = new List<double>();
        var services = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            using var app = new AppRun();
            app.Start();
            app.WaitUntilAnswering();
            app.WaitForMark("terminal view ready");
            quit.Add(app.Quit());
            var saved = app.SavedTimeline();
            var started = saved.First(m => m.Name == "shutdown started").Ms;
            services.Add(saved.First(m => m.Name == "services closed").Ms - started);
            Assert.DoesNotContain(saved, m => m.Name.Contains("still closing"));
        }
        Check("closing the app's services", services, budgetMs: 500, note: "from the timeline the app writes as it exits");
        Check("`app quit` until the process has ended", quit, budgetMs: 1000);
    }

    [StartupFact]
    public void Shutdown_with_busy_terminals_and_pages_open_leaves_nothing_running()
    {
        var quit = new List<double>();
        for (var run = 0; run < Runs; run++)
        {
            using var app = new AppRun();
            File.WriteAllText(Path.Combine(app.Paths.WikiDir, "Home.md"), "# Home\n\n" + Load.Markdown(50_000));
            app.Start();
            app.WaitUntilAnswering();
            app.WaitForMark("terminal view ready");
            for (var i = 0; i < 8; i++) app.Send("terminal new", new JsonObject { ["background"] = true, ["command"] = new JsonArray("sleep", "600") });
            app.Send("terminal new", new JsonObject { ["background"] = true, ["command"] = new JsonArray("sh", "-c", "yes > /dev/null") });
            app.Send("app nav", new JsonObject { ["page"] = "wiki" });
            app.WaitForMark("wiki rendered");
            var pids = (app.Send("terminal list") as JsonArray)!.Select(t => t!["pid"]!.GetValue<int>()).ToList();
            Assert.Equal(10, pids.Count);
            quit.Add(app.Quit());
            Thread.Sleep(300);
            var left = pids.Where(pid => OperatingSystem.IsLinux() && Directory.Exists($"/proc/{pid}") && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ")).ToList();
            Assert.True(left.Count == 0, $"Terminal programs still running after the app quit: {string.Join(", ", left)}");
            Assert.DoesNotContain(app.SavedTimeline(), m => m.Name.Contains("still closing"));
        }
        Check("`app quit` with 10 busy terminals and the wiki open", quit, budgetMs: 1500);
    }

    [StartupFact]
    public void A_restart_keeps_what_was_saved()
    {
        using var first = new AppRun();
        first.Start();
        first.WaitUntilAnswering();
        first.Send("wiki write", new JsonObject { ["path"] = "Kept.md", ["content"] = "# Kept\n" });
        first.Send("settings set", new JsonObject { ["key"] = "wiki.editorPictureHeight", ["value"] = "123" });
        var quit = first.Quit();
        using var again = new AppRun(first.Root);
        again.Start();
        var answering = again.WaitUntilAnswering();
        Assert.Contains("Kept.md", again.Send("wiki list")!.ToJsonString());
        Assert.Contains("123", File.ReadAllText(again.Home("avamux.json")));
        Check("quit", [quit], budgetMs: 1000);
        Check("start again over the same data", [answering], budgetMs: 2500);
        again.Quit();
    }
}
