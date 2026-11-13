using System.Text.Json.Nodes;
using Avamux.Core.Config;
using Avamux.Core.Logging;
using Avamux.Core.Terminal;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    public static readonly string[] Pages = ["terminals", "search", "settings", "watching", "events", "dashboard", "logs", "wiki", "repositories", "chat", "workloads"];

    private void AddSearchCommands(AppServices s)
    {
        Add("search", $"Search everything: {string.Join(", ", Search.SearchService.Sources.Select(x => x.Key))}.",
            [Rest("query", "Text to find.", true), M("source", "Only search this source. May be repeated."), F("open", "Show the results in the Search tab.")], async a =>
        {
            var query = a.Require("query");
            if (a.Flag("open")) { s.Host.Navigate("search"); s.Host.Search(query); }
            var hits = await s.Search.SearchAsync(query, a.List("source"));
            return J(hits.Select(h => new { source = h.Source, title = h.Title, detail = h.Detail, target = h.Target, extra = h.Extra, line = h.Line == 0 ? (int?)null : h.Line }));
        });
        Add("theme list", "List the available themes.", [], _ => J(Theming.Themes.All.Select(t => new { id = t.Id, name = t.Name, description = t.Description })));
        Add("theme set", "Switch theme (same as settings set theme.name).", [P("name", "Theme id or name.", true), O("scope", "Which file: home (default), home-local, working, working-local.")], a =>
        {
            var name = a.Require("name");
            var theme = Theming.Themes.All.FirstOrDefault(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown theme '{name}'. Themes: {string.Join(", ", Theming.Themes.All.Select(t => t.Id))}.");
            s.Config.Set("theme.name", JsonValue.Create(theme.Id), ParseScope(a.Str("scope")));
            return theme.Id;
        });
        Add("theme sound", "Play one of the theme's sounds.", [P("event", "event-start, event-end, timer, watch-changed, pull-request or agent-waiting.", true)], a =>
        {
            var e = a.Require("event").Replace("-", "").ToLowerInvariant() switch
            {
                "eventstart" => Theming.SoundEvent.EventStart, "eventend" => Theming.SoundEvent.EventEnd, "timer" => Theming.SoundEvent.Timer,
                "watchchanged" or "watch" => Theming.SoundEvent.WatchChanged, "pullrequest" or "pr" => Theming.SoundEvent.PullRequest, "agentwaiting" or "agent" => Theming.SoundEvent.AgentWaiting,
                _ => throw new ArgumentException("Unknown sound. Use event-start, event-end, timer, watch-changed, pull-request or agent-waiting."),
            };
            s.Sounds.Play(e, force: true);
            return s.Sounds.Resolve(e);
        });
    }

    private void AddAppCommands(AppServices s)
    {
        Add("app ping", "Check that avamux is running.", [], _ => "pong");
        Add("app status", "Show the running instance: version, uptime, current page, counts.", [], _ => new JsonObject
        {
            ["version"] = typeof(AppServices).Assembly.GetName().Version?.ToString(3),
            ["pid"] = Environment.ProcessId,
            ["started"] = s.Started.ToString("yyyy-MM-dd HH:mm:ss"),
            ["page"] = s.Host.CurrentPage,
            ["terminals"] = s.Terminals.Sessions.Count,
            ["watches"] = s.Watches.Items.Count,
            ["events"] = s.Events.Items.Count,
            ["repositories"] = s.Repos.Items.Count,
            ["webServer"] = s.Web.BaseUrl,
        });
        Add("app nav", $"Switch the window to a page: {string.Join(", ", Pages)}, or the name of a custom page.", [P("page", "Page name.", true)], a =>
        {
            // A custom page (see `avamux page list`) is found by its exact name or key; built-in pages by a prefix.
            if (s.Nav.Find(a.Require("page")) is { Enabled: true } custom) { s.Host.Navigate(custom.Key); return custom.Name; }
            var page = Pages.FirstOrDefault(p => p.StartsWith(a.Require("page"), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown page '{a.Str("page")}'. Pages: {string.Join(", ", Pages.Concat(s.Nav.Pages.Where(p => p.Enabled).Select(p => p.Name)))}.");
            s.Host.Navigate(page);
            return page;
        });
        Add("app activate", "Bring the avamux window to the front.", [], _ => { s.Host.Activate(); return "ok"; });
        Add("app quit", "Close avamux.", [], _ => { s.Host.Quit(); return "bye"; });
        Add("app paths", "Show where avamux keeps its data and configuration.", [], _ => new JsonObject
        {
            ["data"] = s.Paths.DataDir, ["logs"] = s.Paths.LogsDir, ["wiki"] = s.Paths.WikiDir, ["dashboard"] = s.Paths.DashboardDir,
            ["plugins"] = s.Paths.PluginsDir, ["socket"] = s.Paths.SocketPath,
            ["homeConfig"] = s.Config.FileFor(ConfigScope.Home), ["workingConfig"] = s.Config.FileFor(ConfigScope.Working),
        });
        Add("app notify", "Show a notification inside avamux.", [P("title", "Title.", true), Rest("message", "Message text.")], a =>
        {
            s.Host.Notify(a.Require("title"), a.Str("message") ?? "");
            return "ok";
        });
    }

    private void AddTerminalCommands(AppServices s)
    {
        Add("terminal list", "List terminal tabs.", [], _ => J(s.Terminals.Sessions.Select(t => t.Info())));
        Add("terminal new", "Open a terminal tab. With no command it starts your shell.",
            [O("name", "Tab name (otherwise the tab shows what is running)."), O("cwd", "Working directory."),
             F("background", "Do not switch to the new tab."), Rest("command", "Command and arguments to run instead of the shell.")], a =>
        {
            var cmd = a.List("command");
            var session = s.Terminals.Create(new TerminalStartOptions(cmd.Count > 0 ? cmd[0] : null, cmd.Skip(1).ToList(), a.Str("cwd"), a.Str("name")));
            if (!a.Flag("background")) s.Host.SelectTerminal(session.Id);
            return J(session.Info());
        });
        Add("terminal rename", "Rename a tab. An empty name goes back to showing what is running.",
            [P("id", "Terminal id or name.", true), Rest("name", "New name.")], a =>
        {
            s.Terminals.Rename(a.Require("id"), a.Str("name"));
            return J(s.Terminals.Get(a.Str("name") is { Length: > 0 } n ? n : a.Require("id")).Info());
        });
        Add("terminal close", "Close a tab and end what is running in it.", [P("id", "Terminal id or name.", true)], a =>
        {
            s.Terminals.Close(a.Require("id"));
            return "closed";
        });
        Add("terminal select", "Show a tab.", [P("id", "Terminal id or name.", true)], a =>
        {
            var t = s.Terminals.Get(a.Require("id"));
            s.Host.SelectTerminal(t.Id);
            return J(t.Info());
        });
        Add("terminal current", "Show which tab is selected.", [], _ =>
            s.Host.SelectedTerminal is { } id && s.Terminals.Find(id) is { } t ? J(t.Info()) : null);
        Add("terminal send", "Type text into a terminal. Escapes: \\n \\r \\t \\e \\xHH, and ^X for Ctrl+X when --keys is given.",
            [P("id", "Terminal id or name.", true), Rest("text", "Text to send.", true), F("enter", "Press Enter afterwards."),
             F("keys", "Interpret ^C style control keys and backslash escapes."), F("paste", "Send as a bracketed paste.")], a =>
        {
            var t = s.Terminals.Get(a.Require("id"));
            var text = a.Require("text");
            if (a.Flag("keys")) text = Unescape(text);
            if (a.Flag("paste")) text = "\x1b[200~" + text + "\x1b[201~";
            if (a.Flag("enter")) text += "\r";
            t.Write(text);
            return "sent";
        });
        Add("terminal read", "Print the text of a terminal (scrollback included) as the tab shows it.",
            [P("id", "Terminal id or name.", true), O("lines", "Only the last N lines."), F("screen", "Only the visible screen."), F("raw", "Raw output bytes instead of rendered text.")], async a =>
        {
            var t = s.Terminals.Get(a.Require("id"));
            if (!a.Flag("raw") && await s.Web.RequestTextAsync(t.Id, a.Int("lines"), a.Flag("screen")) is { } rendered) return rendered;
            var text = System.Text.Encoding.UTF8.GetString(t.Snapshot());
            if (a.Flag("raw")) return text;
            text = TerminalManager.StripAnsi(text);
            if (a.Int("lines") is { } n) text = string.Join('\n', text.Split('\n').TakeLast(n));
            return text;
        });
        Add("terminal resize", "Set the size of a terminal that has no visible tab (the tab view otherwise controls it).",
            [P("id", "Terminal id or name.", true), P("cols", "Columns.", true), P("rows", "Rows.", true)], a =>
        {
            s.Terminals.Get(a.Require("id")).Resize(a.Int("cols")!.Value, a.Int("rows")!.Value);
            return "ok";
        });
        Add("terminal move", "Reorder a tab.", [P("id", "Terminal id or name.", true), P("index", "New position, starting at 0.", true)], a =>
        {
            s.Terminals.Move(a.Require("id"), a.Int("index")!.Value);
            return J(s.Terminals.Sessions.Select(t => t.Info()));
        });
    }

    /// <summary>\n \r \t \e \\ \xHH and ^X (Ctrl+X); ^^ is a literal caret.</summary>
    public static string Unescape(string text)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var n = text[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'e': sb.Append('\x1b'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'x' when i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]):
                        sb.Append((char)Convert.ToInt32(text.Substring(i + 1, 2), 16)); i += 2; break;
                    default: sb.Append('\\').Append(n); break;
                }
            }
            else if (c == '^' && i + 1 < text.Length)
            {
                var n = char.ToUpperInvariant(text[++i]);
                if (n == '^') sb.Append('^');
                else if (n is >= '@' and <= '_') sb.Append((char)(n - '@'));
                else if (n == '?') sb.Append('\x7f');
                else sb.Append('^').Append(text[i]);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static ConfigScope ParseScope(string? text) => (text ?? "home").ToLowerInvariant().Replace("-", "").Replace(".", "") switch
    {
        "home" or "user" => ConfigScope.Home,
        "homelocal" or "userlocal" => ConfigScope.HomeLocal,
        "working" or "cwd" or "folder" or "project" => ConfigScope.Working,
        "workinglocal" or "cwdlocal" or "local" or "projectlocal" => ConfigScope.WorkingLocal,
        _ => throw new ArgumentException("Scope must be one of: home, home-local, working, working-local."),
    };

    private void AddSettingsCommands(AppServices s)
    {
        const string scopeHelp = "Which file: home (default), home-local, working, working-local.";
        Add("settings list", "List every setting, its value and which file it comes from.", [], _ =>
            J(s.Config.Describe().Select(v => new { key = v.Path, value = JsonUtil.Line(v.Value), source = v.Source.ToString() })));
        Add("settings get", "Show one setting.", [P("key", "Setting name, e.g. terminal.scrollback.", true)], a =>
        {
            var key = ConfigService.Canonical(a.Require("key")) ?? throw new ArgumentException($"Unknown setting '{a.Str("key")}'. See `avamux settings list`.");
            return s.Config.Get(key);
        });
        Add("settings set", "Change a setting.", [P("key", "Setting name.", true), Rest("value", "New value.", true), O("scope", scopeHelp)], a =>
        {
            var key = ConfigService.Canonical(a.Require("key")) ?? throw new ArgumentException($"Unknown setting '{a.Str("key")}'. See `avamux settings list`.");
            s.Config.Set(key, ConfigService.Coerce(key, a.Require("value")), ParseScope(a.Str("scope")));
            return s.Config.Get(key);
        });
        Add("settings unset", "Remove a setting from a file so the next layer (or the default) applies.", [P("key", "Setting name.", true), O("scope", scopeHelp)], a =>
        {
            s.Config.Set(a.Require("key"), null, ParseScope(a.Str("scope")));
            return s.Config.Get(ConfigService.Canonical(a.Require("key"))!);
        });
        Add("settings files", "Show the configuration files in order of precedence (last wins).", [], _ =>
            J(ConfigService.FileScopes.Select(scope => new { scope = scope.ToString(), file = s.Config.FileFor(scope), exists = File.Exists(s.Config.FileFor(scope)) })));
        Add("settings reload", "Re-read the configuration files.", [], _ => { s.Config.Reload(); return "reloaded"; });
    }

    private void AddLogCommands(AppServices s)
    {
        Add("logs list", "List log files: app logs, crash reports, saved terminal history, script output.",
            [O("category", "app, crashes, terminals or scripts.")], a =>
            J(s.Logs.List(a.Str("category")).Select(f => new { category = f.Category, name = f.Name, sizeKb = Math.Round(f.Size / 1024.0, 1), modified = f.Modified.ToString("yyyy-MM-dd HH:mm:ss") })));
        Add("logs show", "Print a log file.", [P("name", "File name (see `logs list`).", true), O("lines", "Only the last N lines.")], a =>
            s.Logs.Read(a.Require("name"), a.Int("lines")));
        Add("logs tail", "Print the most recent app log lines.", [O("lines", "How many (default 40).")], a =>
            string.Join('\n', Log.Recent().TakeLast(a.Int("lines") ?? 40)));
        Add("logs delete", "Delete a log file.", [P("name", "File name.", true)], a => { s.Logs.Delete(a.Require("name")); return "deleted"; });
        Add("logs clear", "Delete all log files, or one category.", [O("category", "app, crashes, terminals or scripts.")], a =>
            $"deleted {s.Logs.Clear(a.Str("category"))} file(s)");
        Add("logs usage", "Show how much space logs use against the retention limits.", [], _ =>
        {
            var l = s.Config.Settings.Logs;
            return new JsonObject
            {
                ["files"] = s.Logs.List().Count, ["sizeMb"] = Math.Round(s.Logs.TotalSize() / 1048576.0, 2),
                ["maxTotalSizeMb"] = l.MaxTotalSizeMb, ["maxAgeDays"] = l.MaxAgeDays, ["maxFileCount"] = l.MaxFileCount,
            };
        });
        Add("logs prune", "Apply the retention limits now.", [], _ => $"removed {s.Logs.ApplyRetention()} file(s)");
        Add("logs write", "Add a line to the app log.", [Rest("message", "Text.", true), O("level", "debug, info, warn or error.")], a =>
        {
            Log.Write(Enum.TryParse<LogLevel>(a.Str("level"), true, out var l) ? l : LogLevel.Info, "cli", a.Require("message"));
            return "ok";
        });
    }
}
