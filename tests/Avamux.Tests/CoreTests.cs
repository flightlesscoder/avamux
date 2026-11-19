using System.Text;
using System.Text.Json.Nodes;
using Avamux.Core;
using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Events;
using Avamux.Core.Logging;
using Avamux.Core.Repos;
using Avamux.Core.Terminal;
using Avamux.Core.Watching;
using Avamux.Core.Wiki;

namespace Avamux.Tests;

/// <summary>Separate data, home and working folders under a temp folder, removed afterwards.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "avamux-tests-" + Guid.NewGuid().ToString("N")[..10]);
    public AppPaths Paths { get; }

    public Sandbox()
    {
        foreach (var d in new[] { "home", "work" }) Directory.CreateDirectory(Path.Combine(Root, d));
        Paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "home"), Path.Combine(Root, "work"));
    }

    public string Home(string file) => Path.Combine(Paths.HomeDir, file);
    public string Work(string file) => Path.Combine(Paths.WorkingDir, file);
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class ConfigTests
{
    [Fact]
    public void Defaults_apply_when_no_files_exist()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        Assert.Equal(10000, config.Settings.Terminal.Scrollback);
        Assert.Equal(200, config.Settings.Logs.MaxTotalSizeMb);
        Assert.Equal(5, config.Settings.Watching.DefaultIntervalMinutes);
    }

    [Fact]
    public void Working_folder_wins_over_home_and_local_wins_over_both()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.Home("avamux.json"), """{ "terminal": { "scrollback": 1000, "fontSize": 15 }, "watching": { "timeoutSeconds": 11 } }""");
        File.WriteAllText(box.Home("avamux.local.json"), """{ "terminal": { "scrollback": 2000 } }""");
        File.WriteAllText(box.Work("avamux.json"), """{ "terminal": { "scrollback": 3000 }, "logs": { "maxAgeDays": 7 } }""");
        File.WriteAllText(box.Work("avamux.local.json"), """{ "logs": { "maxAgeDays": 3 } }""");
        using var config = new ConfigService(box.Paths);

        Assert.Equal(3000, config.Settings.Terminal.Scrollback);   // working beats home and home.local
        Assert.Equal(15, config.Settings.Terminal.FontSize);       // merged: untouched keys survive
        Assert.Equal(11, config.Settings.Watching.TimeoutSeconds);
        Assert.Equal(3, config.Settings.Logs.MaxAgeDays);          // .local beats its sibling
        Assert.Equal(ConfigScope.Working, config.Describe().First(v => v.Path == "terminal.scrollback").Source);
        Assert.Equal(ConfigScope.WorkingLocal, config.Describe().First(v => v.Path == "logs.maxAgeDays").Source);
    }

    [Fact]
    public void Local_file_merges_with_and_overrides_its_sibling()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.Home("avamux.json"), """{ "terminal": { "scrollback": 1000, "fontSize": 15 } }""");
        File.WriteAllText(box.Home("avamux.local.json"), """{ "terminal": { "scrollback": 2000 } }""");
        using var config = new ConfigService(box.Paths);
        Assert.Equal(2000, config.Settings.Terminal.Scrollback);
        Assert.Equal(15, config.Settings.Terminal.FontSize);
    }

    [Fact]
    public void Set_writes_only_the_changed_key_and_unset_restores_the_default()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        config.Set("Terminal.Scrollback", ConfigService.Coerce("terminal.scrollback", "500"), ConfigScope.HomeLocal);
        Assert.Equal(500, config.Settings.Terminal.Scrollback);
        var written = JsonNode.Parse(File.ReadAllText(box.Home("avamux.local.json")))!;
        Assert.Equal(500, written["terminal"]!["scrollback"]!.GetValue<int>());
        Assert.Single(written.AsObject());

        config.Set("terminal.scrollback", null, ConfigScope.HomeLocal);
        Assert.Equal(10000, config.Settings.Terminal.Scrollback);
    }

    [Fact]
    public void Coerce_rejects_values_of_the_wrong_type()
    {
        Assert.Throws<ArgumentException>(() => ConfigService.Coerce("terminal.scrollback", "lots"));
        Assert.Throws<ArgumentException>(() => ConfigService.Coerce("terminal.cursorBlink", "maybe"));
        Assert.True(ConfigService.Coerce("terminal.cursorBlink", "on")!.GetValue<bool>());
    }

    [Fact]
    public void A_broken_file_is_ignored_rather_than_fatal()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.Home("avamux.json"), "{ not json");
        using var config = new ConfigService(box.Paths);
        Assert.Equal(10000, config.Settings.Terminal.Scrollback);
    }
}

public class CliTests
{
    private static readonly CommandRegistry Registry = CommandRegistry.Build(null);

