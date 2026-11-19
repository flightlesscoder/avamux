using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avamux.Core.Commands;
using Avamux.Core.Config;

namespace Avamux.Core.Browser;

/// <summary>
/// The `avamux browser` commands: open and find panes, and drive the page in one (snapshot with refs, click, fill,
/// wait, read the console…). The panes themselves live in the UI behind <see cref="IBrowserSurfaces"/>; everything
/// that happens inside a page goes through the agent script (agent.js).
/// </summary>
public sealed class BrowserService
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly ConcurrentDictionary<string, BrowserLog> _logs = new();
    private static readonly Lazy<string> AgentSource = new(() =>
    {
        using var s = typeof(BrowserService).Assembly.GetManifestResourceStream("Avamux.Core.Browser.agent.js")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });
    private static readonly Lazy<string> SkillSource = new(() =>
    {
        using var s = typeof(BrowserService).Assembly.GetManifestResourceStream("Avamux.Core.Browser.SKILL.md")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    /// <summary>Changes whenever agent.js does, so a page holding an older copy gets the new one.</summary>
    public static readonly string AgentVersion = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(AgentSource.Value)))[..12];

    public BrowserService(AppPaths paths, ConfigService config)
    {
        _paths = paths;
        _config = config;
    }

    public IBrowserSurfaces Surfaces { get; set; } = new NullBrowserSurfaces();
    private BrowserSettings Settings => _config.Settings.Browser;

    /// <summary>The script a page gets at document start (and on first use where that is not possible).</summary>
    public static string AgentScript(bool keepAlive) =>
        AgentSource.Value.Replace("'__AVX_VERSION__'", "'" + AgentVersion + "'").Replace("__AVX_KEEP_ALIVE__", keepAlive ? "true" : "false");

    public static string Skill => SkillSource.Value;

    public BrowserLog Log(string id) => _logs.GetOrAdd(id, _ => new BrowserLog());
    public void Forget(string id) => _logs.TryRemove(id, out _);

    /// <summary>A batch of console/error/network records a page posted.</summary>
    public void Record(string id, JsonObject message)
    {
        if (message["items"] is not JsonArray items) return;
        var log = Log(id);
        var url = message["url"]?.GetValue<string>();
        foreach (var item in items.OfType<JsonArray>())
        {
            if (item.Count < 2 || item[0]?.GetValue<string>() is not { } kind || item[1] is not JsonObject rec) continue;
            var copy = (JsonObject)rec.DeepClone();
            if (url is not null && kind != "net") copy["page"] = url;
            log.Add(kind, copy);
        }
    }

    // ---- addresses ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Turns what someone typed into an address: full URLs pass, "example.com" and "localhost:3000" get a scheme,
    /// a file path becomes file://, and anything else is searched for.
    /// </summary>
    public static string NormalizeUrl(string text, string searchUrl)
    {
        var t = text.Trim();
        if (t.Length == 0) return "about:blank";
        if (t.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            throw new BrowserException("invalid_params", "javascript: addresses are not opened; use `avamux browser eval` to run script.");
        if (t.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return t;
        if (Uri.TryCreate(t, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "file") return abs.AbsoluteUri;
        if (t.StartsWith('/') || t.StartsWith("~/"))
        {
            var p = t.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), t[2..]) : t;
            return new Uri(p).AbsoluteUri;
        }
        if (!t.Contains(' '))
        {
            var host = t.Split('/', '?', '#')[0];
            var hostOnly = host.Split(':')[0];
            var local = hostOnly is "localhost" || hostOnly.EndsWith(".localhost") || hostOnly.EndsWith(".local") || System.Net.IPAddress.TryParse(hostOnly, out _);
            if (local) return "http://" + t;
            if (hostOnly.Contains('.') && !hostOnly.StartsWith('.') && !hostOnly.EndsWith('.') && hostOnly.Split('.')[^1].All(char.IsLetter))
                return "https://" + t;
        }
        return (string.IsNullOrWhiteSpace(searchUrl) ? "https://duckduckgo.com/?q={query}" : searchUrl).Replace("{query}", Uri.EscapeDataString(t));
    }

    public string Normalize(string text) => NormalizeUrl(text, Settings.SearchUrl);

    /// <summary>Whether a link clicked in a terminal goes to the system browser rather than the tab's pane.</summary>
    public bool OpensExternally(string url, bool modifier)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return true;
        foreach (var pattern in Settings.ExternalSites.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = pattern.ToLowerInvariant();
            if (p.StartsWith("*.") ? u.Host.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase) || u.Host.Equals(p[2..], StringComparison.OrdinalIgnoreCase) : u.Host.Equals(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return Settings.TerminalLinksInPane == modifier;
    }

    /// <summary>A link clicked in a terminal: into the tab's browser pane, or the system browser (see <see cref="OpensExternally"/>).</summary>
    public async Task OpenLinkAsync(string terminalId, string url, bool modifier)
    {
        if (Surfaces is NullBrowserSurfaces || OpensExternally(url, modifier)) { Shell.Open(url); return; }
        try { await Surfaces.OpenAsync(new BrowserOpenOptions(url, terminalId, false, true, null, null, false)); }
        catch (Exception ex)
        {
            Logging.Log.Warn("browser", $"Could not open {url} in a pane, using the system browser: {ex.Message}");
            Shell.Open(url);
        }
    }

    // ---- which pane a command is for ----------------------------------------------------------------------------

    public static string? NormalizeId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "browser:", "surface:", "browser", "b" })
            if (t.StartsWith(prefix) && int.TryParse(t[prefix.Length..], out var n)) return "b" + n;
        return int.TryParse(t, out var m) ? "b" + m : t;
    }

    /// <summary>
    /// The pane a command is for: --surface, else the one beside the calling terminal tab (an agent in a tab gets its
    /// own), else the only one there is. Never a guess from what happens to have focus.
    /// </summary>
    public BrowserSurfaceInfo Resolve(Args a)
    {
        var list = Surfaces.List();
        if (NormalizeId(a.Str("surface")) is { } id)
            return list.FirstOrDefault(s => s.Id == id) ?? throw new BrowserException("not_found", $"There is no browser pane {id}. See `avamux browser list`.");
        if (Caller(a) is { } term)
        {
            var mine = list.Where(s => s.Terminal == term).ToList();
            if (mine.Count > 0) return mine.FirstOrDefault(s => s.Active && s.Placement == "dock") ?? mine.FirstOrDefault(s => s.Active) ?? mine[^1];
        }
        if (list.Count == 1) return list[0];
        if (list.Count == 0) throw new BrowserException("not_found", "No browser pane is open. Open one with `avamux browser open <url>`.");
        throw new BrowserException("invalid_params", $"{list.Count} browser panes are open and none belongs to this terminal; say which with --surface (see `avamux browser list`).");
    }

    /// <summary>The terminal tab the CLI was run from (AVAMUX_TERMINAL_ID, which the CLI passes along as callerTerminal).</summary>
    public static string? Caller(Args a) => a.Str("callerTerminal") is { Length: > 0 } t ? t : null;

    private void EnsureAllowed()
    {
        if (!Settings.AgentControl)
            throw new BrowserException("not_allowed", "Browser control from the command line is switched off (browser.agentControl in Settings).");
    }

    // ---- running script in a page --------------------------------------------------------------------------------

    /// <summary>
    /// Wraps an expression so it always returns JSON: {ok,v} or {ok:false,code,error}. The agent script is put in
    /// first when the page does not have it (or has an older one); a promise is parked and collected afterwards.
    /// </summary>
    public static string Wrap(string expression, bool keepAlive) =>
        "(function(){try{if(!window.__avx||window.__avx.v!=='" + AgentVersion + "'){" + AgentScript(keepAlive) + "}" +
        "var __r=(" + expression + ");" +
        "if(__r&&typeof __r.then==='function')return JSON.stringify({ok:true,pending:window.__avx.park(__r)});" +
        "return JSON.stringify({ok:true,v:window.__avx.out(__r)});" +
        "}catch(e){return JSON.stringify({ok:false,code:(e&&typeof e.code==='string'&&e.code)||'js_error',error:String((e&&e.name&&e.name!=='Error'?e.name+': ':'')+((e&&e.message)||e))});}})()";

    public static string Js(object? value) => JsonSerializer.Serialize(value, JsonUtil.Compact);

    /// <summary>Runs an expression in the pane's page and returns its (JSON) value.</summary>
    public async Task<JsonNode?> CallAsync(BrowserSurfaceInfo s, string expression, int timeoutMs = 30000)
    {
        if (s.Dialog is { } dialog)
            throw new BrowserException("invalid_state", $"The page is waiting on a {dialog.Type} (\"{Clip(dialog.Message, 120)}\"). Answer it with `avamux browser dialog accept` or `dismiss`.");
        var raw = await EvalRawAsync(s.Id, Wrap(expression, s.KeepAlive), timeoutMs);
        var reply = Parse(raw);
        if (reply["pending"] is JsonValue pv && pv.TryGetValue<int>(out var pendingId))
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                await Task.Delay(40);
                var r = Parse(await EvalRawAsync(s.Id, Wrap($"window.__avx.collect({pendingId})", s.KeepAlive), timeoutMs));
                if (r["v"] is JsonObject p && p["done"]?.GetValue<bool>() == true)
                {
                    if (p["ok"]?.GetValue<bool>() != true) throw new BrowserException("js_error", p["e"]?.ToString() ?? "The promise was rejected.");
                    return p["v"]?.DeepClone();
                }
                if (DateTime.UtcNow > until) throw new BrowserException("timeout", $"The page's promise did not settle within {timeoutMs} ms.");
            }
        }
        return reply["v"]?.DeepClone();
    }

    private async Task<string?> EvalRawAsync(string id, string script, int timeoutMs)
    {
        var eval = Surfaces.EvalAsync(id, script);
        var until = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
        while (true)
        {
            // An alert, confirm or prompt opened by the script stops it until someone answers: say so instead of waiting.
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new BrowserException("timeout", $"The page did not answer within {timeoutMs} ms (busy, or stuck in a script).");
            if (await Task.WhenAny(eval, Task.Delay(left < TimeSpan.FromMilliseconds(150) ? left : TimeSpan.FromMilliseconds(150))) == eval) return await eval;
            if (Surfaces.List().FirstOrDefault(x => x.Id == id) is { Dialog: { } d })
            {
                _ = eval.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                throw new BrowserDialogOpened(d);
            }
        }
    }

    private static JsonObject Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) throw new BrowserException("invalid_state", "The page did not answer (it may still be loading). Try again, or `avamux browser wait --load-state load`.");
        JsonObject? o;
        try { o = JsonNode.Parse(raw) as JsonObject; }
        catch (JsonException) { throw new BrowserException("js_error", "Unexpected answer from the page: " + Clip(raw, 200)); }
        if (o is null) throw new BrowserException("js_error", "Unexpected answer from the page: " + Clip(raw, 200));
        if (o["ok"]?.GetValue<bool>() != true) throw new BrowserException(o["code"]?.GetValue<string>() ?? "js_error", o["error"]?.GetValue<string>() ?? "The page reported an error.");
        return o;
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    private static string Opts(params (string Key, object? Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs)
            if (v is not null) o[k] = JsonSerializer.SerializeToNode(v, JsonUtil.Compact);
        return o.ToJsonString();
    }

    // ---- commands -----------------------------------------------------------------------------------------------

    public JsonNode List() => new JsonArray(Surfaces.List().Select(Describe).ToArray<JsonNode?>());

    public static JsonObject Describe(BrowserSurfaceInfo s)
    {
        var o = new JsonObject
        {
            ["id"] = s.Id, ["title"] = s.Title, ["url"] = s.Url, ["terminal"] = s.Terminal, ["placement"] = s.Placement,
            ["active"] = s.Active, ["onScreen"] = s.OnScreen, ["keepAlive"] = s.KeepAlive, ["loading"] = s.Loading,
        };
        if (s.Profile is not null) o["profile"] = s.Profile;
        if (Math.Abs(s.Zoom - 1) > 0.001) o["zoom"] = s.Zoom;
        if (s.Viewport is not null) o["viewport"] = s.Viewport;
        if (s.Dialog is not null) o["dialog"] = $"{s.Dialog.Type}: {s.Dialog.Message}";
        if (s.DevTools) o["devTools"] = true;
        if (s.DesignMode) o["designMode"] = true;
        return o;
    }

    public JsonNode Identify(Args a)
    {
        var term = Caller(a);
        var list = Surfaces.List();
        var mine = term is null ? [] : list.Where(s => s.Terminal == term).ToList();
        BrowserSurfaceInfo? current = null;
        try { current = Resolve(a); } catch (BrowserException) { }
        return new JsonObject
        {
            ["terminal"] = term,
            ["surface"] = current?.Id,
            ["url"] = current?.Url,
            ["title"] = current?.Title,
            ["mine"] = new JsonArray(mine.Select(s => (JsonNode)s.Id).ToArray()),
            ["all"] = list.Count,
        };
    }

    public async Task<JsonNode?> OpenAsync(Args a)
    {
        EnsureAllowed();
        var url = a.Str("url") is { Length: > 0 } u ? Normalize(u) : Normalize(Settings.HomePage);
        var info = await Surfaces.OpenAsync(new BrowserOpenOptions(url, a.Str("terminal") ?? Caller(a), a.Flag("window"), a.Flag("focus"),
            a.Has("keep-alive") ? a.Flag("keep-alive") : null, a.Str("profile"), a.Flag("private")));
        if (!a.Flag("no-wait") && url != "about:blank") await Surfaces.WaitForLoadAsync(info.Id, 0, TimeSpan.FromMilliseconds(a.Int("timeout-ms") ?? 30000));
        info = Surfaces.List().FirstOrDefault(s => s.Id == info.Id) ?? info;
        return new JsonObject { ["id"] = info.Id, ["url"] = info.Url, ["title"] = info.Title, ["terminal"] = info.Terminal, ["placement"] = info.Placement };
    }

    public async Task<JsonNode?> GotoAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var url = Normalize(a.Require("url"));
        var sameDoc = Uri.TryCreate(s.Url, UriKind.Absolute, out var from) && Uri.TryCreate(url, UriKind.Absolute, out var to)
            && from.GetLeftPart(UriPartial.Query) == to.GetLeftPart(UriPartial.Query) && to.Fragment.Length > 0;
        await Surfaces.NavigateAsync(s.Id, url);
        if (!a.Flag("no-wait") && !sameDoc) await Surfaces.WaitForLoadAsync(s.Id, s.Loads, TimeSpan.FromMilliseconds(a.Int("timeout-ms") ?? 30000));
        return await AfterAsync(a, s.Id, $"opened {url}");
    }

    public async Task<JsonNode?> GoAsync(Args a, string action)
    {
        EnsureAllowed();
        var s = Resolve(a);
        await Surfaces.GoAsync(s.Id, action);
        if (action != "stop" && !a.Flag("no-wait")) await Surfaces.WaitForLoadAsync(s.Id, s.Loads, TimeSpan.FromMilliseconds(a.Int("timeout-ms") ?? (action.Contains("reload") ? 30000 : 8000)));
        var after = Surfaces.List().FirstOrDefault(x => x.Id == s.Id);
        return await AfterAsync(a, s.Id, $"{action}: {after?.Url ?? s.Url}");
    }

    public async Task<JsonNode?> CloseAsync(Args a)
    {
        var s = Resolve(a);
        await Surfaces.CloseAsync(s.Id);
        Forget(s.Id);
        return $"closed {s.Id}";
    }

    public async Task<JsonNode?> ShowAsync(Args a)
    {
        var s = Resolve(a);
        await Surfaces.ShowAsync(s.Id, a.Flag("focus"));
        return $"showing {s.Id}";
    }

    public async Task<JsonNode?> MoveAsync(Args a)
    {
        var s = Resolve(a);
        var to = a.Require("to").ToLowerInvariant();
        if (to is not ("dock" or "window")) throw new BrowserException("invalid_params", "Move to dock or window.");
        await Surfaces.MoveAsync(s.Id, to, a.Str("terminal"));
        return $"{s.Id} is now in {(to == "dock" ? "the dock" : "its own window")}";
    }

    public async Task<JsonNode?> SnapshotAsync(Args a, BrowserSurfaceInfo? s = null, bool? interactive = null)
    {
        EnsureAllowed();
        s ??= Resolve(a);
        var r = await CallAsync(s, "window.__avx.snapshot(" + Opts(("interactive", interactive ?? a.Flag("interactive")), ("compact", a.Flag("compact")),
            ("maxDepth", a.Int("max-depth")), ("selector", a.Str("selector")), ("cursor", a.Flag("cursor")), ("maxLines", a.Int("max-lines"))) + ")");
        return r?["text"]?.GetValue<string>() ?? "";
    }

    /// <summary>After an action: the result, plus a fresh snapshot when --snapshot-after asked for one.</summary>
    private async Task<JsonNode?> AfterAsync(Args a, string id, string message)
    {
        if (!a.Flag("snapshot-after")) return message;
        // Let a click's navigation or re-render start, then wait for it to finish (briefly).
        await Task.Delay(150);
        var s = Surfaces.List().FirstOrDefault(x => x.Id == id) ?? throw new BrowserException("not_found", $"Browser pane {id} has gone.");
        if (s.Loading) { await Surfaces.WaitForLoadAsync(id, s.Loads, TimeSpan.FromSeconds(10)); s = Surfaces.List().First(x => x.Id == id); }
        try { await WaitUntilAsync(s, "{\"load\":\"domcontentloaded\"}", null, 5000); } catch (BrowserException) { }
        var snap = await SnapshotAsync(a, s, interactive: true);
        return message + "\n\n" + snap;
    }

    private static string Text(JsonNode? r, string verb) => r switch
    {
        JsonObject o when o.Count > 0 => $"{o.First().Key} {Plain(o.First().Value)}" + string.Concat(o.Skip(1).Select(p => $", {p.Key} {Plain(p.Value)}")),
        null => verb,
        _ => verb + " " + Plain(r),
    };

    private static string Plain(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray arr => string.Join(", ", arr.Select(Plain)),
        null => "null",
        _ => n.ToJsonString(),
    };

    /// <summary>A page action: runs the agent call, then (with --snapshot-after) snapshots.</summary>
    public async Task<JsonNode?> ActAsync(Args a, string verb, string expression)
    {
        EnsureAllowed();
        var s = Resolve(a);
        JsonNode? r;
        try { r = await CallAsync(s, expression); }
        catch (BrowserDialogOpened d) { return $"{verb}; {d.Describe()}"; }
        return await AfterAsync(a, s.Id, Text(r, verb));
    }

    public Task<JsonNode?> ClickAsync(Args a, int count = 1) =>
        ActAsync(a, "clicked", "window.__avx.click(" + Js(a.Require("target")) + "," + Opts(("button", a.Str("button")), ("count", count == 1 ? a.Int("count") : count),
            ("force", a.Flag("force")), ("ctrl", a.Flag("ctrl")), ("shift", a.Flag("shift")), ("alt", a.Flag("alt")), ("meta", a.Flag("meta"))) + ")");

    public Task<JsonNode?> FillAsync(Args a) =>
        ActAsync(a, "filled", "window.__avx.fill(" + Js(a.Require("target")) + "," + Js(a.Str("text") ?? "") + ")");

    public Task<JsonNode?> TypeAsync(Args a) =>
        ActAsync(a, "typed", "window.__avx.type(" + Js(a.Str("target")) + "," + Js(a.Require("text")) + "," + Opts(("clear", a.Flag("clear"))) + ")");

    public Task<JsonNode?> PressAsync(Args a) =>
        ActAsync(a, "pressed", "window.__avx.press(" + Js(a.Require("key")) + "," + Js(a.Str("target")) + ")");

    public Task<JsonNode?> SelectAsync(Args a) =>
        ActAsync(a, "selected", "window.__avx.select(" + Js(a.Require("target")) + "," + Js(a.List("values")) + ")");

    public Task<JsonNode?> CheckAsync(Args a, bool on) =>
        ActAsync(a, on ? "checked" : "unchecked", "window.__avx.check(" + Js(a.Require("target")) + "," + (on ? "true" : "false") + ")");

    public Task<JsonNode?> ScrollAsync(Args a) =>
        ActAsync(a, "scrolled", "window.__avx.scroll(" + Opts(("direction", a.Str("direction")), ("amount", a.Num("amount")), ("dx", a.Num("dx")), ("dy", a.Num("dy")), ("selector", a.Str("selector"))) + ")");

    public async Task<JsonNode?> UploadAsync(Args a)
    {
        var files = a.List("files");
        if (files.Count == 0) throw new BrowserException("invalid_params", "Name at least one file to upload.");
        var list = new JsonArray();
        long total = 0;
        foreach (var f in files)
        {
            var path = Path.GetFullPath(f);
            if (!File.Exists(path)) throw new FileNotFoundException($"There is no file {path}.");
            var bytes = await File.ReadAllBytesAsync(path);
            total += bytes.Length;
            if (total > 50L * 1024 * 1024) throw new BrowserException("invalid_params", "Uploads are limited to 50 MB in total.");
            list.Add(new JsonObject { ["name"] = Path.GetFileName(path), ["type"] = MimeOf(path), ["data"] = Convert.ToBase64String(bytes) });
        }
        return await ActAsync(a, "uploaded", "window.__avx.upload(" + Js(a.Require("target")) + "," + list.ToJsonString() + ")");
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".csv" => "text/csv", ".json" => "application/json", ".html" => "text/html",
        ".zip" => "application/zip", ".mp4" => "video/mp4", ".mp3" => "audio/mpeg", _ => "application/octet-stream",
    };

    public async Task<JsonNode?> GetAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var what = a.Require("what").ToLowerInvariant();
        if (what is "url" or "title" && a.Str("target") is null) return what == "url" ? s.Url : s.Title;
        var r = await CallAsync(s, "window.__avx.get(" + Js(what) + "," + Js(a.Str("target")) + "," + Opts(("attr", a.Str("attr")), ("property", a.Str("property")), ("outer", a.Flag("outer"))) + ")");
        return r;
    }

    public async Task<JsonNode?> IsAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        return await CallAsync(s, "window.__avx.is(" + Js(a.Require("what").ToLowerInvariant()) + "," + Js(a.Require("target")) + ")");
    }

    public async Task<JsonNode?> FindAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var by = a.Require("by").ToLowerInvariant();
        var r = await CallAsync(s, "window.__avx.find(" + Js(by) + "," + Js(a.Require("value")) + "," + Opts(("name", a.Str("name")), ("exact", a.Flag("exact")),
            ("nth", a.Int("nth")), ("first", a.Flag("first")), ("last", a.Flag("last"))) + ")") as JsonObject;
        var action = a.Str("action")?.ToLowerInvariant();
        if (action is null) return r;
        var refs = r?["refs"] as JsonArray;
        if (refs is null || refs.Count == 0) throw new BrowserException("not_found", $"Nothing found by {by} \"{a.Str("value")}\".");
        var target = refs[0]!.GetValue<string>();
        var text = a.Str("text");
        var expr = action switch
        {
            "click" => $"window.__avx.click({Js(target)},{{}})",
            "dblclick" => $"window.__avx.click({Js(target)},{{\"count\":2}})",
            "hover" => $"window.__avx.hover({Js(target)})",
            "focus" => $"window.__avx.focus({Js(target)})",
            "fill" => $"window.__avx.fill({Js(target)},{Js(text ?? "")})",
            "type" => $"window.__avx.type({Js(target)},{Js(text ?? "")},{{}})",
            "check" => $"window.__avx.check({Js(target)},true)",
            "uncheck" => $"window.__avx.check({Js(target)},false)",
            "text" => $"window.__avx.get('text',{Js(target)},{{}})",
            "highlight" => $"window.__avx.highlight({Js(target)},2000)",
            _ => throw new BrowserException("invalid_params", "After find, use click, dblclick, hover, focus, fill, type, check, uncheck, text or highlight."),
        };
        JsonNode? result;
        try { result = await CallAsync(s, expr); }
        catch (BrowserDialogOpened d) { return $"{action}; {d.Describe()}"; }
        return await AfterAsync(a, s.Id, Text(result, action));
    }

    public async Task<JsonNode?> EvalAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var code = a.Require("script").Trim();
        try { return await CallAsync(s, "(" + code + "\n)", a.Int("timeout-ms") ?? 30000); }
        catch (Exception ex) when (ex is not BrowserException && ex.Message.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase))
        {
            // Not an expression: run it as the body of a function, so statements and `return` work.
            return await CallAsync(s, "(function(){\n" + code + "\n})()", a.Int("timeout-ms") ?? 30000);
        }
    }

    public async Task<JsonNode?> WaitAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var timeout = a.Int("timeout-ms") ?? 30000;
        if (a.Int("ms") is { } ms && !a.Has("selector") && !a.Has("text") && !a.Has("url") && !a.Has("url-contains") && !a.Has("load-state") && !a.Has("function"))
        {
            await Task.Delay(Math.Clamp(ms, 0, 600_000));
            return $"waited {ms} ms";
        }
        var load = a.Str("load-state")?.ToLowerInvariant();
        if (load is not (null or "load" or "complete" or "domcontentloaded" or "networkidle"))
            throw new BrowserException("invalid_params", "--load-state is load, complete, domcontentloaded or networkidle.");
        var cond = Opts(("selector", a.Str("selector")), ("state", a.Str("state")), ("text", a.Str("text")), ("urlContains", a.Str("url-contains")), ("url", a.Str("url")), ("load", load));
        if (cond == "{}" && a.Str("function") is null) throw new BrowserException("invalid_params", "Wait for what? Give --selector, --text, --url, --url-contains, --load-state, --function, or a number of milliseconds.");
        await WaitUntilAsync(s, cond, a.Str("function"), timeout);
        return "ok";
    }

    private async Task WaitUntilAsync(BrowserSurfaceInfo s, string condition, string? function, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var why = "";
        var expr = function is null ? $"window.__avx.check_({condition})"
            : $"(function(){{var c=window.__avx.check_({condition});if(!c.ok)return c;var f;try{{f=({function}\n);}}catch(e){{return {{ok:false,why:String(e&&e.message||e)}};}}" +
              "if(f&&typeof f.then==='function')return f.then(function(v){return {ok:!!v,why:'the function returned '+v};});return {ok:!!f,why:'the function returned '+f};})()";
        while (true)
        {
            try
            {
                var cur = Surfaces.List().FirstOrDefault(x => x.Id == s.Id) ?? throw new BrowserException("not_found", $"Browser pane {s.Id} has gone.");
                var r = await CallAsync(cur, expr, Math.Max(1000, (int)(until - DateTime.UtcNow).TotalMilliseconds));
                if (r?["ok"]?.GetValue<bool>() == true) return;
                why = r?["why"]?.ToString() ?? "";
            }
            catch (BrowserException ex) when (ex.Code is "invalid_state" && !ex.Message.Contains("waiting on")) { why = "the page is loading"; }
            if (DateTime.UtcNow >= until) throw new BrowserException("timeout", $"Timed out after {timeoutMs} ms" + (why.Length > 0 ? $" ({why})." : "."));
            await Task.Delay(100);
        }
    }

    public async Task<JsonNode?> ScreenshotAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var file = a.Str("out") is { Length: > 0 } o ? Path.GetFullPath(o) : NewFile(s.Id, "png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await Surfaces.ScreenshotAsync(s.Id, file, a.Flag("full"));
        return file;
    }

    public async Task<JsonNode?> PdfAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var file = a.Str("out") is { Length: > 0 } o ? Path.GetFullPath(o) : NewFile(s.Id, "pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await Surfaces.PdfAsync(s.Id, file);
        return file;
    }

    private string NewFile(string id, string ext) =>
        Path.Combine(_paths.DataDir, "browser", "captures", $"{id}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.{ext}");

    public async Task<JsonNode?> LogAsync(Args a, string kind)
    {
        var s = Resolve(a);
        var log = Log(s.Id);
        if ((a.Str("action") ?? "list").ToLowerInvariant() == "clear")
        {
            var n = log.Clear(kind);
            try { await CallAsync(s, $"(window.__avx.{(kind == "errors" ? "errors" : kind == "net" ? "net" : "console")}.length=0)"); } catch (BrowserException) { }
            return $"cleared {n}";
        }
        IEnumerable<JsonObject> items = log.Get(kind);
        if (a.Str("level") is { } level) items = items.Where(x => string.Equals(x["level"]?.ToString(), level, StringComparison.OrdinalIgnoreCase) || level == "warn" && x["level"]?.ToString() == "warning");
        if (a.Str("filter") is { } f) items = items.Where(x => x.ToJsonString().Contains(f, StringComparison.OrdinalIgnoreCase));
        if (kind == "net" && a.Flag("failed")) items = items.Where(x => x["failed"]?.GetValue<bool>() == true || x["status"] is JsonValue st && st.TryGetValue<int>(out var code) && code >= 400);
        var limit = a.Int("limit") ?? 50;
        var list = items.ToList();
        var shown = list.Skip(Math.Max(0, list.Count - limit)).Select(x =>
        {
            if (x["ts"] is JsonValue tv && tv.TryGetValue<long>(out var ms)) x["time"] = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.fff");
            x.Remove("ts");
            return (JsonNode)x;
        }).ToArray();
        return new JsonArray(shown);
    }

    public async Task<JsonNode?> CookiesAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        switch ((a.Str("action") ?? "get").ToLowerInvariant())
        {
            case "get" or "list":
            {
                var all = await CallAsync(s, "window.__avx.cookies()") as JsonArray ?? [];
                return a.Str("name") is { } n ? all.FirstOrDefault(c => c?["name"]?.ToString() == n)?["value"]?.DeepClone() : all;
            }
            case "set":
                return Text(await CallAsync(s, "window.__avx.setCookie(" + Js(a.Require("name")) + "," + Js(a.Str("value") ?? "") + "," +
                    Opts(("domain", a.Str("domain")), ("path", a.Str("path")), ("maxAge", a.Int("max-age")), ("secure", a.Flag("secure") ? true : null), ("sameSite", a.Str("same-site"))) + ")"), "set");
            case "clear":
                if (a.Str("name") is null && !a.Flag("all")) throw new BrowserException("invalid_params", "Say which cookies to clear: a name, or --all.");
                return Text(await CallAsync(s, "window.__avx.clearCookies(" + Opts(("name", a.Str("name")), ("path", a.Str("path"))) + ")"), "cleared");
        }
        throw new BrowserException("invalid_params", "cookies get, set or clear.");
    }

    public async Task<JsonNode?> StorageAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var kind = a.Require("kind").ToLowerInvariant();
        if (kind is not ("local" or "session")) throw new BrowserException("invalid_params", "storage local or storage session.");
        return await CallAsync(s, "window.__avx.storage(" + Js(kind) + "," + Js((a.Str("action") ?? "get").ToLowerInvariant()) + "," + Js(a.Str("key")) + "," + Js(a.Str("value")) + ")");
    }

    public async Task<JsonNode?> StateAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        var file = Path.GetFullPath(a.Require("file"));
        switch (a.Require("action").ToLowerInvariant())
        {
            case "save":
            {
                var state = await CallAsync(s, "window.__avx.state()") as JsonObject ?? new JsonObject();
                state["saved"] = DateTime.Now.ToString("O");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, JsonUtil.Pretty(state));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return $"saved {(state["cookies"] as JsonArray)?.Count ?? 0} cookies and {(state["local"] as JsonObject)?.Count ?? 0}+{(state["session"] as JsonObject)?.Count ?? 0} storage entries for {state["origin"]} to {file}";
            }
            case "load":
            {
                var state = JsonNode.Parse(await File.ReadAllTextAsync(file)) as JsonObject ?? throw new BrowserException("invalid_params", $"{file} is not a saved state.");
                var origin = state["origin"]?.ToString();
                var here = Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "";
                if (origin is { Length: > 0 } && !string.Equals(origin, here, StringComparison.OrdinalIgnoreCase))
                {
                    // Cookies and storage belong to a site: go there first.
                    await Surfaces.NavigateAsync(s.Id, state["url"]?.ToString() ?? origin);
                    await Surfaces.WaitForLoadAsync(s.Id, s.Loads, TimeSpan.FromSeconds(30));
                    s = Surfaces.List().First(x => x.Id == s.Id);
                }
                var r = await CallAsync(s, "window.__avx.loadState(" + state.ToJsonString() + ")");
                var loads = Surfaces.List().First(x => x.Id == s.Id).Loads;
                await Surfaces.GoAsync(s.Id, "reload");
                await Surfaces.WaitForLoadAsync(s.Id, loads, TimeSpan.FromSeconds(30));
                return "loaded " + Plain(r) + " and reloaded";
            }
        }
        throw new BrowserException("invalid_params", "state save <file> or state load <file>.");
    }

    public async Task<JsonNode?> DialogAsync(Args a)
    {
        var s = Resolve(a);
        var action = (a.Str("action") ?? "status").ToLowerInvariant();
        if (action == "status")
            return s.Dialog is { } d ? new JsonObject { ["type"] = d.Type, ["message"] = d.Message, ["default"] = d.DefaultText } : "no dialog is open";
        if (action is not ("accept" or "dismiss")) throw new BrowserException("invalid_params", "dialog status, accept [text] or dismiss.");
        if (s.Dialog is null) throw new BrowserException("invalid_state", "No dialog is open.");
        await Surfaces.AnswerDialogAsync(s.Id, action == "accept", a.Str("text"));
        return $"{action}ed the {s.Dialog.Type}";
    }

    public async Task<JsonNode?> TabAsync(Args a)
    {
        EnsureAllowed();
        var action = (a.Str("action") ?? "list").ToLowerInvariant();
        var list = Surfaces.List();
        BrowserSurfaceInfo? anchor = null;
        try { anchor = Resolve(a); } catch (BrowserException) when (action is "new") { }
        var term = anchor?.Terminal ?? Caller(a);
        var tabs = list.Where(s => s.Terminal == term && s.Placement == (anchor?.Placement ?? "dock")).ToList();
        BrowserSurfaceInfo Pick(string? which)
        {
            if (which is null) return anchor ?? throw new BrowserException("invalid_params", "Which tab?");
            if (int.TryParse(which, out var i) && i >= 0 && i < tabs.Count && !which.StartsWith('b')) return tabs[i];
            var id = NormalizeId(which);
            return list.FirstOrDefault(s => s.Id == id) ?? throw new BrowserException("not_found", $"No tab {which}.");
        }
        switch (action)
        {
            case "list":
                return new JsonArray(tabs.Select((s, i) => (JsonNode)new JsonObject { ["index"] = i, ["id"] = s.Id, ["active"] = s.Active, ["title"] = s.Title, ["url"] = s.Url }).ToArray());
            case "new":
            {
                var url = a.Str("target") is { Length: > 0 } t ? Normalize(t) : Normalize(Settings.HomePage);
                var info = await Surfaces.OpenAsync(new BrowserOpenOptions(url, term, anchor?.Placement == "window", a.Flag("focus"), null, anchor?.Profile, false));
                if (url != "about:blank") await Surfaces.WaitForLoadAsync(info.Id, 0, TimeSpan.FromSeconds(30));
                return info.Id;
            }
            case "switch" or "select":
            {
                var t = Pick(a.Str("target"));
                await Surfaces.ShowAsync(t.Id, a.Flag("focus"));
                return $"switched to {t.Id}";
            }
            case "close":
            {
                var t = Pick(a.Str("target"));
                await Surfaces.CloseAsync(t.Id);
                Forget(t.Id);
                return $"closed {t.Id}";
            }
        }
        throw new BrowserException("invalid_params", "tab list, new [url], switch <index|id> or close [index|id].");
    }

    public async Task<JsonNode?> FrameAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        return await CallAsync(s, "window.__avx.selectFrame(" + Js(a.Require("target")) + ")");
    }

    public async Task<JsonNode?> HighlightAsync(Args a)
    {
        EnsureAllowed();
        var s = Resolve(a);
        return Text(await CallAsync(s, "window.__avx.highlight(" + Js(a.Require("target")) + "," + (a.Int("ms") ?? 2000) + ")"), "highlighted");
    }

    public async Task<JsonNode?> DevToolsAsync(Args a)
    {
        var s = Resolve(a);
        var action = (a.Str("action") ?? "toggle").ToLowerInvariant();
        if (action is not ("open" or "close" or "toggle" or "attach" or "detach" or "status")) throw new BrowserException("invalid_params", "devtools open, close, toggle, attach, detach or status.");
        return "developer tools " + await Surfaces.DevToolsAsync(s.Id, action);
    }

    public async Task<JsonNode?> KeepAliveAsync(Args a)
    {
        var s = Resolve(a);
        var action = (a.Str("action") ?? "status").ToLowerInvariant();
        if (action is "on" or "off" or "true" or "false")
        {
            var on = action is "on" or "true";
            await Surfaces.SetKeepAliveAsync(s.Id, on);
            return $"keep alive is {(on ? "on" : "off")} for {s.Id}";
        }
        if (action != "status") throw new BrowserException("invalid_params", "keep-alive on, off or status.");
        return $"keep alive is {(s.KeepAlive ? "on" : "off")} for {s.Id}";
    }

    public async Task<JsonNode?> ZoomAsync(Args a)
    {
        var s = Resolve(a);
        var z = await Surfaces.ZoomAsync(s.Id, (a.Str("level") ?? "status").ToLowerInvariant());
        return $"zoom {Math.Round(z * 100)}%";
    }

    public async Task<JsonNode?> ViewportAsync(Args a)
    {
        var s = Resolve(a);
        var w = a.Str("width");
        if (w is null || w.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            await Surfaces.SetViewportAsync(s.Id, null, null);
            return "viewport follows the pane";
        }
        if (!int.TryParse(w, out var width) || a.Int("height") is not { } height) throw new BrowserException("invalid_params", "viewport <width> <height>, or viewport reset.");
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new BrowserException("invalid_params", "Width and height are 1 to 4096 CSS pixels.");
        return "viewport " + await Surfaces.SetViewportAsync(s.Id, width, height);
    }

    public async Task<JsonNode?> DesignModeAsync(Args a)
    {
        var s = Resolve(a);
        var action = (a.Str("action") ?? "status").ToLowerInvariant();
        bool? on = action switch { "enable" or "on" => true, "disable" or "off" => false, "status" => null, _ => throw new BrowserException("invalid_params", "design-mode enable, disable or status.") };
        var now = await Surfaces.DesignModeAsync(s.Id, on);
        return $"design mode is {(now ? "on: click an element in the page to copy its details" : "off")}";
    }

    /// <summary>Writes the avamux-browser skill where Claude Code (and Codex, if present) look for skills.</summary>
    public JsonNode InstallSkill(Args a)
    {
        if (!a.Flag("install")) return Skill;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();
        if (a.Str("dir") is { Length: > 0 } dir) roots.Add(Path.GetFullPath(dir));
        else
        {
            roots.Add(Path.Combine(home, ".claude", "skills"));
            if (Directory.Exists(Path.Combine(home, ".codex"))) roots.Add(Path.Combine(home, ".codex", "skills"));
        }
        var written = new JsonArray();
        foreach (var root in roots)
        {
            var folder = Path.Combine(root, "avamux-browser");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "SKILL.md");
            File.WriteAllText(file, Skill);
            written.Add(file);
            Logging.Log.Info("browser", $"Installed the avamux-browser skill at {file}");
        }
        return written;
    }
}
