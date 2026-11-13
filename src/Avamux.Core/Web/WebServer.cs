using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avamux.Core.Logging;
using Avamux.Core.Terminal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Avamux.Core.Web;

/// <summary>
/// Loopback-only HTTP server: the xterm.js terminal page and its pty WebSockets, the dashboard folder,
/// the wiki preview, and the JSON command API. Every request must present the per-run token
/// (?t=…, the X-Avamux-Token header, or the SameSite=Strict cookie set from either).
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    private readonly AppServices _app;
    private WebApplication? _web;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<TermSocket, byte>> _sockets = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _dumps = new();
    private int _dumpId;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public WebServer(AppServices app) { _app = app; }

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string TerminalUrl => $"{BaseUrl}/term/index.html?t={Token}";
    public string DashboardUrl => $"{BaseUrl}/dashboard/?t={Token}";
    public string WikiPreviewUrl => $"{BaseUrl}/wiki/_preview?t={Token}";

    public async Task StartAsync()
    {
        var preferred = _app.Config.Settings.Dashboard.Port;
        try { await StartOnAsync(preferred); }
        catch (Exception ex) when (preferred != 0)
        {
            Log.Warn("web", $"Port {preferred} unavailable ({ex.GetBaseException().Message}); using a free port.");
            await StartOnAsync(0);
        }
        _app.Terminals.RenderedTextProvider = id => RequestTextAsync(id, null, false);
        _app.Terminals.Closed += s =>
        {
            if (_sockets.TryRemove(s.Id, out var set)) foreach (var sock in set.Keys) sock.Abort();
        };
        Log.Info("web", $"Local server listening on {BaseUrl}");
    }

    private async Task StartOnAsync(int port)
    {
        // Root the host at the app's own folder. The default is the current directory, and the host watches its
        // content root for appsettings.json changes — started from the home folder, that scans all of it and hangs.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var web = builder.Build();
        web.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        web.Use(Guard);
        web.Map("/ws/term/{id}", TerminalSocketAsync);
        web.MapGet("/term/options.js", () => Results.Content("window.__avamuxOptions = " + TerminalOptions().ToJsonString() + ";", "text/javascript; charset=utf-8"));
        web.MapGet("/term/{**path}", (string path) => Embedded("term/" + path));
        web.MapPost("/api/command", (Delegate)CommandAsync);
        web.MapGet("/api/commands", () => Results.Json(_app.Commands.Describe(), JsonUtil.Options));
        web.MapGet("/dashboard/{**path}", (HttpContext ctx, string? path) =>
            // Without the trailing slash the page's relative links would resolve against the site root.
            string.IsNullOrEmpty(path) && !ctx.Request.Path.Value!.EndsWith('/')
                ? Results.Redirect("/dashboard/" + ctx.Request.QueryString)
                : _app.Dashboard.Serve(ctx, path));
        web.MapGet("/wiki/_preview", () => Results.Content(Wiki.WikiService.PreviewShellHtml(Theming.Themes.Resolve(_app.Config.Settings.Theme)), "text/html; charset=utf-8"));
        web.MapGet("/wiki/_assets/{**path}", (string path) => Embedded("wiki/" + path));
        web.MapGet("/wiki/raw/{**path}", (string path) => _app.Wiki.ServeRaw(path));
        try { await web.StartAsync(); }
        catch { await web.DisposeAsync(); throw; }
        _web = web;
        var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    /// <summary>The terminal settings the xterm.js page needs.</summary>
    public JsonObject TerminalOptions()
    {
        var t = _app.Config.Settings.Terminal;
        var theme = Theming.Themes.Resolve(_app.Config.Settings.Theme);
        return new JsonObject
        {
            ["scrollback"] = t.Scrollback, ["fontFamily"] = t.FontFamily, ["fontSize"] = t.FontSize, ["cursorBlink"] = t.CursorBlink,
            ["copyOnSelect"] = t.CopyOnSelect,
            ["pulse"] = t.CursorPulse, ["pulseShape"] = t.CursorPulseShape is "outline" or "bar" ? t.CursorPulseShape : "block",
            ["pulseColor"] = Theming.Rgb.TryParse(t.CursorPulseColor, out var pulse) ? pulse.Hex : theme["accent"],
            ["crt"] = t.Crt, ["crtIntensity"] = Math.Clamp(t.CrtIntensity, 0, 100), ["crtFlicker"] = t.CrtFlicker,
            ["pulseDouble"] = t.CursorPulseDouble, ["pulseRate"] = Math.Clamp(t.CursorPulseRate, 20, 240), ["pulseGlow"] = Math.Clamp(t.CursorPulseGlow, 0, 100),
            ["background"] = Theming.Rgb.TryParse(t.Background, out var bg) ? bg.Hex : theme["termBg"],
            ["foreground"] = Theming.Rgb.TryParse(t.Foreground, out var fg) ? fg.Hex : theme["termFg"],
            ["cursor"] = theme["accent"], ["selection"] = theme["termSel"], ["panel"] = theme["panel"], ["line"] = theme["lineStrong"], ["text"] = theme["text"], ["dim"] = theme["dim"],
            ["ansi"] = new JsonArray(theme.Ansi.Select(c => (JsonNode)c).ToArray()),
        };
    }

    private async Task Guard(HttpContext ctx, RequestDelegate next)
    {
        // Loopback names only: defeats DNS rebinding.
        var host = ctx.Request.Host.Host;
        if (host is not ("127.0.0.1" or "localhost" or "[::1]")) { ctx.Response.StatusCode = 403; return; }
        var supplied = ctx.Request.Query["t"].FirstOrDefault() ?? ctx.Request.Headers["X-Avamux-Token"].FirstOrDefault();
        var ok = supplied is not null && FixedEquals(supplied, Token);
        if (ok)
            ctx.Response.Cookies.Append("avamux_auth", Token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        else
            ok = ctx.Request.Cookies.TryGetValue("avamux_auth", out var cookie) && FixedEquals(cookie, Token);
        if (!ok)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("avamux: missing or invalid token. Open this page from the app.");
            return;
        }
        // State-changing requests must come from our own pages.
        var origin = ctx.Request.Headers.Origin.FirstOrDefault();
        if (origin is not null && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.IsLoopback && o.Port == Port))
        { ctx.Response.StatusCode = 403; return; }
        ctx.Response.Headers.CacheControl = "no-store";
        await next(ctx);
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static IResult Embedded(string path)
    {
        if (path.Contains("..")) return Results.NotFound();
        var name = "Avamux.Core.Web." + path.Replace('/', '.');
        var stream = typeof(WebServer).Assembly.GetManifestResourceStream(name);
        if (stream is null) return Results.NotFound();
        return Results.Stream(stream, MimeOf(path));
    }

    public static string MimeOf(string path)
    {
        if (ContentTypes.TryGetContentType(path, out var type))
            return type.StartsWith("text/") || type.Contains("javascript") || type.Contains("json") ? type + "; charset=utf-8" : type;
        return "application/octet-stream";
    }

    private async Task<IResult> CommandAsync(HttpContext ctx)
    {
        if (!_app.Config.Settings.Dashboard.AllowAppControl)
            return Results.Json(new JsonObject { ["ok"] = false, ["error"] = "App control from the dashboard is disabled (dashboard.allowAppControl)." }, statusCode: 403);
        JsonObject? body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject; }
        catch { body = null; }
        var name = body?["command"]?.GetValue<string>();
        if (name is null) return Results.Json(new JsonObject { ["ok"] = false, ["error"] = "Expected {\"command\": \"…\", \"args\": {…}}." }, statusCode: 400);
        var response = await _app.Commands.ExecuteAsync(name, body!["args"] as JsonObject ?? new JsonObject());
        return Results.Content(JsonUtil.Line(response), "application/json");
    }

    // ---- terminal sockets ----

    private sealed class TermSocket(WebSocket ws)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public WebSocket Socket => ws;
        public async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type)
        {
            await _gate.WaitAsync();
            try { if (ws.State == WebSocketState.Open) await ws.SendAsync(data, type, true, CancellationToken.None); }
            catch { }
            finally { _gate.Release(); }
        }
        public Task SendJsonAsync(JsonObject o) => SendAsync(Encoding.UTF8.GetBytes(JsonUtil.Line(o)), WebSocketMessageType.Text);
        public void Abort() { try { ws.Abort(); } catch { } }
    }

    private async Task TerminalSocketAsync(HttpContext ctx, string id)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var session = _app.Terminals.Find(id);
        if (session is null) { ctx.Response.StatusCode = 404; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var sock = new TermSocket(ws);
        var set = _sockets.GetOrAdd(session.Id, _ => new());
        set[sock] = 0;

        // Output is queued so the pty read thread never blocks on a slow socket and ordering is preserved.
        var queue = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var (replay, subscription) = session.Attach(chunk => queue.Writer.TryWrite(chunk));
        void OnExit(TerminalSession s, int code) => _ = sock.SendJsonAsync(new JsonObject { ["t"] = "exit", ["code"] = code });
        session.Exited += OnExit;
        var pump = Task.Run(async () =>
        {
            if (replay.Length > 0) await sock.SendAsync(replay, WebSocketMessageType.Binary);
            if (!session.Running) await sock.SendJsonAsync(new JsonObject { ["t"] = "exit", ["code"] = session.ExitCode ?? 0 });
            await foreach (var chunk in queue.Reader.ReadAllAsync())
                await sock.SendAsync(chunk, WebSocketMessageType.Binary);
        });
        try
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                try { await HandleClientMessageAsync(session, sock, text); }
                catch (Exception ex) { Log.Warn("web", $"Bad terminal message: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            subscription.Dispose();
            session.Exited -= OnExit;
            queue.Writer.TryComplete();
            set.TryRemove(sock, out _);
            try { await pump.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    private async Task HandleClientMessageAsync(TerminalSession session, TermSocket sock, string text)
    {
        if (JsonNode.Parse(text) is not JsonObject m) return;
        switch (m["t"]?.GetValue<string>())
        {
            case "i": session.Write(m["d"]!.GetValue<string>()); break;
            case "b": session.Write(Convert.FromBase64String(m["d"]!.GetValue<string>())); break;
            case "r": session.Resize(m["cols"]!.GetValue<int>(), m["rows"]!.GetValue<int>()); break;
            case "title":
                session.OscTitle = m["title"]?.GetValue<string>();
                break;
            case "bell": session.Bell(); break;
            case "copy": await _app.Host.SetClipboardAsync(m["text"]?.GetValue<string>() ?? ""); break;
            case "paste":
                var clip = await _app.Host.GetClipboardAsync();
                if (!string.IsNullOrEmpty(clip)) await sock.SendJsonAsync(new JsonObject { ["t"] = "paste", ["text"] = clip });
                break;
            case "open":
                if (m["url"]?.GetValue<string>() is { } url && (url.StartsWith("http://") || url.StartsWith("https://"))) Shell.Open(url);
                break;
            case "dump":
                if (_dumps.TryRemove(m["n"]!.GetValue<int>(), out var tcs)) tcs.TrySetResult(m["text"]?.GetValue<string>());
                break;
        }
    }

    /// <summary>Asks a connected terminal view for its rendered text. Null when no view is attached.</summary>
    public async Task<string?> RequestTextAsync(string id, int? lines, bool screenOnly)
    {
        if (!_sockets.TryGetValue(id, out var set)) return null;
        var sock = set.Keys.FirstOrDefault(s => s.Socket.State == WebSocketState.Open);
        if (sock is null) return null;
        var n = Interlocked.Increment(ref _dumpId);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dumps[n] = tcs;
        var msg = new JsonObject { ["t"] = "dump", ["n"] = n, ["screen"] = screenOnly };
        if (lines is int l) msg["lines"] = l;
        await sock.SendJsonAsync(msg);
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { _dumps.TryRemove(n, out _); return null; }
    }

    public bool HasView(string id) => _sockets.TryGetValue(id, out var set) && !set.IsEmpty;

    public async ValueTask DisposeAsync()
    {
        if (_web is not null)
        {
            foreach (var set in _sockets.Values) foreach (var s in set.Keys) s.Abort();
            try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _web.StopAsync(stop.Token); } catch { }
            await _web.DisposeAsync();
        }
    }
}