    [Theory]
    [InlineData("/?"), InlineData("--?"), InlineData("-?"), InlineData("/help"), InlineData("--help"), InlineData("-h"), InlineData("help")]
    public void Every_help_spelling_is_recognised(string word)
    {
        Assert.True(CliParser.Parse(Registry, [word]).Help);
        var forCommand = CliParser.Parse(Registry, ["terminal", "new", word]);
        Assert.True(forCommand.Help);
        Assert.Equal("terminal new", forCommand.HelpTopic);
        Assert.Equal("watch", CliParser.Parse(Registry, ["watch", word]).HelpTopic);
    }

    [Fact]
    public void Help_text_lists_every_group()
    {
        var help = CliParser.Help(Registry, null);
        foreach (var group in new[] { "terminal", "settings", "watch", "event", "dashboard", "logs", "wiki", "repo" })
            Assert.Contains(group + " ", help);
    }

    [Fact]
    public void Options_positionals_flags_and_literal_arguments_are_parsed()
    {
        var r = CliParser.Parse(Registry, ["terminal", "new", "--name", "build", "--background", "--", "htop", "--tree"]);
        Assert.Null(r.Error);
        Assert.Equal("terminal new", r.Command!.Name);
        var a = new Args(r.Args);
        Assert.Equal("build", a.Str("name"));
        Assert.True(a.Flag("background"));
        Assert.Equal(["htop", "--tree"], a.List("command"));
    }

    [Fact]
    public void Repeated_options_collect_and_unknown_ones_are_errors()
    {
        var r = CliParser.Parse(Registry, ["watch", "add", "PRs", "--kind", "github-pr", "--set", "repo=a/b", "--set", "state=open"]);
        Assert.Equal(["repo=a/b", "state=open"], new Args(r.Args).List("set"));
        Assert.NotNull(CliParser.Parse(Registry, ["watch", "add", "x", "--nope", "1"]).Error);
        Assert.NotNull(CliParser.Parse(Registry, ["frobnicate"]).Error);
    }

    [Fact]
    public void Control_key_escapes_are_decoded()
    {
        Assert.Equal("\u0002c", CommandRegistry.Unescape("^Bc"));
        Assert.Equal("\x03", CommandRegistry.Unescape("^C"));
        Assert.Equal("a\r\n\x1b[A^", CommandRegistry.Unescape("a\\r\\n\\e[A^^"));
    }

    [Fact]
    public void Durations_and_times_are_understood()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), TimeParsing.ParseDuration("90s"));
        Assert.Equal(TimeSpan.FromMinutes(90), TimeParsing.ParseDuration("1h30m"));
        Assert.Equal(TimeSpan.FromMinutes(5), TimeParsing.ParseDuration("5"));
        Assert.Equal(DateTime.Today.AddDays(1).AddHours(9), TimeParsing.ParseDateTime("tomorrow 9:00"));
        Assert.Throws<ArgumentException>(() => TimeParsing.ParseDuration("soon"));
    }
}

public class RecurrenceTests
{
    private static EventItem Item(DateTime start, RecurrenceFreq freq, Action<Recurrence>? tweak = null, DateTime? end = null)
    {
        var e = new EventItem { Title = "t", Start = start, End = end, Recurrence = new Recurrence { Freq = freq } };
        tweak?.Invoke(e.Recurrence);
        return e;
    }

