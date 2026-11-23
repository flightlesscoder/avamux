using System.Text.Json.Nodes;
using Avamux.Core.Events;
using Avamux.Core.Terminal;
using Avamux.Core.Watching;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    private static JsonNode WatchJson(WatchItem w, bool full = false)
    {
        var o = new JsonObject
        {
            ["id"] = w.Id, ["name"] = w.Name, ["kind"] = w.Kind, ["status"] = w.Status, ["summary"] = w.Summary,
            ["link"] = w.EffectiveLink, ["enabled"] = w.Enabled, ["intervalMinutes"] = w.IntervalMinutes,
            ["lastChecked"] = w.LastChecked?.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        if (full)
        {
            o["settings"] = JsonUtil.ToNode(w.Settings);
            o["lastChanged"] = w.LastChanged?.ToString("yyyy-MM-dd HH:mm:ss");
            o["items"] = JsonUtil.ToNode(w.LastResult?.Items ?? []);
        }
        return o;
    }

    private static void ApplySettings(Dictionary<string, string> target, IEnumerable<string> pairs)
    {
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"Expected key=value, got '{pair}'.");
            var (k, v) = (pair[..eq].Trim(), pair[(eq + 1)..]);
            if (v.Length == 0) target.Remove(k); else target[k] = v;
        }
    }

    private void AddWatchCommands(AppServices s)
    {
        CommandParam[] edit =
        [
            O("kind", "Watch kind (see `watch kinds`)."), O("interval", "Minutes between checks (default: watching.defaultIntervalMinutes)."),
            O("link", "Link to open for this watch."), M("set", "Provider setting as key=value, e.g. --set repo=owner/name."),
        ];
        Add("watch list", "List watches with their latest status.", [], _ => new JsonArray(s.Watches.Items.Select(w => WatchJson(w)).ToArray()));
        Add("watch show", "Show one watch in full, including the items of its last check.", [P("id", "Watch id or name.", true)], a => WatchJson(s.Watches.Get(a.Require("id")), true));
        Add("watch kinds", "List the kinds of watch available (built-in and plugins) and their settings.", [], _ =>
        {
            var arr = new JsonArray();
            foreach (var p in s.Watches.Providers)
                arr.Add(new JsonObject { ["kind"] = p.Kind, ["description"] = p.Description, ["settings"] = string.Join(", ", p.Settings.Keys) });
            arr.Add(new JsonObject { ["kind"] = "plugin", ["description"] = "Load a provider from a .NET DLL that implements Avamux.Plugins.IWatchProvider.", ["settings"] = "assembly, type" });
            return arr;
        });
        Add("watch kind", "Describe the settings of one watch kind.", [P("kind", "Kind name.", true)], a =>
        {
            var p = s.Watches.Providers.FirstOrDefault(p => string.Equals(p.Kind, a.Require("kind"), StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No watch kind '{a.Str("kind")}'.");
            return J(p.Settings.Select(kv => new { setting = kv.Key, help = kv.Value }));
        });
        Add("watch add", "Add a watch.", [P("name", "Name.", true), .. edit, F("disabled", "Create it disabled."), F("no-check", "Do not check it immediately.")], async a =>
        {
            var item = new WatchItem { Name = a.Require("name"), Kind = a.Str("kind") ?? "command", IntervalMinutes = a.Num("interval"), Link = a.Str("link"), Enabled = !a.Flag("disabled") };
            if (!s.Watches.Kinds.Contains(item.Kind, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown watch kind '{item.Kind}'. Known kinds: {string.Join(", ", s.Watches.Kinds)}.");
            ApplySettings(item.Settings, a.List("set"));
            s.Watches.Add(item);
            if (item.Enabled && !a.Flag("no-check")) await s.Watches.CheckAsync(item);
            return WatchJson(item, true);
        });
        Add("watch edit", "Change a watch.", [P("id", "Watch id or name.", true), O("name", "New name."), .. edit], a =>
        {
            var item = s.Watches.Get(a.Require("id"));
            if (a.Str("name") is { } n) item.Name = n;
            if (a.Str("kind") is { } k) item.Kind = k;
            if (a.Has("interval")) item.IntervalMinutes = a.Str("interval") is "" or "default" ? null : a.Num("interval");
            if (a.Has("link")) item.Link = a.Str("link");
            ApplySettings(item.Settings, a.List("set"));
            s.Watches.Save();
            return WatchJson(item, true);
        });
        Add("watch remove", "Delete a watch.", [P("id", "Watch id or name.", true)], a => { s.Watches.Remove(a.Require("id")); return "removed"; });
        Add("watch enable", "Enable a watch.", [P("id", "Watch id or name.", true)], a => { s.Watches.Get(a.Require("id")).Enabled = true; s.Watches.Save(); return "enabled"; });
        Add("watch disable", "Disable a watch.", [P("id", "Watch id or name.", true)], a => { s.Watches.Get(a.Require("id")).Enabled = false; s.Watches.Save(); return "disabled"; });
        Add("watch check", "Check one watch now, or all of them.", [P("id", "Watch id or name.")], async a =>
        {
            if (a.Str("id") is { } id) return WatchJson(await s.Watches.CheckAsync(s.Watches.Get(id)), true);
            await s.Watches.CheckAllAsync();
            return new JsonArray(s.Watches.Items.Select(w => WatchJson(w)).ToArray());
        });
        Add("watch open", "Open a watch's link in the browser.", [P("id", "Watch id or name.", true)], a =>
        {
            var link = s.Watches.Get(a.Require("id")).EffectiveLink ?? throw new InvalidOperationException("This watch has no link.");
            Shell.Open(link);
            return link;
        });
    }

    private JsonNode EventJson(AppServices s, EventItem e)
    {
        var next = s.Events.NextOccurrence(e);
        return new JsonObject
        {
            ["id"] = e.Id, ["title"] = e.Title, ["kind"] = e.Kind.ToString().ToLowerInvariant(),
            ["start"] = e.Start.ToString("yyyy-MM-dd HH:mm:ss"), ["end"] = e.End?.ToString("yyyy-MM-dd HH:mm:ss"),
            ["allDay"] = e.AllDay, ["repeat"] = RecurrenceCalculator.Describe(e.Recurrence),
            ["next"] = next?.Start.ToString("yyyy-MM-dd HH:mm:ss"), ["enabled"] = e.Enabled, ["completed"] = e.Completed,
            ["onStart"] = e.OnStartScript, ["onEnd"] = e.OnEndScript, ["lastRun"] = e.LastRunSummary, ["notes"] = e.Notes,
            ["location"] = e.Location, ["color"] = e.Color,
            ["skipped"] = e.SkippedDates.Count == 0 ? null : new JsonArray(e.SkippedDates.Select(d => (JsonNode)d.ToString("yyyy-MM-dd")).ToArray()),
            ["attachments"] = e.Attachments.Count == 0 ? null : new JsonArray(e.Attachments.Select(n => (JsonNode)s.Events.AttachmentPath(e, n)).ToArray()),
        };
    }

    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday, ["thu"] = DayOfWeek.Thursday,
        ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday, ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday, ["sun"] = DayOfWeek.Sunday,
    };

    public static List<DayOfWeek> ParseDays(string text) => text.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries).Select(d =>
        DayNames.TryGetValue(d, out var v) ? v
        : Enum.TryParse<DayOfWeek>(d, true, out var full) ? full
        : d.Equals("weekdays", StringComparison.OrdinalIgnoreCase) ? (DayOfWeek)(-1)
        : throw new ArgumentException($"Unknown day '{d}'. Use mon, tue, wed, thu, fri, sat, sun.")).SelectMany(d =>
        (int)d == -1 ? new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday } : [d]).Distinct().ToList();

    /// <summary>Dates separated by spaces or commas, each anything <see cref="TimeParsing.ParseDateTime"/> understands.</summary>
    private static List<DateOnly> ParseSkipDates(IEnumerable<string> texts) =>
        texts.SelectMany(t => t.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)).Select(t => DateOnly.FromDateTime(TimeParsing.ParseDateTime(t))).ToList();

    private static void ApplyEventArgs(EventItem e, Args a)
    {
        if (a.Str("title") is { } t) e.Title = t;
        if (a.Str("kind") is { } k) e.Kind = Enum.TryParse<EventKind>(k, true, out var kind) ? kind : throw new ArgumentException("Kind must be calendar, task or timer.");
        if (a.Time("start") is { } start) e.Start = start;
        if (a.Has("end")) e.End = a.Str("end") is "" or "none" ? null : a.Time("end");
        if (a.Str("duration") is { } d) e.End = e.Start + TimeParsing.ParseDuration(d);
        if (a.Bool("all-day") is { } allDay)
        {
            e.AllDay = allDay;
            if (allDay) { e.Start = e.Start.Date; e.End = (e.End ?? e.Start).Date.AddDays(1).AddSeconds(-1); }
        }
        if (a.Str("repeat") is { } rep)
        {
            e.Recurrence.Freq = rep.ToLowerInvariant() switch
            {
                "none" or "once" or "never" => RecurrenceFreq.None,
                "interval" => RecurrenceFreq.Interval,
                "daily" or "day" => RecurrenceFreq.Daily,
                "weekly" or "week" => RecurrenceFreq.Weekly,
                "monthly" or "month" => RecurrenceFreq.Monthly,
                "yearly" or "year" or "annually" => RecurrenceFreq.Yearly,
                _ => throw new ArgumentException("Repeat must be none, interval, daily, weekly, monthly or yearly."),
            };
        }
        if (a.Str("every") is { } every)
        {
            if (e.Recurrence.Freq is RecurrenceFreq.None or RecurrenceFreq.Interval)
            {
                // "--every 15m" on its own means a fixed interval.
                e.Recurrence.Freq = RecurrenceFreq.Interval;
                e.Recurrence.IntervalMinutes = TimeParsing.ParseDuration(every).TotalMinutes;
            }
            else e.Recurrence.Interval = int.TryParse(every, out var n) && n > 0 ? n : throw new ArgumentException("--every must be a positive whole number for daily/weekly/monthly/yearly repeats.");
        }
        if (a.Str("days") is { } days)
        {
            e.Recurrence.Days = ParseDays(days);
            if (e.Recurrence.Freq == RecurrenceFreq.None) e.Recurrence.Freq = RecurrenceFreq.Weekly;
        }
        if (a.Has("until")) e.Recurrence.Until = a.Str("until") is "" or "none" ? null : a.Time("until");
        if (a.Has("count")) e.Recurrence.Count = a.Str("count") is "" or "none" ? null : a.Int("count");
        if (a.Has("on-start")) e.OnStartScript = a.Str("on-start");
        if (a.Has("on-end")) e.OnEndScript = a.Str("on-end");
        if (a.Has("notes")) e.Notes = a.Str("notes");
        if (a.Has("location")) e.Location = a.Str("location") is { Length: > 0 } loc ? loc : null;
        if (a.Has("color"))
            e.Color = a.Str("color") is not { Length: > 0 } c || c == "none" ? null
                : Theming.Rgb.TryParse(c, out var rgb) ? rgb.Hex : throw new ArgumentException("Colour must look like #rrggbb.");
        if (e.End is { } end && end < e.Start) throw new ArgumentException("The end is before the start.");
        e.Completed = false;
    }

    private void AddEventCommands(AppServices s)
    {
        CommandParam[] fields =
        [
            O("start", "When it starts: 2026-12-24T18:00, \"tomorrow 9:00\", 17:30, +90m."),
            O("end", "When it ends (may be days later)."), O("duration", "Length instead of --end, e.g. 45m or 3d."),
            F("all-day", "All-day event."),
            O("repeat", "none, interval, daily, weekly, monthly or yearly."),
            O("every", "Every N days/weeks/months/years, or a duration like 15m for interval repeats."),
            O("days", "Weekdays for weekly repeats: mon,tue,fri or weekdays."),
            O("until", "Stop repeating after this date."), O("count", "Stop after this many occurrences."),
            O("on-start", "Shell script to run when it starts / triggers."), O("on-end", "Shell script to run when it ends."),
            O("notes", "Description (markdown)."), O("location", "Where: an address, a link, any text."),
            O("color", "Calendar colour as #rrggbb, or none."),
        ];
        var attach = M("attach", "File to attach (copied into avamux's data folder). May be repeated.");
        Add("event list", "List timers, scheduled tasks and calendar events.", [F("all", "Include completed ones.")], a =>
            new JsonArray(s.Events.Items.Where(e => a.Flag("all") || !e.Completed).OrderBy(e => s.Events.NextOccurrence(e)?.Start ?? DateTime.MaxValue).Select(e => EventJson(s, e)).ToArray()));
        Add("event show", "Show one event.", [P("id", "Event id or title.", true)], a => EventJson(s, s.Events.Get(a.Require("id"))));
        Add("event add", "Add a calendar event.", [P("title", "Title.", true), O("kind", "calendar (default), task or timer."), .. fields, attach], a =>
        {
            var e = new EventItem { Title = a.Require("title"), Start = DateTime.Now.AddMinutes(1) };
            ApplyEventArgs(e, a);
            s.Events.Add(e);
            foreach (var f in a.List("attach")) s.Events.Attach(e, f);
            if (e.Attachments.Count > 0) s.Events.Save();
            return EventJson(s, e);
        });
        Add("event task", "Schedule a script: once at a time, or repeatedly.",
            [P("title", "Title.", true), O("run", "Shell script to run.", true), O("at", "When to run (first time)."), .. fields.Where(f => f.Name is "repeat" or "every" or "days" or "until" or "count" or "notes" or "color")], a =>
        {
            var e = new EventItem { Title = a.Require("title"), Kind = EventKind.Task, OnStartScript = a.Require("run") };
            ApplyEventArgs(e, a);
            e.Start = a.Time("at") ?? (e.Recurrence.Freq == RecurrenceFreq.Interval ? DateTime.Now.AddMinutes(e.Recurrence.IntervalMinutes) : DateTime.Now.AddMinutes(1));
            return EventJson(s, s.Events.Add(e));
        });
        Add("event timer", "Start a countdown timer.", [P("duration", "How long: 90s, 5m, 1h30m.", true), Rest("title", "Title."), O("run", "Shell script to run when it goes off.")], a =>
        {
            var d = TimeParsing.ParseDuration(a.Require("duration"));
            return EventJson(s, s.Events.AddTimer(a.Str("title") is { Length: > 0 } t ? t : $"Timer {RecurrenceCalculator.FormatMinutes(d.TotalMinutes)}", d, a.Str("run")));
        });
        Add("event edit", "Change an event.", [P("id", "Event id or title.", true), O("title", "New title."), O("kind", "calendar, task or timer."), .. fields, attach, M("detach", "Attachment file name to remove."),
            M("skip", "Date of one occurrence to skip (see event skip). May be repeated."), M("unskip", "Date of a skipped occurrence to bring back. May be repeated.")], a =>
        {
            var e = s.Events.Get(a.Require("id"));
            ApplyEventArgs(e, a);
            s.Events.Skip(e, ParseSkipDates(a.List("skip")));
            s.Events.Unskip(e, ParseSkipDates(a.List("unskip")));
            foreach (var f in a.List("attach")) s.Events.Attach(e, f);
            foreach (var f in a.List("detach")) s.Events.Detach(e, Path.GetFileName(f));
            s.Events.Save();
            return EventJson(s, e);
        });
        Add("event skip", "Skip single occurrences of a repeating event, such as a holiday.",
            [P("id", "Event id or title.", true), Rest("dates", "Dates to skip: 2026-12-25, tomorrow. The event must occur on each.", true)], a =>
        {
            var e = s.Events.Get(a.Require("id"));
            s.Events.Skip(e, ParseSkipDates(a.List("dates")));
            s.Events.Save();
            return EventJson(s, e);
        });
        Add("event unskip", "Bring back skipped occurrences of a repeating event.",
            [P("id", "Event id or title.", true), Rest("dates", "Dates to bring back, as shown by event show.", true)], a =>
        {
            var e = s.Events.Get(a.Require("id"));
            s.Events.Unskip(e, ParseSkipDates(a.List("dates")));
            s.Events.Save();
            return EventJson(s, e);
        });
        Add("event remove", "Delete an event.", [P("id", "Event id or title.", true)], a => { s.Events.Remove(a.Require("id")); return "removed"; });
        Add("event enable", "Enable an event.", [P("id", "Event id or title.", true)], a => { s.Events.Get(a.Require("id")).Enabled = true; s.Events.Save(); return "enabled"; });
        Add("event disable", "Disable an event.", [P("id", "Event id or title.", true)], a => { s.Events.Get(a.Require("id")).Enabled = false; s.Events.Save(); return "disabled"; });
        Add("event agenda", "List what happens in the coming days.", [O("days", "How many days ahead (default 7)."), O("from", "Start date (default now).")], a =>
        {
            var from = a.Time("from") ?? DateTime.Now;
            return new JsonArray(s.Events.Agenda(from, from.AddDays(a.Num("days") ?? 7)).Where(x => x.Item.Enabled).Select(x => (JsonNode)new JsonObject
            {
                ["start"] = x.Occurrence.Start.ToString("yyyy-MM-ddTHH:mm:ss"), ["end"] = x.Occurrence.End?.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["title"] = x.Item.Title, ["kind"] = x.Item.Kind.ToString().ToLowerInvariant(), ["id"] = x.Item.Id,
            }).ToArray());
        });
        Add("event running", "List what is running now: events in progress and event scripts still executing.", [], _ =>
        {
            var now = DateTime.Now;
            return new JsonObject
            {
                ["inProgress"] = new JsonArray(s.Events.InProgress(now).Select(x => (JsonNode)new JsonObject
                {
                    ["id"] = x.Item.Id, ["title"] = x.Item.Title, ["kind"] = x.Item.Kind.ToString().ToLowerInvariant(),
                    ["start"] = x.Occurrence.Start.ToString("yyyy-MM-ddTHH:mm:ss"), ["end"] = x.Occurrence.End?.ToString("yyyy-MM-ddTHH:mm:ss"),
                }).ToArray()),
                ["scripts"] = new JsonArray(s.Events.RunningScripts.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Item.Id, ["title"] = r.Item.Title, ["phase"] = r.Phase, ["started"] = r.Started.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["seconds"] = Math.Round((now - r.Started).TotalSeconds), ["script"] = r.Script,
                }).ToArray()),
            };
        });
        Add("event stop", "Stop the running scripts of an event.", [P("id", "Event id or title.", true)], a =>
        {
            var e = s.Events.Get(a.Require("id"));
            var runs = s.Events.RunningScripts.Where(r => r.Item.Id == e.Id).ToList();
            foreach (var r in runs) s.Events.Stop(r);
            return runs.Count == 0 ? "nothing running" : $"stopped {runs.Count}";
        });
        Add("event run", "Run an event's script right now.", [P("id", "Event id or title.", true), F("end", "Run the on-end script instead of on-start.")], async a =>
        {
            var e = s.Events.Get(a.Require("id"));
            var phase = a.Flag("end") ? "end" : "start";
            var script = (phase == "end" ? e.OnEndScript : e.OnStartScript) ?? throw new InvalidOperationException($"This event has no on-{phase} script.");
            var r = await s.Events.RunScriptAsync(e, phase, new Occurrence(DateTime.Now, null), script);
            return new JsonObject { ["exitCode"] = r.ExitCode, ["timedOut"] = r.TimedOut, ["stdout"] = r.Stdout, ["stderr"] = r.Stderr };
        });
        Add("event clear", "Delete completed timers, tasks and events.", [], _ =>
        {
            var done = s.Events.Items.Where(e => e.Completed).ToList();
            foreach (var e in done) s.Events.Remove(e.Id);
            return $"removed {done.Count}";
        });
    }

    private void AddDashboardCommands(AppServices s)
    {
        Add("dashboard info", "Show the dashboard folder, URL and files.", [], _ => new JsonObject
        {
            ["folder"] = s.Dashboard.Root, ["url"] = s.Web.BaseUrl + "/dashboard/",
            ["allowAppControl"] = s.Config.Settings.Dashboard.AllowAppControl,
            ["files"] = new JsonArray(s.Dashboard.Files().Select(f => (JsonNode)f).ToArray()),
        });
        Add("dashboard reload", "Reload the dashboard page.", [], _ => { s.Host.ReloadDashboard(); return "reloaded"; });
        Add("dashboard devtools", "Open the web developer tools for the dashboard page.", [], _ => { s.Host.Navigate("dashboard"); s.Host.OpenDashboardDevTools(); return "ok"; });
        Add("dashboard browser", "Open the dashboard in the default web browser.", [], _ => { Shell.Open(s.Web.DashboardUrl); return s.Web.BaseUrl + "/dashboard/"; });
        Add("dashboard source list", "List the extra CSS/JS sources that can be pulled into the dashboard.", [], _ => J(s.Dashboard.Sources));
        Add("dashboard source add", "Add an extra CSS or JS source (URL, absolute file, or path inside the dashboard folder).",
            [P("url", "Where to load it from.", true), O("type", "css or js (default: from the extension)."), F("disabled", "Add it switched off.")], a =>
        {
            var src = s.Dashboard.AddSource(a.Require("url"), a.Str("type"), !a.Flag("disabled"));
            s.Host.ReloadDashboard();
            return J(src);
        });
        Add("dashboard source remove", "Remove an extra source.", [P("id", "Source id or URL.", true)], a => { s.Dashboard.RemoveSource(a.Require("id")); s.Host.ReloadDashboard(); return "removed"; });
        Add("dashboard source enable", "Switch an extra source on.", [P("id", "Source id or URL.", true)], a => { s.Dashboard.SetEnabled(a.Require("id"), true); s.Host.ReloadDashboard(); return "enabled"; });
        Add("dashboard source disable", "Switch an extra source off.", [P("id", "Source id or URL.", true)], a => { s.Dashboard.SetEnabled(a.Require("id"), false); s.Host.ReloadDashboard(); return "disabled"; });
    }

    private void AddWikiCommands(AppServices s)
    {
        Add("wiki list", "List wiki pages and files.", [], _ => new JsonArray(s.Wiki.Pages().Select(p => (JsonNode)p).ToArray()));
        Add("wiki read", "Print a wiki page's markdown.", [P("path", "Page path, e.g. Notes/Todo.md (\".md\" optional).", true)], a => s.Wiki.Read(a.Require("path")));
        Add("wiki render", "Print a wiki page rendered to HTML.", [P("path", "Page path.", true)], a => Wiki.WikiService.Render(s.Wiki.Read(a.Require("path"))));
        Add("wiki write", "Create or replace a page.", [P("path", "Page path.", true), Rest("content", "Markdown text."), O("file", "Read the content from this file instead."), F("append", "Add to the end instead of replacing.")], a =>
        {
            var content = a.Str("file") is { } f ? File.ReadAllText(f) : Unescape(a.Str("content") ?? "");
            var path = a.Require("path");
            if (a.Flag("append") && s.Wiki.FindPage(path) is { } existing) { content = s.Wiki.Read(existing).TrimEnd('\n') + "\n" + content; path = existing; }
            return s.Wiki.Write(path, content.EndsWith('\n') ? content : content + "\n");
        });
        Add("wiki mkdir", "Create a folder.", [P("path", "Folder path.", true)], a => { s.Wiki.CreateFolder(a.Require("path")); return "created"; });
        Add("wiki move", "Rename or move a page or folder.", [P("from", "Current path.", true), P("to", "New path.", true)], a => s.Wiki.Move(a.Require("from"), a.Require("to")));
        Add("wiki delete", "Delete a page or folder.", [P("path", "Path.", true)], a => { s.Wiki.Delete(s.Wiki.FindPage(a.Require("path")) ?? a.Require("path")); return "deleted"; });
        Add("wiki search", "Search page text.", [Rest("query", "Text to find.", true)], a =>
            J(s.Wiki.Search(a.Require("query")).Select(h => new { page = h.Path, line = h.Line, text = h.Text })));
        Add("wiki open", "Show a page in the Wiki tab.", [P("path", "Page path.", true)], a =>
        {
            var page = s.Wiki.FindPage(a.Require("path")) ?? throw new FileNotFoundException($"No wiki page '{a.Str("path")}'.");
            s.Host.Navigate("wiki");
            s.Host.OpenWikiPage(page);
            return page;
        });
        Add("wiki today", "Open today's daily note in the Wiki tab, creating it if needed (see wiki.dailyNoteFormat).", [O("date", "Another day instead of today."), F("no-open", "Only create it and print its path.")], a =>
        {
            var page = s.Wiki.EnsureDailyNote(a.Time("date") ?? DateTime.Now, s.Config.Settings.Wiki);
            if (!a.Flag("no-open")) { s.Host.Navigate("wiki"); s.Host.OpenWikiPage(page); }
            return page;
        });
        Add("wiki tags", "List the #tags used in wiki pages, with how many pages carry each.", [F("wait", "Wait for the first pass over the wiki to finish.")], async a =>
        {
            if (a.Flag("wait")) await s.Tags.Ready;
            return J(s.Tags.Tags().Select(t => new { tag = t.Name, key = t.Key, pages = t.Pages }));
        });
        Add("wiki tag", "List the pages carrying a #tag (and tags nested under it, a/b under a).", [P("tag", "The tag, with or without '#'.", true), F("exact", "Leave out nested tags."), F("wait", "Wait for the first pass over the wiki to finish.")], async a =>
        {
            if (a.Flag("wait")) await s.Tags.Ready;
            return J(s.Tags.Pages(a.Require("tag"), nested: !a.Flag("exact")).Select(h => new { page = h.Page, tag = h.Tag, line = h.Line, text = h.Snippet, modified = h.Modified }));
        });
        Add("wiki import", "Copy a file (such as an image) into the wiki.", [P("file", "File to copy.", true), O("folder", "Destination folder inside the wiki.")], a =>
            s.Wiki.Import(a.Require("file"), a.Str("folder") ?? ""));
    }

    private void AddRepoCommands(AppServices s)
    {
        Add("repo list", "List configured repositories.", [], _ => J(s.Repos.Items));
        Add("repo add", "Add a git repository.", [P("path", "Folder inside the repository.", true), O("name", "Display name.")], async a => J(await s.Repos.AddAsync(a.Require("path"), a.Str("name"))));
        Add("repo remove", "Remove a repository from the list (files are not touched).", [P("id", "Repository id, name or path.", true)], a => { s.Repos.Remove(a.Require("id")); return "removed"; });
        Add("repo rename", "Change a repository's display name.", [P("id", "Repository id, name or path.", true), Rest("name", "New name.", true)], a => { s.Repos.Rename(a.Require("id"), a.Require("name")); return "renamed"; });
        Add("repo status", "Show branch and changed files for one repository, or all.", [P("id", "Repository id, name or path.")], async a =>
        {
            var statuses = a.Str("id") is { } id ? [await s.Repos.StatusAsync(s.Repos.Get(id))] : await s.Repos.StatusAllAsync();
            return new JsonArray(statuses.Select(st => (JsonNode)new JsonObject
            {
                ["repository"] = st.Name, ["branch"] = st.Branch, ["ahead"] = st.Ahead, ["behind"] = st.Behind, ["error"] = st.Error,
                ["changes"] = new JsonArray(st.Changes.Select(c => (JsonNode)new JsonObject { ["status"] = c.Code, ["path"] = c.Path, ["what"] = c.Description }).ToArray()),
            }).ToArray());
        });
        Add("repo changes", "List changed files across one repository, or all.", [P("id", "Repository id, name or path.")], async a =>
        {
            var statuses = a.Str("id") is { } id ? [await s.Repos.StatusAsync(s.Repos.Get(id))] : await s.Repos.StatusAllAsync();
            return J(statuses.SelectMany(st => st.Changes).Select(c => new { repository = c.RepoName, status = c.Code, what = c.Description, path = c.Path }));
        });
        Add("repo diff", "Show the diff of a file, or of the whole working tree.", [P("id", "Repository id, name or path.", true), P("file", "File path inside the repository."), F("staged", "Show the staged diff.")], async a =>
        {
            var repo = s.Repos.Get(a.Require("id"));
            return a.Str("file") is { } f ? await s.Repos.DiffAsync(repo, f, a.Flag("staged")) : await s.Repos.DiffAllAsync(repo);
        });
        Add("repo log", "Show recent commits.", [P("id", "Repository id, name or path.", true), O("count", "How many.")], async a =>
            J(await s.Repos.LogAsync(s.Repos.Get(a.Require("id")), a.Int("count"))));
        Add("repo show", "Show a commit.", [P("id", "Repository id, name or path.", true), P("commit", "Commit hash.", true)], async a =>
            await s.Repos.ShowAsync(s.Repos.Get(a.Require("id")), a.Require("commit")));
        Add("repo stage", "Stage a file.", [P("id", "Repository id, name or path.", true), P("file", "File path.", true)], async a => { await s.Repos.StageAsync(s.Repos.Get(a.Require("id")), a.Require("file")); return "staged"; });
        Add("repo unstage", "Unstage a file.", [P("id", "Repository id, name or path.", true), P("file", "File path.", true)], async a => { await s.Repos.UnstageAsync(s.Repos.Get(a.Require("id")), a.Require("file")); return "unstaged"; });
        Add("repo edit", "Open a file from a repository in the editor (repositories.editor, default nvim) in a new terminal tab.",
            [P("id", "Repository id, name or path.", true), P("file", "File path inside the repository.", true)], a =>
        {
            var repo = s.Repos.Get(a.Require("id"));
            var file = a.Require("file");
            var session = s.Terminals.Create(new TerminalStartOptions(s.Config.Settings.Repositories.Editor, [file], repo.Path, $"{s.Config.Settings.Repositories.Editor} {Path.GetFileName(file)}"));
            s.Host.SelectTerminal(session.Id);
            s.Host.Navigate("terminals");
            return J(session.Info());
        });
        Add("repo branches", "List branches.", [P("id", "Repository id, name or path.", true), F("local", "Only local branches.")], async a =>
            J((await s.Repos.BranchesAsync(s.Repos.Get(a.Require("id")), !a.Flag("local"))).Select(b => new { branch = b.Name, current = b.IsCurrent, remote = b.IsRemote, upstream = b.Upstream, track = b.Track, updated = b.Date, subject = b.Subject })));
        Add("repo switch", "Switch a repository to another branch.", [P("id", "Repository id, name or path.", true), P("branch", "Branch name.", true), F("create", "Create the branch first.")], async a =>
        {
            var repo = s.Repos.Get(a.Require("id"));
            if (a.Flag("create")) await s.Repos.CreateBranchAsync(repo, a.Require("branch")); else await s.Repos.SwitchAsync(repo, a.Require("branch"));
            return await s.Repos.CurrentBranchAsync(repo);
        });
        Add("repo commit", "Commit what is staged.", [P("id", "Repository id, name or path.", true), Rest("message", "Commit message.", true)], async a =>
        {
            await s.Repos.CommitAsync(s.Repos.Get(a.Require("id")), a.Require("message"));
            return "committed";
        });
        Add("repo files", "List the files a commit changed.", [P("id", "Repository id, name or path.", true), P("commit", "Commit hash.", true)], async a =>
            J((await s.Repos.CommitFilesAsync(s.Repos.Get(a.Require("id")), a.Require("commit"))).Select(f => new { status = f.Status, what = f.Description, path = f.Path, was = f.OriginalPath })));
        Add("repo remote", "Print (and with --open, open) the web page of a repository's remote.", [P("id", "Repository id, name or path.", true), F("open", "Open it in the browser.")], async a =>
        {
            var url = Repos.RepoService.ToWebUrl(await s.Repos.RemoteUrlAsync(s.Repos.Get(a.Require("id")))) ?? throw new InvalidOperationException("This repository has no remote.");
            if (a.Flag("open")) Shell.Open(url);
            return url;
        });
        Add("repo prs", "List a repository's pull requests. The first use creates a watch for them (see `watch list`).",
            [P("id", "Repository id, name or path.", true), O("command", "Command that prints the pull requests as JSON (default: gh pr list)."), F("mine", "Only mine."), F("all", "Include merged and closed ones."), F("refresh", "Check now.")], async a =>
        {
            var repo = s.Repos.Get(a.Require("id"));
            var existed = s.Watches.FindRepoPullRequests(repo.Path) is not null;
            var watch = s.Watches.EnsureRepoPullRequests(repo.Name, repo.Path, a.Str("command"));
            if (a.Str("command") is { } cmd && existed) { watch.Settings["command"] = cmd; s.Watches.Save(); }
            if (!existed || a.Flag("refresh") || watch.LastChecked is null) await s.Watches.CheckAsync(watch);
            if (watch.LastError is { } error) throw new InvalidOperationException(error);
            return J((watch.LastResult?.Items ?? []).Where(i => (a.Flag("all") || i.Active != false) && (!a.Flag("mine") || i.Mine == true))
                .Select(i => new { title = i.Title, status = i.Status, author = i.Author, mine = i.Mine, link = i.Link }));
        });
        Add("repo select", "Show a repository (or \"all\") in the Repositories tab.", [P("id", "Repository id, name, path, or all.", true)], a =>
        {
            var id = a.Require("id").Equals("all", StringComparison.OrdinalIgnoreCase) ? null : s.Repos.Get(a.Require("id")).Id;
            if (id != Wiki.WikiService.RepoId) s.Host.Navigate("repositories"); // the wiki's repository opens in its own window
            s.Host.SelectRepository(id);
            return "ok";
        });
    }
}
