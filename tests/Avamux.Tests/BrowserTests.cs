using System.Text;
using System.Text.Json.Nodes;
using Avamux.Core.Browser;
using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Terminal;

namespace Avamux.Tests;

/// <summary>Stands in for the UI's panes: a list of them, and a script the "page" answers with.</summary>
internal sealed class FakeSurfaces : IBrowserSurfaces
{
    public List<BrowserSurfaceInfo> Items { get; } = [];
    public List<string> Scripts { get; } = [];
    /// <summary>What the page answers for a script; null makes it hang (as a page stopped in an alert does).</summary>
    public Func<string, string?> Page { get; set; } = _ => "{\"ok\":true,\"v\":null}";
    public List<string> Navigations { get; } = [];

    public static BrowserSurfaceInfo Pane(string id, string? terminal = "1", bool active = true, string placement = "dock", BrowserDialogInfo? dialog = null) =>
        new(id, "http://localhost:3000/", "App", false, terminal, placement, active, true, false, null, 1, 1, dialog, false, false, null);

    public IReadOnlyList<BrowserSurfaceInfo> List() => Items.ToList();
    public Task<BrowserSurfaceInfo> OpenAsync(BrowserOpenOptions options)
    {
        var info = Pane("b" + (Items.Count + 1), options.Terminal) with { Url = options.Url ?? "about:blank" };
        Items.Add(info);
        return Task.FromResult(info);
    }
    public Task CloseAsync(string id) { Items.RemoveAll(s => s.Id == id); return Task.CompletedTask; }
    public Task ShowAsync(string id, bool focus) => Task.CompletedTask;
    public Task MoveAsync(string id, string placement, string? terminal) => Task.CompletedTask;
    public Task<string?> EvalAsync(string id, string script)
    {
        Scripts.Add(script);
        var answer = Page(script);
        return answer is null ? new TaskCompletionSource<string?>().Task : Task.FromResult<string?>(answer);
    }
    public Task NavigateAsync(string id, string url) { Navigations.Add(url); return Task.CompletedTask; }
    public Task GoAsync(string id, string action) => Task.CompletedTask;
    public Task<bool> WaitForLoadAsync(string id, int loads, TimeSpan timeout) => Task.FromResult(true);
    public Task ScreenshotAsync(string id, string file, bool fullPage) => File.WriteAllBytesAsync(file, [137, 80, 78, 71]);
    public Task PdfAsync(string id, string file) => File.WriteAllTextAsync(file, "%PDF");
    public Task<string> DevToolsAsync(string id, string action) => Task.FromResult("open");
    public Task SetKeepAliveAsync(string id, bool on) => Task.CompletedTask;
    public Task<double> ZoomAsync(string id, string action) => Task.FromResult(1.0);
    public Task<string?> SetViewportAsync(string id, int? width, int? height) => Task.FromResult<string?>($"{width}x{height}");
    public Task<bool> DesignModeAsync(string id, bool? on) => Task.FromResult(on ?? false);
    public Task<bool> AnswerDialogAsync(string id, bool accept, string? text) => Task.FromResult(true);
    public Dictionary<string, IReadOnlyList<BrowserInitScript>> InitScripts { get; } = [];
    public Task SetInitScriptsAsync(string id, IReadOnlyList<BrowserInitScript> scripts) { InitScripts[id] = scripts; return Task.CompletedTask; }
}

public class BrowserTests
{
    private static (Sandbox Box, ConfigService Config, BrowserService Service, FakeSurfaces Panes) Make()
    {
        var box = new Sandbox();
        var config = new ConfigService(box.Paths);
        var panes = new FakeSurfaces();
        return (box, config, new BrowserService(box.Paths, config) { Surfaces = panes }, panes);
    }