    [Fact]
    public void Weekly_on_mon_tue_fri()
    {
        var monday = new DateTime(2026, 11, 9, 9, 30, 0);
        var e = Item(monday, RecurrenceFreq.Weekly, r => r.Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday]);
        var days = RecurrenceCalculator.All(e).Take(6).Select(o => o.Start).ToList();
        Assert.Equal([monday, monday.AddDays(1), monday.AddDays(4), monday.AddDays(7), monday.AddDays(8), monday.AddDays(11)], days);
    }

    [Fact]
    public void Every_second_week_skips_a_week()
    {
        var e = Item(new DateTime(2026, 1, 5, 8, 0, 0), RecurrenceFreq.Weekly, r => r.Interval = 2);
        Assert.Equal(new DateTime(2026, 1, 19, 8, 0, 0), RecurrenceCalculator.All(e).Skip(1).First().Start);
    }

    [Fact]
    public void Monthly_on_the_31st_skips_short_months()
    {
        var e = Item(new DateTime(2026, 1, 31, 12, 0, 0), RecurrenceFreq.Monthly);
        Assert.Equal([1, 3, 5, 7], RecurrenceCalculator.All(e).Take(4).Select(o => o.Start.Month));
    }

    [Fact]
    public void Yearly_on_a_leap_day_waits_for_the_next_leap_year()
    {
        var e = Item(new DateTime(2024, 2, 29), RecurrenceFreq.Yearly);
        Assert.Equal(2028, RecurrenceCalculator.All(e).Skip(1).First().Start.Year);
    }

    [Fact]
    public void Count_and_until_end_the_series()
    {
        var start = new DateTime(2026, 3, 1, 7, 0, 0);
        Assert.Equal(3, RecurrenceCalculator.All(Item(start, RecurrenceFreq.Daily, r => r.Count = 3)).Count());
        Assert.Equal(5, RecurrenceCalculator.All(Item(start, RecurrenceFreq.Daily, r => r.Until = start.AddDays(4))).Count());
    }

    [Fact]
    public void A_multi_day_event_overlaps_every_day_it_spans()
    {
        var e = Item(new DateTime(2026, 11, 17), RecurrenceFreq.None, end: new DateTime(2026, 11, 19, 23, 59, 59));
        Assert.Single(RecurrenceCalculator.Between(e, new DateTime(2026, 11, 18), new DateTime(2026, 11, 18, 23, 0, 0)));
        Assert.Empty(RecurrenceCalculator.Between(e, new DateTime(2026, 11, 20), new DateTime(2026, 11, 21)));
    }

    [Fact]
    public void Interval_repeats_fast_forward_to_the_present()
    {
        var e = Item(new DateTime(2020, 1, 1), RecurrenceFreq.Interval, r => r.IntervalMinutes = 15);
        var now = new DateTime(2026, 11, 11, 10, 7, 0);
        Assert.Equal(new DateTime(2026, 11, 11, 10, 15, 0), RecurrenceCalculator.Next(e, now)!.Start);
    }

    [Fact]
    public void Skipped_dates_leave_out_occurrences_but_still_count_toward_the_total()
    {
        var monday = new DateTime(2026, 12, 21, 10, 0, 0);
        var e = Item(monday, RecurrenceFreq.Daily, r => r.Count = 4);
        e.SkippedDates = [new DateOnly(2026, 12, 22)];
        Assert.Equal([monday, monday.AddDays(2), monday.AddDays(3)], RecurrenceCalculator.All(e).Select(o => o.Start));
        Assert.Equal(monday.AddDays(2), RecurrenceCalculator.Next(e, monday)!.Start);
        Assert.Empty(RecurrenceCalculator.Between(e, monday.AddDays(1), monday.AddDays(1).AddHours(23)));
        // The skipped occurrence is still found when asked for by date.
        Assert.Equal(monday.AddDays(1), RecurrenceCalculator.OccurrenceOn(e, new DateOnly(2026, 12, 22))!.Start);
        Assert.Null(RecurrenceCalculator.OccurrenceOn(e, new DateOnly(2026, 12, 26)));
    }

    [Fact]
    public void Skipping_works_for_weekly_and_fast_forwarded_interval_repeats()
    {
        var weekly = Item(new DateTime(2026, 11, 5, 9, 0, 0), RecurrenceFreq.Weekly); // a Thursday
        weekly.SkippedDates = [new DateOnly(2026, 11, 12)];
        Assert.Equal(new DateTime(2026, 11, 19, 9, 0, 0), RecurrenceCalculator.Next(weekly, new DateTime(2026, 11, 6))!.Start);

        var interval = Item(new DateTime(2020, 1, 1), RecurrenceFreq.Interval, r => r.IntervalMinutes = 15);
        interval.SkippedDates = [new DateOnly(2026, 11, 11)];
        Assert.Equal(new DateTime(2026, 11, 12, 0, 0, 0), RecurrenceCalculator.Next(interval, new DateTime(2026, 11, 11, 10, 7, 0))!.Start);
    }

    [Fact]
    public void Descriptions_read_naturally()
    {
        Assert.Equal("Weekly on Mon, Tue, Fri", RecurrenceCalculator.Describe(new Recurrence { Freq = RecurrenceFreq.Weekly, Days = [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Tuesday] }));
        Assert.Equal("Every 15m", RecurrenceCalculator.Describe(new Recurrence { Freq = RecurrenceFreq.Interval, IntervalMinutes = 15 }));
        Assert.Equal("Every 2 months", RecurrenceCalculator.Describe(new Recurrence { Freq = RecurrenceFreq.Monthly, Interval = 2 }));
    }
}

public class EventServiceTests
{
    [Fact]
    public void Start_and_end_fire_once_each_and_a_one_off_event_completes()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var now = new DateTime(2026, 11, 11, 9, 0, 0);
        using var events = new EventService(box.Paths, config) { Now = () => now };
        var fired = new List<string>();
        events.Fired += (item, phase, _) => fired.Add($"{item.Title}:{phase}");
        events.Tick(); // establishes the starting point of the clock
        var e = events.Add(new EventItem { Title = "Meeting", Start = now.AddSeconds(30), End = now.AddSeconds(90) });

