namespace Avamux.Core.Events;

public enum EventKind { Timer, Task, Calendar }
public enum RecurrenceFreq { None, Interval, Daily, Weekly, Monthly, Yearly }

public sealed class Recurrence
{
    public RecurrenceFreq Freq { get; set; } = RecurrenceFreq.None;
    /// <summary>Every N days / weeks / months / years.</summary>
    public int Interval { get; set; } = 1;
    /// <summary>For <see cref="RecurrenceFreq.Interval"/>: minutes between occurrences.</summary>
    public double IntervalMinutes { get; set; } = 60;
    /// <summary>For weekly recurrence: which days. Empty means the weekday of the start date.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    public DateTime? Until { get; set; }
    public int? Count { get; set; }
}

public sealed class EventItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public EventKind Kind { get; set; } = EventKind.Calendar;
    public bool Enabled { get; set; } = true;
    /// <summary>Description, written in markdown.</summary>
    public string? Notes { get; set; }
    /// <summary>Free text: an address, a link, a room name.</summary>
    public string? Location { get; set; }
    /// <summary>Calendar colour as #rrggbb. Empty uses the colour of the event's kind.</summary>
    public string? Color { get; set; }
    /// <summary>File names inside this event's attachments folder.</summary>
    public List<string> Attachments { get; set; } = [];
    /// <summary>Local time of the first occurrence (for a timer: when it goes off).</summary>
    public DateTime Start { get; set; }
    /// <summary>End of the first occurrence. May be days later (multi-day events).</summary>
    public DateTime? End { get; set; }
    public bool AllDay { get; set; }
    public Recurrence Recurrence { get; set; } = new();
    /// <summary>Shell script run when an occurrence starts / a timer or task triggers.</summary>
    public string? OnStartScript { get; set; }
    /// <summary>Shell script run when an occurrence ends.</summary>
    public string? OnEndScript { get; set; }
    public DateTime? LastFired { get; set; }
    public string? LastRunSummary { get; set; }
    /// <summary>Set once a non-repeating item has finished.</summary>
    public bool Completed { get; set; }
}

public sealed record Occurrence(DateTime Start, DateTime? End)
{
    public DateTime EffectiveEnd => End ?? Start;
}

/// <summary>An event script that is running right now, started by a trigger or by "run now".</summary>
public sealed class RunningScript
{
    internal readonly CancellationTokenSource Cancel = new();
    public required EventItem Item { get; init; }
    /// <summary>"start" or "end".</summary>
    public required string Phase { get; init; }
    public required string Script { get; init; }
    public DateTime Started { get; init; }
    /// <summary>Set when someone asked for it to be stopped.</summary>
    public bool Stopping { get; internal set; }
}

public sealed class EventState
{
    public List<EventItem> Items { get; set; } = [];
}

public static class RecurrenceCalculator
{
    private const int MaxSteps = 200_000;

