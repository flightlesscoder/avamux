using System.Text;
using Microsoft.AspNetCore.Http;

namespace Avamux.Core.Dashboard;

public sealed class DashboardSource
{
    public string Id { get; set; } = "";
    /// <summary>"css" or "js".</summary>
    public string Type { get; set; } = "css";
    /// <summary>An http(s) URL, an absolute file path, or a path relative to the dashboard folder.</summary>
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class DashboardState { public List<DashboardSource> Sources { get; set; } = []; }

/// <summary>Serves the user's dashboard folder and injects the avamux JavaScript API plus any enabled extra CSS/JS.</summary>
public sealed class DashboardService
{
    private readonly AppServices _app;
    private readonly object _gate = new();
    private DashboardState _state;

    public DashboardService(AppServices app)
    {
        _app = app;
        _state = JsonUtil.LoadFile<DashboardState>(app.Paths.StateFile("dashboard"));
        Seed();
    }

    public string Root => _app.Paths.DashboardDir;
    public event Action? Changed;
    public IReadOnlyList<DashboardSource> Sources { get { lock (_gate) return _state.Sources.ToList(); } }

    private void Seed()
    {
        if (Directory.EnumerateFileSystemEntries(Root).Any()) return;
        foreach (var name in new[] { "index.html", "style.css", "app.js" })
        {
            using var s = typeof(DashboardService).Assembly.GetManifestResourceStream("Avamux.Core.Web.dashboard." + name)!;
            using var f = File.Create(Path.Combine(Root, name));
            s.CopyTo(f);
        }
    }

    public DashboardSource AddSource(string url, string? type = null, bool enabled = true)
    {
        type = (type ?? (url.Split('?')[0].EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "js" : "css")).ToLowerInvariant();
        if (type is not ("css" or "js")) throw new ArgumentException("Source type must be css or js.");
        DashboardSource src;
        lock (_gate)
        {
            src = new DashboardSource { Id = Watching.WatchService.NewId(_state.Sources.Select(s => s.Id)), Url = url.Trim(), Type = type, Enabled = enabled };
            _state.Sources.Add(src);
        }
        Save();
        return src;
    }

    public DashboardSource GetSource(string idOrUrl)
    {
        lock (_gate)
            return _state.Sources.FirstOrDefault(s => s.Id == idOrUrl || s.Url == idOrUrl) ?? throw new KeyNotFoundException($"No dashboard source '{idOrUrl}'.");
    }

    public void SetEnabled(string idOrUrl, bool enabled) { GetSource(idOrUrl).Enabled = enabled; Save(); }

    public void RemoveSource(string idOrUrl)
    {
        var s = GetSource(idOrUrl);
        lock (_gate) _state.Sources.Remove(s);
        Save();
    }

    public void Save()
    {
        lock (_gate) JsonUtil.SaveFile(_app.Paths.StateFile("dashboard"), _state);
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Files() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(f => PathSafety.Relative(Root, f)).OrderBy(f => f).ToList();

    private static bool IsRemote(string url) => url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("//");

    private string InjectionHtml()
    {
        var sb = new StringBuilder("\n<script src=\"/dashboard/__avamux.js\"></script>\n");
        foreach (var s in Sources.Where(s => s.Enabled))
        {
            var href = IsRemote(s.Url) ? s.Url : Path.IsPathRooted(s.Url) ? "/dashboard/__source/" + s.Id : s.Url;
            href = System.Net.WebUtility.HtmlEncode(href);
            sb.Append(s.Type == "js" ? $"<script src=\"{href}\"></script>\n" : $"<link rel=\"stylesheet\" href=\"{href}\">\n");
        }
        return sb.ToString();
    }

    public IResult Serve(HttpContext ctx, string? path)
    {
        path = string.IsNullOrEmpty(path) ? "index.html" : path;
        if (path == "__avamux.js") return Results.Content(ApiScript(), "text/javascript; charset=utf-8");
        if (path.StartsWith("__source/"))
        {
            var src = Sources.FirstOrDefault(s => s.Id == path["__source/".Length..]);
            return src is not null && File.Exists(src.Url) ? Results.File(src.Url, src.Type == "js" ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8") : Results.NotFound();
        }
        string full;
        try { full = PathSafety.Combine(Root, path); } catch { return Results.NotFound(); }
        if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
        if (!File.Exists(full)) return Results.NotFound();
        if (full.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            var html = File.ReadAllText(full);
            var inject = InjectionHtml();
            var i = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = i >= 0 ? html.Insert(i, inject) : inject + html;
            return Results.Content(html, "text/html; charset=utf-8");
        }
        return Results.File(full, Web.WebServer.MimeOf(full));
    }

    /// <summary>window.avamux for dashboard pages: run any avamux command, same surface as the CLI.</summary>
    private string ApiScript() => $$"""
        (function () {
          const token = "{{_app.Web.Token}}";
          async function call(path, init) {
            const r = await fetch(path, Object.assign({ headers: { 'Content-Type': 'application/json', 'X-Avamux-Token': token } }, init));
            return r.json();
          }
          window.avamux = {
            /** Runs an avamux command, e.g. avamux.command('terminal new', { name: 'build' }). Resolves to its result. */
            async command(name, args) {
              const r = await call('/api/command', { method: 'POST', body: JSON.stringify({ command: name, args: args || {} }) });
              if (!r.ok) throw new Error(r.error || 'avamux command failed');
              return r.result;
            },
            /** Lists every available command with its parameters. */
            commands() { return call('/api/commands'); },
            allowed: {{(_app.Config.Settings.Dashboard.AllowAppControl ? "true" : "false")}}
          };
          window.dispatchEvent(new Event('avamux-ready'));
        })();
        """;
}