        now = now.AddSeconds(31); events.Tick();
        Assert.Equal(["Meeting:start"], fired);
        now = now.AddSeconds(10); events.Tick();
        Assert.Single(fired);
        Assert.False(e.Completed);
        now = now.AddSeconds(60); events.Tick();
        Assert.Equal(["Meeting:start", "Meeting:end"], fired);
        Assert.True(e.Completed);
    }

    [Fact]
    public async Task A_script_receives_the_event_and_its_output_is_logged()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var events = new EventService(box.Paths, config);
        var e = events.Add(new EventItem { Title = "Backup", Kind = EventKind.Task, Start = DateTime.Now.AddHours(1), OnStartScript = "echo \"$AVAMUX_EVENT_TITLE/$AVAMUX_EVENT_PHASE\"" });
        var result = await events.RunScriptAsync(e, "start", new Occurrence(DateTime.Now, null), e.OnStartScript!);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Backup/start", result.Stdout.Trim());
        Assert.Contains("Backup/start", File.ReadAllText(Directory.GetFiles(box.Paths.EventLogsDir).Single()));
        Assert.Contains("exit 0", e.LastRunSummary);
    }

    [Fact]
    public async Task A_running_script_is_listed_until_it_finishes_and_can_be_stopped()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var events = new EventService(box.Paths, config);
        var e = events.Add(new EventItem { Title = "Long job", Kind = EventKind.Task, Start = DateTime.Now.AddHours(1), OnStartScript = "sleep 30" });
        var run = events.RunScriptAsync(e, "start", new Occurrence(DateTime.Now, null), e.OnStartScript!);
        var listed = Assert.Single(events.RunningScripts);
        Assert.Same(e, listed.Item);
        Assert.Equal("start", listed.Phase);

        events.Stop(listed);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.TimedOut);
        Assert.Empty(events.RunningScripts);
        Assert.Contains("stopped", e.LastRunSummary);
    }

    [Fact]
    public void In_progress_lists_events_between_their_start_and_end()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        using var events = new EventService(box.Paths, config) { Now = () => now };
        events.Add(new EventItem { Title = "Meeting", Start = now.AddMinutes(-15), End = now.AddMinutes(45) });
        events.Add(new EventItem { Title = "Later", Start = now.AddHours(2), End = now.AddHours(3) });
        events.Add(new EventItem { Title = "No end", Start = now.AddMinutes(-5) });
        events.Add(new EventItem { Title = "Holiday", Start = now.Date, End = now.Date.AddDays(1).AddSeconds(-1), AllDay = true });
        events.Add(new EventItem { Title = "Off", Start = now.AddMinutes(-15), End = now.AddMinutes(45), Enabled = false });
        events.Add(new EventItem
        {
            Title = "Standup", Start = now.Date.AddDays(-7).AddHours(9).AddMinutes(50), End = now.Date.AddDays(-7).AddHours(10).AddMinutes(5),
            Recurrence = new Recurrence { Freq = RecurrenceFreq.Daily },
        });

        var running = events.InProgress(now);
        Assert.Equal(["Standup", "Meeting"], running.Select(r => r.Item.Title).OrderByDescending(t => t));
        Assert.Equal(now.Date.AddHours(9).AddMinutes(50), running.Single(r => r.Item.Title == "Standup").Occurrence.Start);
        Assert.Empty(events.InProgress(now.AddMinutes(45)));
    }

    [Fact]
    public void Events_survive_a_restart()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using (var first = new EventService(box.Paths, config))
            first.Add(new EventItem { Title = "Standup", Start = new DateTime(2026, 11, 9, 9, 30, 0), Recurrence = new Recurrence { Freq = RecurrenceFreq.Weekly, Days = [DayOfWeek.Monday] } });
        using var second = new EventService(box.Paths, config);
        var loaded = Assert.Single(second.Items);
        Assert.Equal(RecurrenceFreq.Weekly, loaded.Recurrence.Freq);
        Assert.Equal([DayOfWeek.Monday], loaded.Recurrence.Days);
    }
}

public class EventSkipTests
{
    private static readonly DateTime Monday = new(2026, 12, 21, 10, 0, 0);