    /// <summary>All occurrences in chronological order, lazily. Infinite for unbounded recurrences.</summary>
    public static IEnumerable<Occurrence> All(EventItem e, DateTime? fastForwardTo = null)
    {
        var r = e.Recurrence;
        var duration = e.End is { } end && end > e.Start ? end - e.Start : (TimeSpan?)null;
        Occurrence Make(DateTime s) => new(s, duration is { } d ? s + d : null);
        var emitted = 0;
        bool Allowed(DateTime s) => (r.Until is null || s <= r.Until) && (r.Count is null || emitted < r.Count);
        var every = Math.Max(1, r.Interval);

        switch (r.Freq)
        {
            case RecurrenceFreq.None:
                yield return Make(e.Start);
                yield break;

            case RecurrenceFreq.Interval:
            case RecurrenceFreq.Daily:
            {
                var step = r.Freq == RecurrenceFreq.Daily ? TimeSpan.FromDays(every) : TimeSpan.FromMinutes(Math.Max(0.05, r.IntervalMinutes));
                long k = 0;
                if (fastForwardTo is { } ff && r.Count is null)
                {
                    var target = ff - (duration ?? TimeSpan.Zero);
                    if (target > e.Start) k = (long)Math.Floor((target - e.Start) / step);
                }
                for (var i = 0; i < MaxSteps; i++, k++)
                {
                    // Daily steps add calendar days so the wall-clock time survives DST changes.
                    var s = r.Freq == RecurrenceFreq.Daily ? e.Start.AddDays(k * every) : e.Start + step * k;
                    if (!Allowed(s)) yield break;
                    emitted++;
                    yield return Make(s);
                }
                yield break;
            }

            case RecurrenceFreq.Weekly:
            {
                var days = (r.Days.Count == 0 ? [e.Start.DayOfWeek] : r.Days.Distinct()).OrderBy(d => ((int)d + 6) % 7).ToList();
                var weekStart = e.Start.Date.AddDays(-(((int)e.Start.DayOfWeek + 6) % 7)); // Monday
                for (var w = 0; w < MaxSteps; w++)
                {
                    foreach (var d in days)
                    {
                        var s = weekStart.AddDays(7L * every * w + ((int)d + 6) % 7) + e.Start.TimeOfDay;
                        if (s < e.Start) continue;
                        if (!Allowed(s)) yield break;
                        emitted++;
                        yield return Make(s);
                    }
                }
                yield break;
            }

            case RecurrenceFreq.Monthly:
            case RecurrenceFreq.Yearly:
            {
                var months = r.Freq == RecurrenceFreq.Monthly ? every : every * 12;
                for (var i = 0; i < MaxSteps; i++)
                {
                    var total = e.Start.Year * 12 + (e.Start.Month - 1) + (long)months * i;
                    var (year, month) = ((int)(total / 12), (int)(total % 12) + 1);
                    if (year > 9000) yield break;
                    if (e.Start.Day > DateTime.DaysInMonth(year, month)) continue; // e.g. the 31st, or Feb 29
                    var s = new DateTime(year, month, e.Start.Day) + e.Start.TimeOfDay;
                    if (!Allowed(s)) yield break;
                    emitted++;
                    yield return Make(s);
                }
                yield break;
            }
        }
    }

    /// <summary>Occurrences that overlap [from, to].</summary>
    public static IEnumerable<Occurrence> Between(EventItem e, DateTime from, DateTime to)
    {
        foreach (var o in All(e, from))
        {
            if (o.Start > to) yield break;
            if (o.EffectiveEnd >= from) yield return o;
        }
    }

    public static Occurrence? Next(EventItem e, DateTime after)
    {
        foreach (var o in All(e, after))
            if (o.Start > after) return o;
        return null;
    }

    /// <summary>Human description such as "Weekly on Mon, Tue, Fri".</summary>
    public static string Describe(Recurrence r)
    {
        var n = Math.Max(1, r.Interval);
        string Every(string unit) => n == 1 ? unit switch { "day" => "Daily", "week" => "Weekly", "month" => "Monthly", _ => "Yearly" } : $"Every {n} {unit}s";
        var text = r.Freq switch
        {
            RecurrenceFreq.None => "Once",
            RecurrenceFreq.Interval => "Every " + FormatMinutes(r.IntervalMinutes),
            RecurrenceFreq.Daily => Every("day"),
            RecurrenceFreq.Weekly => Every("week") + (r.Days.Count > 0 ? " on " + string.Join(", ", r.Days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3])) : ""),
            RecurrenceFreq.Monthly => Every("month"),
            _ => Every("year"),
        };
        if (r.Freq != RecurrenceFreq.None)
        {
            if (r.Count is { } c) text += $", {c} times";
            if (r.Until is { } u) text += $", until {u:yyyy-MM-dd}";
        }
        return text;
    }

    public static string FormatMinutes(double minutes)
    {
        var t = TimeSpan.FromMinutes(minutes);
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:0.#}s";
        if (t.TotalMinutes < 60) return $"{t.TotalMinutes:0.#}m";
        if (t.TotalHours < 24) return $"{t.TotalHours:0.#}h";
        return $"{t.TotalDays:0.#}d";
    }
}
