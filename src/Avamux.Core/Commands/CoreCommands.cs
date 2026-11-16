using System.Text.Json.Nodes;
using Avamux.Core.Config;
using Avamux.Core.Logging;
using Avamux.Core.Terminal;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    public static readonly string[] Pages = ["terminals", "search", "settings", "watching", "events", "dashboard", "logs", "wiki", "repositories", "interactive", "caffeine", "system", "chat", "workloads"];

    private static object DiskJson(SystemInfo.DiskScan d) => new
    {
        id = d.Id, title = d.Title, roots = d.Roots, state = d.State.ToString().ToLowerInvariant(), bytes = d.Size, files = d.Files, folders = d.Folders,
        fraction = d.Fraction, elapsedSeconds = (int)d.Elapsed.TotalSeconds, started = d.Started, finished = d.Finished, current = d.CurrentPath,
    };

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
        Add("theme list", "List the available themes (built-in and theme files).", [], _ => J(Theming.Themes.All.Select(t => new
        {
            id = t.Id, name = t.Name, description = t.Description, light = t.Light, file = t.File,
            current = string.Equals(t.Id, Theming.Themes.Find(s.Config.Settings.Theme.Name).Id, StringComparison.OrdinalIgnoreCase),
        })));
        Add("theme set", "Switch theme (same as settings set theme.name).", [P("name", "Theme id or name.", true), O("scope", "Which file: home (default), home-local, working, working-local.")], a =>
        {
            var theme = FindTheme(a.Require("name"));
            s.Config.Set("theme.name", JsonValue.Create(theme.Id), ParseScope(a.Str("scope")));
            return theme.Id;
        });
        Add("theme show", "Print a theme as YAML or JSON (every colour), ready to save as a theme file.",
            [P("name", "Theme id or name. Default: the current theme."), O("format", "yaml (default) or json."), O("file", "Write it to this file instead of printing it.")], a =>
        {
            var theme = a.Str("name") is { Length: > 0 } n ? FindTheme(n) : Theming.Themes.Find(s.Config.Settings.Theme.Name);
            var format = a.Str("format") ?? (a.Str("file") is { } f ? Theming.ThemeFile.FormatOf(f) : "yaml");
            if (format is not ("yaml" or "json")) throw new ArgumentException("format must be yaml or json.");
            var text = theme.File is { } source && Theming.ThemeFile.FormatOf(source) == format ? File.ReadAllText(source) : Theming.ThemeFile.Write(theme, format);
            if (a.Str("file") is not { } file) return text;
            File.WriteAllText(file, text);
            return file;
        });
        Add("theme validate", "Check a theme file and report every problem in it.", [P("file", "Path to a .yaml, .yml or .json file.", true)], a =>
        {
            var file = a.Require("file");
            try
            {
                var t = Theming.ThemeFile.Parse(File.ReadAllText(file), Theming.ThemeStore.IdOf(file), Theming.ThemeFile.FormatOf(file));
                return J(new { ok = true, id = t.Id, name = t.Name, light = t.Light });
            }
            catch (Theming.ThemeFileException ex) { return J(new { ok = false, problems = ex.Problems }); }
        });
        Add("theme import", "Copy a theme file into the themes folder (after checking it).",
            [P("file", "Path to a .yaml, .yml or .json file.", true), O("id", "Theme id to save it as (default: the file name)."), F("use", "Switch to it afterwards.")], a =>
        {
            var theme = s.ThemeFiles.Import(a.Require("file"), a.Str("id"));
            if (a.Flag("use")) s.Config.Set("theme.name", JsonValue.Create(theme.Id), ConfigScope.Home);
            return J(new { id = theme.Id, name = theme.Name, file = theme.File });
        });
        Add("theme delete", "Delete a theme file. A file that replaced a built-in theme brings the built-in one back.", [P("name", "Theme id or name.", true)], a =>
        {
            var name = a.Require("name");
            var theme = s.ThemeFiles.Themes.FirstOrDefault(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(Theming.Themes.TryFind(name) is { } builtIn ? $"'{builtIn.Name}' is built in; only theme files can be deleted." : $"There is no theme file called '{name}'.");
            s.ThemeFiles.Delete(theme.Id);
            return theme.Id;
        });
        Add("theme folder", "Print the folder theme files are read from.", [F("open", "Open it in the file manager.")], a =>
        {
            if (a.Flag("open")) Shell.Open(s.ThemeFiles.Dir);
            return s.ThemeFiles.Dir;
        });
        Add("theme reload", "Re-read the theme files and report any that could not be read.", [], _ =>
        {
            s.ThemeFiles.Load();
            return J(new { themes = s.ThemeFiles.Themes.Select(t => t.Id), problems = s.ThemeFiles.Problems.Select(p => new { file = p.File, problems = p.Problems }) });
        });
        Add("theme edit", "Open the theme editor.", [P("name", "Theme to open. Default: the current theme.")], a =>
        {
            var name = a.Str("name") is { Length: > 0 } n ? FindTheme(n).Id : null;
            s.Host.OpenThemeEditor(name);
            return name ?? Theming.Themes.Find(s.Config.Settings.Theme.Name).Id;
        });
        Add("theme help", "Describe the theme file format.", [], _ => Theming.ThemeFile.Reference);
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

    private static JsonNode CaffeineJson(AppServices s)
    {
        var st = s.Caffeine.Status;
        return new JsonObject
        {
            ["mode"] = st.Mode.ToString().ToLowerInvariant(), ["holding"] = st.Holding, ["until"] = st.Until?.ToString("s"),
            ["busy"] = new JsonArray(st.BusyTerminals.Select(b => (JsonNode)b).ToArray()), ["problem"] = st.Problem, ["status"] = st.Describe(DateTime.Now),
        };
    }

    private static Theming.ThemeDef FindTheme(string name) => Theming.Themes.TryFind(name)
        ?? throw new ArgumentException($"Unknown theme '{name}'. Themes: {string.Join(", ", Theming.Themes.All.Select(t => t.Id))}.");

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
        Add("app boss", "Show the boss screen (or hide it again).", [F("off", "Hide it."), F("on", "Show it.")], a =>
        {
            s.Host.BossScreen(a.Flag("off") ? false : a.Flag("on") ? true : null);
            return "ok";
        });
        Add("system status", "CPU, memory, GPUs, network and the mounted volumes, best effort.", [], async _ =>
        {
            var monitor = new SystemInfo.SystemMonitor();
            monitor.Sample();
            await Task.Delay(500);
            var snap = monitor.Sample();
            return J(new { snapshot = snap, volumes = SystemInfo.Volumes.List() });
        });
        Add("disk scans", "The disk scans and how far each got.", [], _ => J(s.Disks.Scans.Select(DiskJson)));
        Add("disk scan", "Map the space taken on a volume (give its mount point) or in a folder. Runs in the background.",
            [P("target", "A mount point or folder.", true), F("open", "Show the map window."), F("again", "Start over even if it was scanned before.")], a =>
        {
            var target = Path.GetFullPath(a.Require("target"));
            var volume = SystemInfo.Volumes.List().FirstOrDefault(v => v.MountPoints.Contains(target.Length > 1 ? target.TrimEnd('/') : target));
            var scan = volume is not null ? s.Disks.ForVolume(volume) : Directory.Exists(target) ? s.Disks.ForFolder(target) : throw new ArgumentException($"No such folder: {target}");
            if (a.Flag("again") || scan.State == SystemInfo.DiskScanState.New) scan.Start();
            else if (scan.State == SystemInfo.DiskScanState.Stopped || scan.State == SystemInfo.DiskScanState.Paused) scan.Resume();
            if (a.Flag("open")) s.Host.OpenDiskMap(scan.Id);
            return J(DiskJson(scan));
        });
        foreach (var (verb, help) in new[] { ("pause", "Pause a running disk scan."), ("resume", "Carry on a paused or stopped disk scan (loading it if need be)."), ("stop", "Stop a disk scan, keeping what it found."), ("forget", "Delete a disk scan.") })
            Add("disk " + verb, help, [P("id", "Scan id (from disk scans) or its title.", true)], a =>
            {
                var key = a.Require("id");
                var scan = s.Disks.Find(key) ?? s.Disks.Scans.FirstOrDefault(d => d.Title.Equals(key, StringComparison.OrdinalIgnoreCase) || d.Roots.Contains(key))
                    ?? throw new ArgumentException($"No disk scan '{key}'.");
                switch (verb)
                {
                    case "pause": scan.Pause(); break;
                    case "stop": scan.Stop(wait: true); break;
                    case "forget": s.Disks.Delete(scan); break;
                    default:
                        s.Disks.EnsureLoaded(scan);
                        scan.Resume();
                        break;
                }
                return J(DiskJson(scan));
            });
        Add("caffeine status", "Is the computer being kept awake, and why.", [], _ => CaffeineJson(s));
        Add("caffeine on", "Keep the computer awake: until turned off (default), for a while, until a time, or while terminals are busy.",
            [O("for", "How long: 30m, 1h30m, 2h …"), O("until", "Until a time: 17:30, tomorrow 09:00, +45m …"), F("terminals", "Only while a terminal is running something (and a few minutes after).")], a =>
        {
            if (a.Flag("terminals")) s.Caffeine.WhileTerminalsBusy();
            else if (a.Str("for") is { } d) s.Caffeine.For(TimeParsing.ParseDuration(d));
            else if (a.Str("until") is { } u)
            {
                var when = TimeParsing.ParseDateTime(u);
                // A bare clock time that has gone by today means tomorrow.
                if (when <= DateTime.Now && when.Date == DateTime.Today && !u.Contains("today", StringComparison.OrdinalIgnoreCase)) when = when.AddDays(1);
                s.Caffeine.UntilTime(when);
            }
            else s.Caffeine.Indefinite();
            return CaffeineJson(s);
        });
        Add("caffeine off", "Let the computer sleep as usual again.", [], _ => { s.Caffeine.Off(); return CaffeineJson(s); });
        Add("app screenshot", "Save a picture of the main window (or another avamux window) as a PNG.", [P("file", "Where to save it.", true), O("window", "Part of the title of the window to take instead.")],
            async a => J(new { file = Path.GetFullPath(a.Require("file")), window = await s.Host.ScreenshotAsync(Path.GetFullPath(a.Require("file")), a.Str("window")) }));
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