    private static EventItem Weekdays() => new()
    {
        Title = "Standup", Start = Monday, End = Monday.AddMinutes(15),
        Recurrence = new Recurrence { Freq = RecurrenceFreq.Weekly, Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] },
    };

    [Fact]
    public void A_skipped_occurrence_leaves_the_agenda_and_the_next_occurrence()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var now = Monday.AddHours(-1);
        using var events = new EventService(box.Paths, config) { Now = () => now };
        var e = events.Add(Weekdays());
        events.Skip(e, [new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 22)]);
        Assert.Equal(new DateTime(2026, 12, 23, 10, 0, 0), events.NextOccurrence(e)!.Start);
        Assert.Equal([new DateTime(2026, 12, 23, 10, 0, 0), new DateTime(2026, 12, 24, 10, 0, 0)], events.Agenda(Monday, Monday.AddDays(3).AddHours(12)).Select(x => x.Occurrence.Start));

        events.Unskip(e, [new DateOnly(2026, 12, 21)]);
        Assert.Equal(Monday, events.NextOccurrence(e)!.Start);
        Assert.Equal([new DateOnly(2026, 12, 22)], e.SkippedDates);
    }

    [Fact]
    public void Skipping_a_date_without_an_occurrence_or_on_a_one_off_event_is_an_error()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var events = new EventService(box.Paths, config);
        var e = events.Add(Weekdays());
        var ex = Assert.Throws<ArgumentException>(() => events.Skip(e, [new DateOnly(2026, 12, 22), new DateOnly(2026, 12, 26)])); // a Saturday
        Assert.Contains("2026-12-26", ex.Message);
        Assert.Empty(e.SkippedDates); // nothing is applied when one date is wrong
        Assert.Throws<ArgumentException>(() => events.Unskip(e, [new DateOnly(2026, 12, 22)]));
        var once = events.Add(new EventItem { Title = "Once", Start = Monday });
        Assert.Throws<InvalidOperationException>(() => events.Skip(once, [new DateOnly(2026, 12, 21)]));
    }

    [Fact]
    public void Tick_does_not_fire_a_skipped_occurrence()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var now = Monday.AddMinutes(-1);
        using var events = new EventService(box.Paths, config) { Now = () => now };
        var fired = new List<string>();
        events.Fired += (_, phase, o) => fired.Add($"{o.Start:MM-dd}:{phase}");
        events.Tick();
        var e = events.Add(Weekdays());
        events.Skip(e, [new DateOnly(2026, 12, 21)]);
        now = Monday.AddSeconds(30); events.Tick(); // the skipped Monday start passes
        now = Monday.AddMinutes(15).AddSeconds(30); events.Tick(); // and its end
        Assert.Empty(fired);
        now = Monday.AddDays(1).AddMinutes(-1); events.Tick();
        now = Monday.AddDays(1).AddSeconds(30); events.Tick();
        now = Monday.AddDays(1).AddMinutes(15).AddSeconds(30); events.Tick();
        Assert.Equal(["12-22:start", "12-22:end"], fired);
        Assert.False(e.Completed);
    }

    [Fact]
    public void An_event_whose_remaining_occurrences_are_all_skipped_completes_and_comes_back_when_unskipped()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var now = Monday.AddHours(-1);
        using var events = new EventService(box.Paths, config) { Now = () => now };
        var fired = new List<string>();
        events.Fired += (_, phase, _) => fired.Add(phase);
        events.Tick();
        var e = events.Add(new EventItem { Title = "Two days", Start = Monday, Recurrence = new Recurrence { Freq = RecurrenceFreq.Daily, Count = 2 } });
        events.Skip(e, [new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 22)]);
        now = Monday.AddHours(1); events.Tick();
        Assert.True(e.Completed);
        Assert.Empty(fired);
        Assert.Null(events.NextOccurrence(e));

        e.Completed = true;
        now = Monday.AddHours(-1);
        events.Unskip(e, [new DateOnly(2026, 12, 22)]);
        Assert.False(e.Completed);
        Assert.Equal(Monday.AddDays(1), events.NextOccurrence(e)!.Start);
    }

    [Fact]
    public void Skipped_dates_survive_a_restart_and_old_files_without_them_load()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using (var first = new EventService(box.Paths, config))
        {
            var e = first.Add(Weekdays());
            first.Skip(e, [new DateOnly(2026, 12, 25)]);
            first.Save();
        }
        using (var second = new EventService(box.Paths, config))
            Assert.Equal([new DateOnly(2026, 12, 25)], Assert.Single(second.Items).SkippedDates);
        var file = box.Paths.StateFile("events");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"SkippedDates\"", "\"Other\"").Replace("\"skippedDates\"", "\"other\""));
        using var old = new EventService(box.Paths, config);
        Assert.Empty(Assert.Single(old.Items).SkippedDates);
    }

    [Fact]
    public async Task Commands_skip_unskip_and_show_the_dates()
    {
        using var box = new Sandbox();
        await using var app = new AppServices(box.Paths);
        var registry = CommandRegistry.Build(null);
        var add = CliParser.Parse(registry, ["event", "add", "Standup", "--start", "2026-12-21T10:00", "--repeat", "weekly", "--days", "weekdays"]);
        Assert.True((await app.Commands.ExecuteAsync("event add", add.Args))["ok"]!.GetValue<bool>());

        var skip = CliParser.Parse(registry, ["event", "skip", "Standup", "2026-12-25", "2026-12-28"]);
        Assert.Null(skip.Error);
        var r = await app.Commands.ExecuteAsync("event skip", skip.Args);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        Assert.Equal(["2026-12-25", "2026-12-28"], r["result"]!["skipped"]!.AsArray().Select(n => n!.GetValue<string>()));

        r = await app.Commands.ExecuteAsync("event skip", CliParser.Parse(registry, ["event", "skip", "Standup", "2026-12-26"]).Args); // a Saturday
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("no occurrence on 2026-12-26", r.ToJsonString());

        r = await app.Commands.ExecuteAsync("event edit", CliParser.Parse(registry, ["event", "edit", "Standup", "--unskip", "2026-12-25", "--skip", "2026-12-29"]).Args);
        Assert.True(r["ok"]!.GetValue<bool>(), r.ToJsonString());
        Assert.Equal(["2026-12-28", "2026-12-29"], r["result"]!["skipped"]!.AsArray().Select(n => n!.GetValue<string>()));

        r = await app.Commands.ExecuteAsync("event unskip", CliParser.Parse(registry, ["event", "unskip", "Standup", "2026-12-28,2026-12-29"]).Args);
        Assert.Null(r["result"]!["skipped"]);
        Assert.True(CliParser.Parse(registry, ["event", "skip", "--help"]).Help);
    }
}