    private static Args A(params (string Key, object Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = JsonValue.Create(v);
        return new Args(o);
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("127.0.0.1:8080/x", "http://127.0.0.1:8080/x")]
    [InlineData("app.localhost", "http://app.localhost")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("/tmp/page.html", "file:///tmp/page.html")]
    [InlineData("how do refs work", "https://duckduckgo.com/?q=how%20do%20refs%20work")]
    [InlineData("readme", "https://duckduckgo.com/?q=readme")]
    public void Addresses_are_completed_or_searched_for(string typed, string expected) =>
        Assert.Equal(expected, BrowserService.NormalizeUrl(typed, "https://duckduckgo.com/?q={query}"));

    [Fact]
    public void Javascript_addresses_are_refused()
    {
        var ex = Assert.Throws<BrowserException>(() => BrowserService.NormalizeUrl("javascript:alert(1)", ""));
        Assert.Equal("invalid_params", ex.Code);
    }

    [Theory]
    [InlineData("b3", "b3"), InlineData("3", "b3"), InlineData("browser:3", "b3"), InlineData("surface:3", "b3"), InlineData("B12", "b12")]
    public void Pane_ids_are_accepted_in_several_spellings(string given, string id) => Assert.Equal(id, BrowserService.NormalizeId(given));

    [Fact]
    public void Commands_act_on_the_pane_beside_the_calling_tab_and_never_guess()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            Assert.Equal("not_found", Assert.Throws<BrowserException>(() => service.Resolve(A())).Code);
            panes.Items.Add(FakeSurfaces.Pane("b1", "1"));
            Assert.Equal("b1", service.Resolve(A()).Id); // the only one there is
            panes.Items.Add(FakeSurfaces.Pane("b2", "2"));
            panes.Items.Add(FakeSurfaces.Pane("b3", "2", active: false));
            Assert.Equal("b2", service.Resolve(A(("callerTerminal", "2"))).Id); // the active one beside tab 2
            Assert.Equal("b3", service.Resolve(A(("callerTerminal", "2"), ("surface", "3"))).Id); // --surface wins
            Assert.Equal("invalid_params", Assert.Throws<BrowserException>(() => service.Resolve(A(("callerTerminal", "9")))).Code);
            Assert.Equal("not_found", Assert.Throws<BrowserException>(() => service.Resolve(A(("surface", "b7")))).Code);
        }
    }

    [Fact]
    public void Terminal_links_follow_the_setting_the_modifier_and_the_external_sites()
    {
        var (box, config, service, _) = Make();
        using (box) using (config)
        {
            Assert.True(service.OpensExternally("https://example.com", modifier: false)); // a plain click keeps going to the system browser
            Assert.False(service.OpensExternally("https://example.com", modifier: true)); // Ctrl+click: the pane
            config.Settings.Browser.TerminalLinksInPane = true;
            Assert.False(service.OpensExternally("https://example.com", false));
            Assert.True(service.OpensExternally("https://example.com", true));
            config.Settings.Browser.ExternalSites = "*.google.com, github.com";
            Assert.True(service.OpensExternally("https://docs.google.com/x", false));
            Assert.True(service.OpensExternally("https://google.com/", false));
            Assert.True(service.OpensExternally("https://github.com/a/b", false));
            Assert.False(service.OpensExternally("https://gitlab.com/", false));
        }
    }

    [Fact]
    public async Task Page_answers_become_values_and_page_errors_keep_their_code()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            panes.Items.Add(FakeSurfaces.Pane("b1"));
            panes.Page = _ => "{\"ok\":true,\"v\":{\"clicked\":\"button \\\"Save\\\" [ref=e4]\"}}";
            Assert.Equal("clicked button \"Save\" [ref=e4]", (await service.ClickAsync(A(("target", "e4"))))!.GetValue<string>());
            Assert.Contains("window.__avx.click(\"e4\"", panes.Scripts[^1]);
            Assert.Contains(BrowserService.AgentVersion, panes.Scripts[^1]); // the page gets the agent script if it lacks it

            panes.Page = _ => "{\"ok\":false,\"code\":\"not_found\",\"error\":\"Ref e9 is unknown or stale\"}";
            var ex = await Assert.ThrowsAsync<BrowserException>(() => service.ClickAsync(A(("target", "e9"))));
            Assert.Equal("not_found", ex.Code);
            Assert.StartsWith("not_found: Ref e9", ex.Message);
        }
    }

    [Fact]
    public async Task A_promise_is_collected_once_it_settles()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            panes.Items.Add(FakeSurfaces.Pane("b1"));
            var polls = 0;
            panes.Page = script => script.Contains("collect(7)")
                ? ++polls < 3 ? "{\"ok\":true,\"v\":{\"done\":false}}" : "{\"ok\":true,\"v\":{\"done\":true,\"ok\":true,\"v\":42}}"
                : "{\"ok\":true,\"pending\":7}";
            Assert.Equal(42, (await service.EvalAsync(A(("script", "fetch('/x').then(r => r.status)"))))!.GetValue<int>());
            Assert.Equal(3, polls);
        }
    }

    [Fact]
    public async Task An_action_that_opens_a_dialog_returns_at_once_and_says_how_to_answer()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            panes.Items.Add(FakeSurfaces.Pane("b1"));
            panes.Page = _ =>
            {
                // The page stops in confirm() and never answers the script.
                panes.Items[0] = panes.Items[0] with { Dialog = new BrowserDialogInfo("confirm", "Really delete?", null) };
                return null;
            };
            var started = DateTime.UtcNow;
            var r = (await service.ClickAsync(A(("target", "e3"))))!.GetValue<string>();
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            Assert.Contains("opened a confirm: \"Really delete?\"", r);
            Assert.Contains("dialog accept", r);

            // With the dialog still open, other commands say so instead of hanging.
            var ex = await Assert.ThrowsAsync<BrowserException>(() => service.GetAsync(A(("what", "text"), ("target", "h1"))));
            Assert.Equal("invalid_state", ex.Code);
            Assert.Contains("dialog accept", ex.Message);
        }
    }

    [Fact]
    public async Task Console_errors_and_requests_are_kept_per_pane_and_can_be_filtered_and_cleared()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            panes.Items.Add(FakeSurfaces.Pane("b1"));
            service.Record("b1", JsonNode.Parse("""
                {"k":"log","url":"http://localhost:3000/","items":[
                  ["console",{"level":"info","text":"ready","ts":1700000000000}],
                  ["console",{"level":"error","text":"boom","ts":1700000000100}],
                  ["errors",{"kind":"error","text":"TypeError: x is undefined","ts":1700000000200}],
                  ["net",{"type":"fetch","method":"GET","url":"http://localhost:3000/api","status":500,"ts":1700000000300}],
                  ["net",{"type":"fetch","method":"GET","url":"http://localhost:3000/ok","status":200,"ts":1700000000400}]]}
                """)!.AsObject());
            var errorsOnly = (JsonArray)(await service.LogAsync(A(("level", "error")), "console"))!;
            Assert.Single(errorsOnly);
            Assert.Equal("boom", errorsOnly[0]!["text"]!.GetValue<string>());
            Assert.Equal("http://localhost:3000/", errorsOnly[0]!["page"]!.GetValue<string>());
            Assert.NotNull(errorsOnly[0]!["time"]);
            Assert.Single((JsonArray)(await service.LogAsync(A(), "errors"))!);
            var failed = (JsonArray)(await service.LogAsync(A(("failed", true)), "net"))!;
            Assert.Equal("http://localhost:3000/api", Assert.Single(failed)!["url"]!.GetValue<string>());
            Assert.Equal("cleared 2", (await service.LogAsync(A(("action", "clear")), "console"))!.GetValue<string>());
            Assert.Empty((JsonArray)(await service.LogAsync(A(), "console"))!);
        }
    }

    [Fact]
    public async Task Init_scripts_go_to_the_pane_in_order_and_can_be_listed_removed_and_cleared()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            panes.Items.Add(FakeSurfaces.Pane("b1"));
            var file = Path.Combine(box.Root, "probe.js");
            File.WriteAllText(file, "parent.postMessage('hi', '*');\nconsole.log(1);");
            Assert.StartsWith("i1 added", (await service.AddInitScriptAsync(A(("script", "window.__early = 1"))))!.GetValue<string>());
            Assert.StartsWith("i2 added", (await service.AddInitScriptAsync(A(("file", file), ("main-frame", true))))!.GetValue<string>());
            Assert.Equal(["i1", "i2"], panes.InitScripts["b1"].Select(x => x.Id));
            Assert.Equal([false, true], panes.InitScripts["b1"].Select(x => x.MainFrameOnly));
            Assert.Equal(File.ReadAllText(file), panes.InitScripts["b1"][1].Source);

            var listed = (JsonArray)(await service.InitScriptsAsync(A()))!;
            Assert.Equal(["all", "main"], listed.Select(x => x!["frames"]!.GetValue<string>()));
            Assert.Equal(2, listed[1]!["lines"]!.GetValue<int>());

            await service.InitScriptsAsync(A(("action", "remove"), ("id", "i1")));
            Assert.Equal(["i2"], panes.InitScripts["b1"].Select(x => x.Id));
            Assert.StartsWith("i3", (await service.AddInitScriptAsync(A(("script", "1"))))!.GetValue<string>()); // ids are not reused
            Assert.Equal("not_found", (await Assert.ThrowsAsync<BrowserException>(() => service.InitScriptsAsync(A(("action", "remove"), ("id", "i1"))))).Code);
            Assert.Equal("invalid_params", (await Assert.ThrowsAsync<BrowserException>(() => service.AddInitScriptAsync(A(("script", " "))))).Code);

            await service.InitScriptsAsync(A(("action", "clear")));
            Assert.Empty(panes.InitScripts["b1"]);
            Assert.Empty((JsonArray)(await service.InitScriptsAsync(A()))!);
        }
    }

    [Fact]
    public async Task Messages_posted_to_the_page_are_kept_and_filtered_by_origin()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            panes.Items.Add(FakeSurfaces.Pane("b1"));
            service.Record("b1", JsonNode.Parse("""
                {"k":"log","url":"http://localhost:3000/","items":[
                  ["messages",{"origin":"https://pay.example","from":"frame","frame":"#card","data":"{\"fields\":3}","ts":1700000000000}],
                  ["messages",{"origin":"http://localhost:3000","from":"self","data":"tick","ts":1700000000100}]]}
                """)!.AsObject());
            Assert.Equal(2, ((JsonArray)(await service.LogAsync(A(), "messages"))!).Count);
            var pay = Assert.Single((JsonArray)(await service.LogAsync(A(("origin", "pay.example")), "messages"))!);
            Assert.Equal("#card", pay!["frame"]!.GetValue<string>());
            Assert.Equal("cleared 2", (await service.LogAsync(A(("action", "clear")), "messages"))!.GetValue<string>());
        }
    }

    [Fact]
    public void The_log_keeps_only_the_latest_entries()
    {
        var log = new BrowserLog();
        for (var i = 0; i < BrowserLog.Limit + 50; i++) log.Add("console", new JsonObject { ["n"] = i });
        var kept = log.Get("console");
        Assert.Equal(BrowserLog.Limit, kept.Count);
        Assert.Equal(50, kept[0]["n"]!.GetValue<int>());
    }

    [Fact]
    public async Task Opening_waits_for_the_page_and_reports_where_it_went()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            var r = (JsonObject)(await service.OpenAsync(A(("url", "localhost:5173"), ("callerTerminal", "4"))))!;
            Assert.Equal("b1", r["id"]!.GetValue<string>());
            Assert.Equal("4", r["terminal"]!.GetValue<string>());
            Assert.Equal("http://localhost:5173", r["url"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Browser_control_can_be_switched_off()
    {
        var (box, config, service, panes) = Make();
        using (box) using (config)
        {
            panes.Items.Add(FakeSurfaces.Pane("b1"));
            config.Settings.Browser.AgentControl = false;
            var ex = await Assert.ThrowsAsync<BrowserException>(() => service.ClickAsync(A(("target", "e1"))));
            Assert.Equal("not_allowed", ex.Code);
            Assert.Single((JsonArray)service.List()); // listing still works
        }
    }

    [Fact]
    public void The_agent_script_is_embedded_with_its_version_and_keep_alive_filled_in()
    {
        var on = BrowserService.AgentScript(true);
        var off = BrowserService.AgentScript(false);
        Assert.DoesNotContain("__AVX_", on);
        Assert.Contains("'" + BrowserService.AgentVersion + "'", on);
        Assert.Contains("var KEEP_ALIVE = true;", on);
        Assert.Contains("var KEEP_ALIVE = false;", off);
        Assert.Equal(12, BrowserService.AgentVersion.Length);
    }

    [Fact]
    public void The_skill_installs_where_asked()
    {
        var (box, config, service, _) = Make();
        using (box) using (config)
        {
            var dir = Path.Combine(box.Root, "skills");
            var written = (JsonArray)service.InstallSkill(A(("install", true), ("dir", dir)));
            var file = Assert.Single(written)!.GetValue<string>();
            Assert.Equal(Path.Combine(dir, "avamux-browser", "SKILL.md"), file);
            Assert.StartsWith("---\nname: avamux-browser", File.ReadAllText(file));
            Assert.Contains("avamux browser snapshot", service.InstallSkill(A()).GetValue<string>());
        }
    }

    [Fact]
    public void Browser_commands_parse_like_agent_browser()
    {
        var registry = CommandRegistry.Build(null);
        var snap = CliParser.Parse(registry, ["browser", "snapshot", "-i", "-c", "--surface", "b2"]);
        Assert.Null(snap.Error);
        var a = new Args(snap.Args);
        Assert.True(a.Flag("interactive"));
        Assert.True(a.Flag("compact"));
        Assert.Equal("b2", a.Str("surface"));

        var fill = CliParser.Parse(registry, ["browser", "fill", "e3", "ada@example.com", "--snapshot-after"]);
        Assert.Null(fill.Error);
        Assert.Equal("ada@example.com", new Args(fill.Args).Str("text"));
        Assert.True(new Args(fill.Args).Flag("snapshot-after"));

        var find = CliParser.Parse(registry, ["browser", "find", "role", "button", "click", "--name", "Save"]);
        Assert.Equal("click", new Args(find.Args).Str("action"));
        Assert.Equal("Save", new Args(find.Args).Str("name"));

        var press = CliParser.Parse(registry, ["browser", "press", "Tab", "Tab", "Enter"]);
        Assert.Equal("Tab Tab Enter", new Args(press.Args).Str("key"));

        Assert.Contains("browser snapshot", CliParser.Help(registry, "browser"));
        Assert.Contains("-i, --interactive", CliParser.Help(registry, "browser snapshot"));
    }

    [Fact]
    public async Task Terminals_know_their_own_tab_id()
    {
        if (OperatingSystem.IsWindows()) return;
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var terminals = new TerminalManager(box.Paths, config);
        var s = terminals.Create(new TerminalStartOptions("/bin/sh", ["-c", "echo tab=$AVAMUX_TERMINAL_ID browser=${BROWSER:-none}; sleep 2"], box.Root, "probe", 80, 24));
        var seen = "";
        for (var i = 0; i < 100 && !seen.Contains("tab="); i++) { await Task.Delay(50); seen = Encoding.UTF8.GetString(s.Snapshot()); }
        Assert.Contains($"tab={s.Id} browser=none", seen);

        config.Settings.Browser.SetBrowserVariable = true;
        var t = terminals.Create(new TerminalStartOptions("/bin/sh", ["-c", "echo browser=$BROWSER; sleep 2"], box.Root, "probe2", 80, 24));
        seen = "";
        for (var i = 0; i < 100 && !seen.Contains("browser="); i++) { await Task.Delay(50); seen = Encoding.UTF8.GetString(t.Snapshot()); }
        Assert.Contains(Path.Combine(box.Paths.DataDir, "browser", "avamux-open-url"), seen);
        Assert.Contains("browser open", File.ReadAllText(Path.Combine(box.Paths.DataDir, "browser", "avamux-open-url")));
    }
}