public class WatchTests
{
    [Fact]
    public void Script_output_in_the_documented_shape_is_read()
    {
        var r = CommandWatchProvider.ParseOutput("""
            noise before the json
            {"status":"failed","summary":"Build broke","link":"https://ci/1","items":[{"id":"7","title":"Job A","status":"failed","detail":"exit 2","link":"https://ci/1/a"}]}
            """, "", 0);
        Assert.Equal("error", r.Status);
        Assert.Equal("Build broke", r.Summary);
        Assert.Equal("https://ci/1", r.Link);
        Assert.Equal("https://ci/1/a", Assert.Single(r.Items).Link);
    }

    [Theory]
    [InlineData(0, "ok"), InlineData(1, "warn"), InlineData(2, "error")]
    public void Plain_text_output_uses_the_exit_code(int exitCode, string status)
    {
        var r = CommandWatchProvider.ParseOutput("\nall quiet\nsecond line\n", "", exitCode);
        Assert.Equal(status, r.Status);
        Assert.Equal("all quiet", r.Summary);
    }

    [Fact]
    public async Task A_command_watch_runs_gets_its_settings_and_keeps_a_user_link()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var watches = new WatchService(box.Paths, config);
        var item = watches.Add(new WatchItem
        {
            Name = "Demo", Kind = "command",
            Settings = { ["command"] = """echo "{\"status\":\"ok\",\"summary\":\"$AVAMUX_WATCH_NAME sees $AVAMUX_WATCH_TARGET\",\"link\":\"https://reported\"}" """, ["target"] = "prod" },
        });
        await watches.CheckAsync(item);
        Assert.Null(item.LastError);
        Assert.Equal("Demo sees prod", item.Summary);
        Assert.Equal("https://reported", item.EffectiveLink);
        item.Link = "https://mine";
        Assert.Equal("https://mine", item.EffectiveLink);
    }

    [Fact]
    public async Task A_failing_check_is_reported_not_thrown()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var watches = new WatchService(box.Paths, config);
        var item = watches.Add(new WatchItem { Name = "No such kind", Kind = "does-not-exist" });
        await watches.CheckAsync(item);
        Assert.Equal("error", item.Status);
        Assert.Contains("Unknown watch kind", item.Summary);
    }

    [Fact]
    public void Built_in_kinds_cover_github_azure_devops_scripts_and_plugins()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var watches = new WatchService(box.Paths, config);
        foreach (var kind in new[] { "command", "github-pr", "ado-pr", "ado-pbi", "ado-pipeline", "plugin" })
            Assert.Contains(kind, watches.Kinds);
    }
}

public class RepoTests
{
    [Fact]
    public void Porcelain_status_is_parsed_including_renames_and_tracking()
    {
        var repo = new RepoEntry { Id = "r", Name = "demo", Path = "/x" };
        var st = RepoService.ParseStatus(repo, "## main...origin/main [ahead 2, behind 1]\0 M src/a.cs\0A  new file.txt\0R  to.txt\0from.txt\0?? notes.md\0");
        Assert.Equal(("main", "origin/main", 2, 1), (st.Branch, st.Upstream, st.Ahead, st.Behind));
        Assert.Equal(["src/a.cs", "new file.txt", "to.txt", "notes.md"], st.Changes.Select(c => c.Path));
        Assert.Equal(["modified", "added", "renamed", "untracked"], st.Changes.Select(c => c.Description));
        Assert.Equal("from.txt", st.Changes[2].OriginalPath);
        Assert.True(st.Changes[1].Staged && !st.Changes[1].Unstaged);
    }

    [Fact]
    public async Task A_real_repository_reports_changes_diffs_and_staging()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var dir = Path.Combine(box.Root, "repo");
        Directory.CreateDirectory(dir);
        async Task Git(params string[] args) => Assert.Equal(0, (await Shell.RunAsync("git", args, dir)).ExitCode);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "user.name", "Test");
        await Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        await Git("add", ".");
        await Git("commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "new\n");

        var repos = new RepoService(box.Paths, config);
        var repo = await repos.AddAsync(Path.Combine(dir));
        var status = await repos.StatusAsync(repo);
        Assert.Equal("main", status.Branch);
        Assert.Equal(["a.txt", "b.txt"], status.Changes.Select(c => c.Path).Order());
        Assert.Contains("+two", await repos.DiffAsync(repo, "a.txt"));
        Assert.Contains("+new", await repos.DiffAsync(repo, "b.txt", untracked: true));

        await repos.StageAsync(repo, "b.txt");
        Assert.True((await repos.StatusAsync(repo)).Changes.Single(c => c.Path == "b.txt").Staged);
        await repos.UnstageAsync(repo, "b.txt");
        Assert.True((await repos.StatusAsync(repo)).Changes.Single(c => c.Path == "b.txt").Untracked);
        Assert.Equal("first", Assert.Single(await repos.LogAsync(repo)).Subject);
        Assert.Same(repo, await repos.AddAsync(dir)); // adding twice does not duplicate
    }

    [Fact]
    public void A_real_path_follows_links_in_every_part_not_only_the_last()
    {
        if (OperatingSystem.IsWindows()) return; // links need privileges there
        using var box = new Sandbox();
        var real = Path.Combine(box.Root, "private", "var");
        Directory.CreateDirectory(Path.Combine(real, "folders", "repo"));
        var varLink = Path.Combine(box.Root, "var");
        Directory.CreateSymbolicLink(varLink, Path.Combine("private", "var")); // like macOS's /var -> private/var
        var expected = Path.Combine(RepoService.RealPath(real), "folders", "repo");

        // The link is an ancestor, not the leaf.
        Assert.Equal(expected, RepoService.RealPath(Path.Combine(varLink, "folders", "repo")));
        Assert.Equal(expected, RepoService.RealPath(Path.Combine(varLink, "folders", "repo") + "/"));
        Assert.True(RepoService.SamePath(Path.Combine(varLink, "folders", "repo"), expected));

        // A link to a link, an absolute target and ".." inside a target.
        Directory.CreateSymbolicLink(Path.Combine(box.Root, "abs"), varLink);
        Directory.CreateSymbolicLink(Path.Combine(box.Root, "up"), Path.Combine("var", "folders", "..", "folders"));
        Assert.Equal(expected, RepoService.RealPath(Path.Combine(box.Root, "abs", "folders", "repo")));
        Assert.Equal(expected, RepoService.RealPath(Path.Combine(box.Root, "up", "repo")));

        // Parts that do not exist are kept; a loop of links does not hang.
        Assert.Equal(Path.Combine(expected, "not", "there"), RepoService.RealPath(Path.Combine(varLink, "folders", "repo", "not", "there")));
        Directory.CreateSymbolicLink(Path.Combine(box.Root, "loop"), "loop");
        Assert.EndsWith("loop", RepoService.RealPath(Path.Combine(box.Root, "loop")));
        Assert.Equal("/", RepoService.RealPath("/"));
    }
}

public class WikiTests
{
    [Fact]
    public void Pages_folders_links_and_search_work_together()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        Assert.Equal("Notes/Todo.md", wiki.Write("Notes/Todo", "# Todo\n\nbuy milk\n"));
        Assert.Equal("Notes/Todo.md", wiki.FindPage("Todo"));          // [[Todo]] style lookup
        Assert.Contains("buy milk", wiki.Read("Notes/Todo"));
        Assert.Equal(("Notes/Todo.md", 3), wiki.Search("MILK").Select(h => (h.Path, h.Line)).Single());
        Assert.Equal("Archive/Done.md", wiki.Move("Notes/Todo.md", "Archive/Done"));
        Assert.Contains(wiki.Tree().Children, n => n is { IsFolder: true, Name: "Archive" });
        wiki.Delete("Archive");
        Assert.Null(wiki.FindPage("Done"));
    }

    [Fact]
    public void Markdown_renders_wiki_links_images_and_tables_but_leaves_code_alone()
    {
        var html = WikiService.Render("See [[Notes/Todo|my list]] and [[Home]].\n\n![pic](cat.png)\n\n`[[literal]]`\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");
        Assert.Contains("<a href=\"Notes/Todo.md\">my list</a>", html);
        Assert.Contains("<a href=\"Home.md\">Home</a>", html);
        Assert.Contains("<img src=\"cat.png\"", html);
        Assert.Contains("<code>[[literal]]</code>", html);
        Assert.Contains("<table>", html);
    }

    [Fact]
    public void Paths_cannot_escape_the_wiki_folder()
    {
        using var box = new Sandbox();
        var wiki = new WikiService(box.Paths);
        Assert.Throws<UnauthorizedAccessException>(() => wiki.Write("../outside", "x"));
        Assert.Throws<UnauthorizedAccessException>(() => wiki.Resolve("a/../../b"));
    }
}

public class LogTests
{
    private static void Touch(string path, int bytes, DateTime modified)
    {
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTime(path, modified);
    }

    [Fact]
    public void Retention_applies_age_then_count_then_total_size()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.Home("avamux.json"), """{ "logs": { "maxAgeDays": 10, "maxFileCount": 4, "maxTotalSizeMb": 2 } }""");
        using var config = new ConfigService(box.Paths);
        var logs = new LogStore(box.Paths, config);
        var now = DateTime.Now;
        Touch(Path.Combine(box.Paths.AppLogsDir, "ancient.log"), 10, now.AddDays(-40));
        for (var i = 0; i < 6; i++) Touch(Path.Combine(box.Paths.TerminalLogsDir, $"t{i}.log"), 700 * 1024, now.AddHours(-i));

        Assert.Equal(5, logs.ApplyRetention()); // the ancient file, two over the count, two more over 2 MB
        Assert.Equal(["t0.log", "t1.log"], logs.List().Select(f => f.Name));
        Assert.True(logs.TotalSize() <= 2 * 1024 * 1024);
    }

    [Fact]
    public void Files_are_grouped_by_category_and_can_be_read_back()
    {
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        var logs = new LogStore(box.Paths, config);
        File.WriteAllText(Path.Combine(box.Paths.CrashDir, "crash-1.log"), "boom\nline2\nline3\n");
        Assert.Equal("crashes", Assert.Single(logs.List()).Category);
        Assert.Empty(logs.List("terminals"));
        Assert.Equal("line3\n", logs.Read("crash-1.log", 1));
        Assert.Equal(1, logs.Clear("crashes"));
    }
}

[Collection("pty")]
public class TerminalTests
{
    private static async Task<string> WaitForAsync(TerminalSession s, string text, int seconds = 10)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            var seen = Encoding.UTF8.GetString(s.Snapshot());
            if (seen.Contains(text)) return seen;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Never saw '{text}'. Output so far: {Encoding.UTF8.GetString(s.Snapshot())}");
    }

    [Fact]
    public async Task A_shell_in_a_pty_echoes_input_handles_control_keys_and_resizes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var box = new Sandbox();
        using var config = new ConfigService(box.Paths);
        using var terminals = new TerminalManager(box.Paths, config);
        var s = terminals.Create(new TerminalStartOptions("/bin/sh", [], box.Root, "probe", 100, 30));
        Assert.Equal("probe", s.Title);

        s.Write("echo \"size=$(stty size)\" glyph=\r");
        var output = await WaitForAsync(s, "size=30 100");
        Assert.Contains("glyph=", output); // private-use (powerline) characters pass through untouched

        s.Resize(132, 43);
        s.Write("stty size\r");
        await WaitForAsync(s, "43 132");

        // Ctrl+C interrupts the foreground program and the shell keeps going.
        s.Write("sleep 30\r");
        await Task.Delay(300);
        s.Write("\x03");
        s.Write("echo back-$((40+2))\r");
        await WaitForAsync(s, "back-42");

        s.Write("exit 7\r");
        for (var i = 0; i < 100 && s.Running; i++) await Task.Delay(50);
        Assert.False(s.Running);
        Assert.Equal(7, s.ExitCode);
    }

    [Fact]
    public void Ansi_sequences_are_stripped_for_plain_text_logs()
    {
        Assert.Equal("green text\nnext", TerminalManager.StripAnsi("\x1b[32mgreen\x1b[0m text\r\n\x1b]0;title\x07next"));
    }
}
