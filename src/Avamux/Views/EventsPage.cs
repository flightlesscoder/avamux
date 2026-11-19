using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avamux.Core;
using Avamux.Core.Commands;
using Avamux.Core.Events;
using Avamux.Core.Theming;

namespace Avamux.Views;

/// <summary>A month calendar next to the list of timers, scheduled tasks and calendar events.</summary>
public sealed class EventsPage : UserControl, IPage
{
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private enum CalendarView { Month, Week, Day }

    /// <summary>Height of one hour in the week and day views.</summary>
    private const double HourHeight = 44;
    private const double GutterWidth = 50;

    private readonly Grid _calendar = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*"), RowDefinitions = new RowDefinitions("Auto,*,*,*,*,*,*") };
    private readonly Border _calendarHost = new() { Margin = new Thickness(12, 0, 12, 12) };
    private readonly ScrollViewer _timeScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    /// <summary>The week and day views: day headers, the all-day strip and the hours. Kept, so the scroll position survives a rebuild.</summary>
    private readonly Border _timeHeader = new(), _timeStrip = new();
    private readonly DockPanel _timeLayout;
    private readonly TextBlock _monthLabel = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
    private readonly Button _prev, _next;
    private readonly Button[] _viewTabs;
    private readonly StackPanel _side = new() { Spacing = 14, Margin = new Thickness(16, 14, 16, 24) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(TextBlock Text, EventItem Item)> _countdowns = [];
    /// <summary>Texts of the "Running now" segment that change every second.</summary>
    private readonly List<Action> _live = [];
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedDay = DateTime.Today;
    private DateTime _today = DateTime.Today;
    private CalendarView _view;
    /// <summary>Set when the week or day view should scroll to a sensible hour on its next build.</summary>
    private bool _scrollPending = true;
    /// <summary>Where the week or day view scrolls to once it has been laid out.</summary>
    private double? _scrollTo;
    /// <summary>The red "now" line of the week and day views, and where it is drawn.</summary>
    private (Border Line, DateTime Day)? _nowLine;
    /// <summary>What was in progress at the last build, to notice when something starts or ends.</summary>
    private string _inProgressKey = "";

    private static EventService Events => App.Services.Events;

    public EventsPage()
    {
        _view = Enum.TryParse<CalendarView>(App.Services.Ui.Data.EventsView, true, out var v) ? v : CalendarView.Month;
        _timeLayout = new DockPanel
        {
            Children = { _timeHeader.Dock(Dock.Top), _timeStrip.Dock(Dock.Top), new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = _timeScroll } },
        };
        _prev = Ui.TextButton("‹", () => Step(-1));
        _next = Ui.TextButton("›", () => Step(1));
        _viewTabs = Enum.GetValues<CalendarView>().Select(view =>
        {
            var b = new Button { Content = view.ToString(), Classes = { "tab" }, Padding = new Thickness(10, 4) };
            // From the month view, land on the selected day if it is in the month shown, else on the month's first day.
            b.Click += (_, _) => SetView(view, _view != CalendarView.Month || (_selectedDay.Year == _month.Year && _selectedDay.Month == _month.Month)
                ? _selectedDay : _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month ? DateTime.Today : _month);
            return b;
        }).ToArray();
        var nav = new DockPanel
        {
            Margin = new Thickness(16, 10, 16, 8),
            Children =
            {
                Ui.Row(0, _viewTabs).Dock(Dock.Right),
                Ui.Row(4,
                    _prev,
                    Ui.TextButton("Today", () =>
                    {
                        _selectedDay = DateTime.Today;
                        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                        _scrollPending = true;
                        Rebuild();
                    }),
                    _next,
                    Ui.Apply(_monthLabel, t => t.Margin = new Thickness(10, 0, 0, 0))),
            },
        };

        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("Events", "Timers, scheduled tasks and calendar events. Each can run a script when it starts or ends.",
                    Ui.IconButton(Icons.Play, "Start a countdown timer", () => _ = TimerAsync(), "Timer"),
                    Ui.IconButton(Icons.Plus, "Schedule a script", () => _ = EditAsync(null, EventKind.Task, null), "Task"),
                    Ui.IconButton(Icons.Plus, "Add a calendar event", () => _ = EditAsync(null, EventKind.Calendar, _selectedDay), "Event")).Dock(Dock.Top),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,400"),
                    Children =
                    {
                        new Border
                        {
                            BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 1, 0),
                            Child = new DockPanel { Children = { nav.Dock(Dock.Top), _calendarHost } },
                        },
                        new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = new ScrollViewer { Content = _side } }.At(0, 1),
                    },
                },
            },
        };
        // The hours can only be scrolled once they have been measured, which happens after a build.
        _timeScroll.LayoutUpdated += (_, _) =>
        {
            if (_scrollTo is not { } y || _timeScroll.Extent.Height <= 0 || _timeScroll.Viewport.Height <= 0) return;
            _scrollTo = null;
            // Set after the layout pass: an offset set while it is running is put back by the scroll presenter.
            Dispatcher.UIThread.Post(() => _timeScroll.Offset = new Vector(0, Math.Min(y, Math.Max(0, _timeScroll.Extent.Height - _timeScroll.Viewport.Height))), DispatcherPriority.Background);
        };
        Events.Changed += Ui.Debounced(Rebuild);
        _clock.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            if (DateTime.Today != _today) { _today = DateTime.Today; Rebuild(); return; }
            // Something started or ended without a trigger saying so (events can be switched off in Settings).
            if (InProgressKey(DateTime.Now) != _inProgressKey) { Rebuild(); return; }
            foreach (var (text, item) in _countdowns) text.Text = NextText(item);
            foreach (var update in _live) update();
            if (_nowLine is { } now) now.Line.Margin = new Thickness(0, Math.Max(0, (DateTime.Now - now.Day).TotalHours * HourHeight - 1), 0, 0);
        };
        _clock.Start();
        Rebuild();
    }

    public void OnShown() => Rebuild();

    /// <summary>Shows an event's month (or week or day) and opens it for editing (used by Search).</summary>
    public void Reveal(string id)
    {
        if (Events.Find(id) is not { } e) { Ui.Toast(this, "That event no longer exists"); return; }
        var when = Events.NextOccurrence(e)?.Start ?? e.Start;
        _selectedDay = when.Date;
        _month = new DateTime(when.Year, when.Month, 1);
        _scrollPending = true;
        Rebuild();
        _ = EditAsync(e, e.Kind, null);
    }

    private void SetView(CalendarView view, DateTime day)
    {
        _view = view;
        _selectedDay = day.Date;
        _month = new DateTime(day.Year, day.Month, 1);
        _scrollPending = true;
        App.Services.Ui.Data.EventsView = view.ToString().ToLowerInvariant();
        App.Services.Ui.SaveSoon();
        Rebuild();
    }

    /// <summary>‹ and ›: a month, a week or a day back or forward.</summary>
    private void Step(int direction)
    {
        if (_view == CalendarView.Month) _month = _month.AddMonths(direction);
        else
        {
            _selectedDay = _selectedDay.AddDays(direction * (_view == CalendarView.Week ? 7 : 1));
            _month = new DateTime(_selectedDay.Year, _selectedDay.Month, 1);
        }
        Rebuild();
    }

    private static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static string InProgressKey(DateTime now) =>
        string.Join("|", Events.InProgress(now).Select(x => $"{x.Item.Id}@{x.Occurrence.Start:O}"));

    /// <summary>Colour of an item: its own if set, else the colour configured for its kind, else the theme's.</summary>
    private static string? CustomColor(EventItem e)
    {
        var s = App.Services.Config.Settings.Events;
        var hex = !string.IsNullOrWhiteSpace(e.Color) ? e.Color : e.Kind switch { EventKind.Timer => s.TimerColor, EventKind.Task => s.TaskColor, _ => s.CalendarColor };
        return Rgb.TryParse(hex, out var rgb) ? rgb.Hex : null;
    }

    private static IBrush KindBrush(EventKind kind) => Ui.Tk(kind switch { EventKind.Timer => "warn", EventKind.Task => "purple", _ => "accent" });
    private static IBrush ItemBrush(EventItem e) => CustomColor(e) is { } hex ? Ui.Raw(hex) : KindBrush(e.Kind);
    /// <summary>Text colour that stays readable on an item's colour.</summary>
    private static IBrush ItemText(EventItem e) => CustomColor(e) is { } hex ? (Rgb.Parse(hex).Luminance > 0.55 ? Brushes.Black : Brushes.White) : Ui.OnAccent;
    private static string KindName(EventKind kind) => kind switch { EventKind.Timer => "Timer", EventKind.Task => "Task", _ => "Event" };

    private static string NextText(EventItem e)
    {
        if (e.Completed) return "finished";
        if (!e.Enabled) return "disabled";
        var now = DateTime.Now;
        var running = RecurrenceCalculator.Between(e, now, now).FirstOrDefault(o => o.End is not null && o.Start <= now);
        if (running is not null) return $"happening now · ends {Ui.Until(running.End!.Value)}";
        return Events.NextOccurrence(e) is { } next ? Ui.Until(next.Start) + (next.Start - now > TimeSpan.FromHours(24) ? "" : $" ({next.Start:HH:mm})") : "no more occurrences";
    }

    private void Rebuild()
    {
        _inProgressKey = InProgressKey(DateTime.Now);
        for (var i = 0; i < _viewTabs.Length; i++) _viewTabs[i].Classes.Set("on", (int)_view == i);
        var unit = _view switch { CalendarView.Month => "month", CalendarView.Week => "week", _ => "day" };
        ToolTip.SetTip(_prev, "Previous " + unit);
        ToolTip.SetTip(_next, "Next " + unit);
        _nowLine = null;
        if (_view == CalendarView.Month) BuildCalendar();
        else BuildTimeGrid(_view == CalendarView.Week ? WeekStart(_selectedDay) : _selectedDay.Date, _view == CalendarView.Week ? 7 : 1);
        BuildSide();
    }

    /// <summary>Weekend days: the cell colour nudged toward the theme's deep background, so it fits every theme.</summary>
    private static IBrush WeekendBrush() =>
        Ui.Raw(Rgb.Parse(Ui.Theme["cell"]).Mix(Rgb.Parse(Ui.Theme["bgDeep"]), 0.3).Hex);

    private static string WeekLabel(DateTime first)
    {
        var last = first.AddDays(6);
        return first.Year != last.Year ? $"{first:d MMM yyyy} – {last:d MMM yyyy}"
            : first.Month != last.Month ? $"{first:d MMM} – {last:d MMM yyyy}"
            : $"{first.Day} – {last:d MMMM yyyy}";
    }

    /// <summary>Opens an event for editing on a double-click, as on the month calendar.</summary>
    private void EditOnDoubleClick(Control target, EventItem item, DateTime day)
    {
        target.PointerPressed += (_, e) =>
        {
            if (e.ClickCount < 2 || !e.GetCurrentPoint(target).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            _selectedDay = day;
            _ = EditAsync(item, item.Kind, null);
        };
    }

    private static string Tip(EventItem item, Occurrence occ)
    {
        var when = item.AllDay ? "all day"
            : occ.End is { } end ? (occ.Start.Date == end.Date ? $"{occ.Start:HH:mm}–{end:HH:mm}" : $"{occ.Start:ddd d MMM HH:mm} → {end:ddd d MMM HH:mm}")
            : occ.Start.ToString("HH:mm");
        return item.Title + "\n" + when + (string.IsNullOrWhiteSpace(item.Location) ? "" : "\n" + item.Location) + "\nDouble-click to edit"
            + (item.Recurrence.Freq == RecurrenceFreq.None ? "" : ", right-click to skip");
    }

    /// <summary>Right-click menu that skips this occurrence of a repeating event, as on the month calendar.</summary>
    private void SkipMenu(Control target, EventItem item, Occurrence occ)
    {
        if (item.Recurrence.Freq == RecurrenceFreq.None) return;
        var skipDay = DateOnly.FromDateTime(occ.Start);
        target.ContextMenu = new ContextMenu { Items = { Ui.Item($"Skip this occurrence ({skipDay:MMM d})", () => { Events.Skip(item, [skipDay]); Events.Save(); }) } };
    }

    /// <summary>Whether an occurrence goes in the strip above the hours: all-day events and anything a day or longer.</summary>
    private static bool IsBanner(EventItem item, Occurrence occ) => item.AllDay || occ.End is { } end && end - occ.Start >= TimeSpan.FromDays(1);

    /// <summary>
    /// The week and day views: one column per day, hours running down, events drawn as blocks at their times.
    /// Events that overlap share the column side by side. All-day and multi-day events sit in a strip above.
    /// </summary>
    private void BuildTimeGrid(DateTime first, int days)
    {
        _monthLabel.Text = days == 1 ? first.ToString("dddd, d MMMM yyyy") : WeekLabel(first);
        var last = first.AddDays(days);
        var agenda = Events.Agenda(first, last.AddTicks(-1));
        var now = DateTime.Now;
        var columns = $"{GutterWidth}," + string.Join(",", Enumerable.Repeat("*", days));

        // Day headers. In the week view a click on one opens that day.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(0, 0, 0, 4) };
        for (var d = 0; d < days; d++)
        {
            var day = first.AddDays(d);
            var isToday = day == DateTime.Today;
            var number = new TextBlock { Text = day.Day.ToString(), FontSize = 15, FontWeight = isToday ? FontWeight.Bold : FontWeight.SemiBold, Foreground = isToday ? Ui.OnAccent : Ui.Tk("textSoft") };
            var label = Ui.Row(6,
                Ui.Apply(Ui.Text(day.ToString("ddd"), "dim", 12), t => t.VerticalAlignment = VerticalAlignment.Center),
                isToday ? new Border { Background = Ui.Tk("accent"), CornerRadius = Ui.R(11), Padding = new Thickness(7, 0), Child = number, Classes = { "glow" } } : number);
            Control cell = label;
            if (days > 1)
            {
                var open = new Button { Content = label, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.Hand) };
                ToolTip.SetTip(open, "Show " + day.ToString("dddd d MMMM"));
                var target = day;
                open.Click += (_, _) => SetView(CalendarView.Day, target);
                cell = open;
            }
            else cell.Margin = new Thickness(6, 2);
            header.Children.Add(cell.At(0, 1 + d));
        }

        // All-day and multi-day events: one lane per row, each spanning the days it covers.
        var banners = agenda.Where(a => IsBanner(a.Item, a.Occurrence)).ToList();
        var strip = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(0, 0, 0, 4) };
        var laneEnds = new List<int>();
        foreach (var (item, occ) in banners)
        {
            var from = Math.Max(0, (int)(occ.Start.Date - first).TotalDays);
            // An event that ends at midnight does not reach into the next day.
            var lastMoment = occ.EffectiveEnd > occ.Start ? occ.EffectiveEnd.AddTicks(-1) : occ.EffectiveEnd;
            var to = Math.Min(days - 1, (int)((lastMoment.Date - first).TotalDays));
            if (to < from) continue;
            var lane = laneEnds.FindIndex(end => end < from);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(to); strip.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }
            else laneEnds[lane] = to;
            var chip = new Border
            {
                Background = ItemBrush(item), CornerRadius = Ui.R(3), Padding = new Thickness(6, 1), Margin = new Thickness(2, 1),
                Opacity = item.Enabled && !item.Completed ? 0.95 : 0.4, Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = (occ.Start < first ? "‹ " : "") + item.Title + (occ.EffectiveEnd >= last ? " ›" : ""), FontSize = 11.5, Foreground = ItemText(item), TextTrimming = TextTrimming.CharacterEllipsis },
            };
            ToolTip.SetTip(chip, Tip(item, occ));
            EditOnDoubleClick(chip, item, occ.Start.Date < first ? first : occ.Start.Date);
            SkipMenu(chip, item, occ);
            strip.Children.Add(chip.At(lane, 1 + from, 1, to - from + 1));
        }
        if (laneEnds.Count > 0)
            strip.Children.Add(Ui.Apply(Ui.Text("all day", "dim", 10.5), t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); t.HorizontalAlignment = HorizontalAlignment.Right; }).At(0, 0, laneEnds.Count));
        _timeStrip.IsVisible = laneEnds.Count > 0;

        // The hours.
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Height = 24 * HourHeight, Margin = new Thickness(0, 8, 0, 8) };
        for (var h = 0; h < 24; h++)
        {
            body.Children.Add(new Border { BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, h * HourHeight, 0, 0), IsHitTestVisible = false }.At(0, 1, 1, days));
            if (h > 0)
                body.Children.Add(new TextBlock
                {
                    Text = $"{h:00}:00", FontSize = 10.5, Foreground = Ui.Dim, Width = GutterWidth - 8, TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, h * HourHeight - 7, 8, 0),
                });
        }

        for (var d = 0; d < days; d++)
        {
            var day = first.AddDays(d);
            var dayEnd = day.AddDays(1);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var selected = days > 1 && day == _selectedDay;
            var column = new Grid
            {
                Background = selected ? Ui.Tk("cellSel") : weekend ? WeekendBrush() : Brushes.Transparent,
                ClipToBounds = true,
            };
            var columnBorder = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, d == days - 1 ? 1 : 0, 0), Child = column };
            ToolTip.SetTip(columnBorder, "Double-click an event to edit it, or an empty spot to add one at that time");
            column.PointerPressed += (_, e) =>
            {
                if (e.Handled || !e.GetCurrentPoint(column).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount >= 2)
                {
                    // Start on the half hour that was clicked.
                    var minutes = Math.Clamp(Math.Floor(e.GetPosition(column).Y / HourHeight * 2) * 30, 0, 23.5 * 60);
                    _selectedDay = day;
                    _ = EditAsync(null, EventKind.Calendar, day, TimeSpan.FromMinutes(minutes));
                }
                else if (_selectedDay != day) { _selectedDay = day; Rebuild(); }
            };

            // The pieces of timed events that fall on this day, laid out in clusters of overlapping events.
            var pieces = agenda.Where(a => !IsBanner(a.Item, a.Occurrence) && a.Occurrence.Start < dayEnd && (a.Occurrence.End ?? a.Occurrence.Start) >= day
                                           && !(a.Occurrence.End is { } en && en == day && a.Occurrence.Start < day))
                .Select(a =>
                {
                    var start = a.Occurrence.Start < day ? day : a.Occurrence.Start;
                    var end = a.Occurrence.End is { } e && e < dayEnd ? e : a.Occurrence.End is null ? start : dayEnd;
                    // Events without an end, and very short ones, still get a block big enough to read.
                    var shown = Math.Max((end - start).TotalHours, 0.5);
                    var top = (start - day).TotalHours * HourHeight;
                    return (a.Item, a.Occurrence, Top: top, Bottom: Math.Min(24 * HourHeight, top + shown * HourHeight));
                })
                .OrderBy(p => p.Top).ThenByDescending(p => p.Bottom).ToList();
            var i = 0;
            while (i < pieces.Count)
            {
                var cluster = new List<(EventItem Item, Occurrence Occ, double Top, double Bottom, int Lane)>();
                var lanes = new List<double>();
                var clusterTop = pieces[i].Top;
                var clusterBottom = pieces[i].Bottom;
                for (; i < pieces.Count && (cluster.Count == 0 || pieces[i].Top < clusterBottom); i++)
                {
                    var p = pieces[i];
                    var lane = lanes.FindIndex(bottom => bottom <= p.Top);
                    if (lane < 0) { lane = lanes.Count; lanes.Add(p.Bottom); } else lanes[lane] = p.Bottom;
                    cluster.Add((p.Item, p.Occurrence, p.Top, p.Bottom, lane));
                    clusterBottom = Math.Max(clusterBottom, p.Bottom);
                }
                var group = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", lanes.Count))),
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, clusterTop, 0, 0), Height = clusterBottom - clusterTop,
                };
                foreach (var (item, occ, top, bottom, lane) in cluster)
                    group.Children.Add(Ui.Apply(TimeBlock(item, occ, day, bottom - top, occ.Start <= now && occ.End is { } oe && oe > now),
                        b => b.Margin = new Thickness(1, top - clusterTop + 1, 2, 0)).At(0, lane));
                column.Children.Add(group);
            }

            if (day == DateTime.Today)
            {
                var line = new Border
                {
                    Height = 2, Background = Ui.Tk("error"), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                    Margin = new Thickness(0, Math.Max(0, (now - day).TotalHours * HourHeight - 1), 0, 0),
                };
                ToolTip.SetTip(line, "Now");
                column.Children.Add(line);
                _nowLine = (line, day);
            }
            body.Children.Add(columnBorder.At(0, 1 + d));
        }

        _timeHeader.Child = header;
        _timeStrip.Child = strip;
        _timeScroll.Content = body;
        _calendarHost.Child = _timeLayout;

        if (_scrollPending)
        {
            _scrollPending = false;
            // Today: a little before now. Otherwise the first timed event, or the start of a working day.
            var timed = agenda.Where(a => !IsBanner(a.Item, a.Occurrence) && a.Occurrence.Start >= first).Select(a => a.Occurrence.Start.TimeOfDay).DefaultIfEmpty(TimeSpan.FromHours(8)).Min();
            var hour = DateTime.Today >= first && DateTime.Today < last ? now.TimeOfDay.TotalHours - 2 : Math.Min(timed.TotalHours, 8) - 0.5;
            _scrollTo = Math.Max(0, hour * HourHeight);
        }
    }

    /// <summary>One event in the week or day view.</summary>
    private Border TimeBlock(EventItem item, Occurrence occ, DateTime day, double height, bool happening)
    {
        var time = occ.End is { } end
            ? $"{(occ.Start < day ? "‹ " : "")}{occ.Start:HH:mm}–{end:HH:mm}{(end > day.AddDays(1) ? " ›" : "")}"
            : occ.Start.ToString("HH:mm");
        var text = new StackPanel { Spacing = 0 };
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = ItemText(item), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = height > 40 ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxHeight = Math.Max(14, height - 18) });
        if (height >= 30) text.Children.Add(new TextBlock { Text = time, FontSize = 10.5, Foreground = ItemText(item), Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis });
        if (height >= 58 && !string.IsNullOrWhiteSpace(item.Location))
            text.Children.Add(new TextBlock { Text = item.Location, FontSize = 10.5, Foreground = ItemText(item), Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis });
        var block = new Border
        {
            Background = ItemBrush(item), CornerRadius = Ui.R(4), Padding = new Thickness(5, 2), Height = Math.Max(16, height - 2), VerticalAlignment = VerticalAlignment.Top,
            Opacity = item.Enabled && !item.Completed ? 0.95 : 0.45, ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Hand), Child = text,
            BorderBrush = happening ? Ui.Strong : null, BorderThickness = new Thickness(happening ? 1.5 : 0),
        };
        if (happening) block.Classes.Add("glow");
        ToolTip.SetTip(block, Tip(item, occ));
        EditOnDoubleClick(block, item, day);
        SkipMenu(block, item, occ);
        return block;
    }

    private void BuildCalendar()
    {
        _monthLabel.Text = _month.ToString("MMMM yyyy");
        _calendarHost.Child = _calendar;
        _calendar.Children.Clear();
        for (var c = 0; c < 7; c++)
            _calendar.Children.Add(new TextBlock { Text = Week[c].ToString()[..3], Foreground = Ui.Dim, FontSize = 12, Margin = new Thickness(6, 2, 0, 6) }.At(0, c));

        var first = _month.AddDays(-(((int)_month.DayOfWeek + 6) % 7));
        var agenda = Events.Agenda(first, first.AddDays(42));
        for (var i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            var dayEnd = day.AddDays(1).AddTicks(-1);
            var todays = agenda.Where(a => a.Occurrence.Start <= dayEnd && a.Occurrence.EffectiveEnd >= day)
                .GroupBy(a => a.Item.Id).Select(g => (g.First().Item, First: g.First().Occurrence, Count: g.Count())).ToList();
            var inMonth = day.Month == _month.Month;
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var cell = new StackPanel { Spacing = 2 };
            var number = new TextBlock { Text = day.Day.ToString(), FontSize = 12, Foreground = day == DateTime.Today ? Ui.OnAccent : inMonth ? Ui.Tk("textSoft") : Ui.Tk("ghost"), FontWeight = day == DateTime.Today ? FontWeight.Bold : FontWeight.Normal };
            cell.Children.Add(day == DateTime.Today
                ? new Border { Background = Ui.Tk("accent"), CornerRadius = Ui.R(9), Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = number, Classes = { "glow" } }
                : number);
            foreach (var (item, occ, count) in todays.Take(3))
            {
                var label = (count == 1 && occ.Start.Date == day && !item.AllDay ? occ.Start.ToString("HH:mm") + " " : "") + item.Title + (count > 1 ? $" ×{count}" : "");
                var chip = new Border
                {
                    Background = ItemBrush(item), CornerRadius = Ui.R(3), Padding = new Thickness(4, 0), Opacity = item.Enabled && !item.Completed ? 0.95 : 0.4, Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new TextBlock { Text = label, FontSize = 11, Foreground = ItemText(item), TextTrimming = TextTrimming.CharacterEllipsis },
                };
                ToolTip.SetTip(chip, item.Title + (string.IsNullOrWhiteSpace(item.Location) ? "" : "\n" + item.Location) + "\nDouble-click to edit" + (item.Recurrence.Freq == RecurrenceFreq.None ? "" : ", right-click to skip"));
                if (item.Recurrence.Freq != RecurrenceFreq.None)
                {
                    var skipDay = DateOnly.FromDateTime(occ.Start);
                    chip.ContextMenu = new ContextMenu { Items = { Ui.Item($"Skip this occurrence ({skipDay:MMM d})", () => { Events.Skip(item, [skipDay]); Events.Save(); }) } };
                }
                // A double-click on an event edits that event; anywhere else in the cell it adds a new one.
                chip.PointerPressed += (_, e) =>
                {
                    if (e.ClickCount < 2 || !e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    _selectedDay = day;
                    _ = EditAsync(item, item.Kind, null);
                };
                cell.Children.Add(chip);
            }
            if (todays.Count > 3) cell.Children.Add(new TextBlock { Text = $"+{todays.Count - 3} more", FontSize = 10.5, Foreground = Ui.Dim });
            var border = new Border
            {
                Background = day == _selectedDay ? Ui.Tk("cellSel") : inMonth ? (weekend ? WeekendBrush() : Ui.Tk("cell")) : Brushes.Transparent,
                BorderBrush = day == _selectedDay ? Ui.Tk("accent") : Ui.Line, BorderThickness = new Thickness(1), CornerRadius = Ui.R(4),
                Margin = new Thickness(2), Padding = new Thickness(5, 3), Child = cell, ClipToBounds = true, MinHeight = 56,
            };
            if (day == _selectedDay) border.Classes.Add("glow");
            border.PointerPressed += (_, e) =>
            {
                if (e.Handled) return;
                _selectedDay = day;
                if (e.ClickCount >= 2) _ = EditAsync(null, EventKind.Calendar, day);
                else Rebuild();
            };
            ToolTip.SetTip(border, "Double-click an event to edit it, or an empty spot to add one on this day");
            _calendar.Children.Add(border.At(1 + i / 7, i % 7));
        }
    }

    private void BuildSide()
    {
        _countdowns.Clear();
        _live.Clear();
        _side.Children.Clear();
        if (RunningNow() is { } running) _side.Children.Add(running);

        var dayPanel = new StackPanel { Spacing = 8, Children = { Ui.Text(_selectedDay == DateTime.Today ? "Today" : _selectedDay.ToString("dddd, d MMMM yyyy"), "h2") } };
        var dayItems = Events.Agenda(_selectedDay, _selectedDay.AddDays(1).AddTicks(-1));
        if (dayItems.Count == 0) dayPanel.Children.Add(Ui.Text("Nothing scheduled.", "dim", 12.5));
        foreach (var group in dayItems.GroupBy(a => a.Item.Id))
        {
            var (item, occ) = group.First();
            var time = item.AllDay ? "all day"
                : occ.End is { } end ? (occ.Start.Date == end.Date ? $"{occ.Start:HH:mm}–{end:HH:mm}" : $"{occ.Start:MMM d HH:mm} → {end:MMM d HH:mm}")
                : occ.Start.ToString("HH:mm");
            if (group.Count() > 1) time += $" (+{group.Count() - 1} more)";
            dayPanel.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new Border { Width = 4, CornerRadius = Ui.R(2), Background = ItemBrush(item), Margin = new Thickness(0, 2, 8, 2) },
                    DayEntry(item, time).At(0, 1),
                },
            });
        }
        _side.Children.Add(Ui.Card(dayPanel));

        var all = Events.Items;
        var active = all.Where(e => !e.Completed).OrderBy(e => Events.NextOccurrence(e)?.Start ?? DateTime.MaxValue).ToList();
        var done = all.Where(e => e.Completed).OrderByDescending(e => e.Start).ToList();
        _side.Children.Add(Ui.Text($"Scheduled ({active.Count})", "h2"));
        if (active.Count == 0) _side.Children.Add(Ui.Text("No timers, tasks or events. Use the buttons above to add one.", "dim", 12.5));
        foreach (var e in active) _side.Children.Add(ItemCard(e));
        if (done.Count > 0)
        {
            _side.Children.Add(new DockPanel
            {
                Children =
                {
                    Ui.TextButton("Clear", () => { foreach (var e in done) Ui.Try(this, () => Events.Remove(e.Id)); }, "Delete everything that has finished").Dock(Dock.Right),
                    Ui.Apply(Ui.Text($"Finished ({done.Count})", "h2"), t => t.VerticalAlignment = VerticalAlignment.Center),
                },
            });
            foreach (var e in done.Take(30)) _side.Children.Add(ItemCard(e));
        }
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{Math.Max(0, t.Seconds)}s";

    /// <summary>
    /// The "Running now" segment above the day: events in progress and event scripts still executing in the
    /// background, each with how long it has been going. Null when nothing is running.
    /// </summary>
    private Control? RunningNow()
    {
        var now = DateTime.Now;
        var occurrences = Events.InProgress(now);
        var scripts = Events.RunningScripts;
        if (occurrences.Count == 0 && scripts.Count == 0) return null;

        var panel = new StackPanel { Spacing = 10 };
        var count = occurrences.Count + scripts.Count(r => occurrences.All(o => o.Item.Id != r.Item.Id));
        panel.Children.Add(Ui.Row(8,
            Ui.Apply(Ui.Dot("ok", 9), d => { d.VerticalAlignment = VerticalAlignment.Center; d.Classes.Add("glow"); }),
            Ui.Text(count == 1 ? "Running now" : $"Running now ({count})", "h2")));

        Control ScriptRow(RunningScript run)
        {
            var elapsed = new TextBlock { FontSize = 11.5, Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center };
            void Update() => elapsed.Text = run.Stopping ? "stopping…" : Elapsed(DateTime.Now - run.Started);
            Update();
            _live.Add(Update);
            var stop = Ui.IconButton(Icons.Stop, "Stop this script", () => { Events.Stop(run); Update(); });
            stop.IsEnabled = !run.Stopping;
            return new DockPanel
            {
                Children =
                {
                    Ui.Row(4, elapsed, stop).Dock(Dock.Right),
                    new TextBlock
                    {
                        Text = $"{(run.Phase == "end" ? "on end" : "on start")}: {run.Script.ReplaceLineEndings(" ⏎ ")}", FontFamily = Ui.Mono, FontSize = 11.5,
                        Foreground = Ui.Tk("ok"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
        }

        Control Entry(EventItem item, Control details)
        {
            var title = new DockPanel
            {
                Children =
                {
                    Ui.Apply(Ui.Chip(KindName(item.Kind), ItemBrush(item)), c => { ((TextBlock)c.Child!).Foreground = ItemText(item); c.Margin = new Thickness(8, 0, 0, 0); }).Dock(Dock.Right),
                    Ui.Apply(Ui.Link(item.Title, () => _ = EditAsync(item, item.Kind, null), "Edit"), b => b.HorizontalAlignment = HorizontalAlignment.Left),
                },
            };
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new Border { Width = 4, CornerRadius = Ui.R(2), Background = ItemBrush(item), Margin = new Thickness(0, 2, 8, 2) },
                    Ui.Column(3, title, details).At(0, 1),
                },
            };
        }

        foreach (var (item, occ) in occurrences)
        {
            var end = occ.End!.Value;
            var details = new StackPanel { Spacing = 3 };
            var when = new TextBlock { FontSize = 12, Foreground = Ui.Tk("textSoft"), TextTrimming = TextTrimming.CharacterEllipsis };
            var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, Foreground = ItemBrush(item) };
            void Update()
            {
                var t = DateTime.Now;
                var started = occ.Start.Date == t.Date ? occ.Start.ToString("HH:mm") : occ.Start.ToString("ddd d MMM HH:mm");
                var ends = end.Date == t.Date ? end.ToString("HH:mm") : end.ToString("ddd d MMM HH:mm");
                when.Text = $"since {started} ({Elapsed(t - occ.Start)}) · ends {Ui.Until(end)} ({ends})";
                progress.Value = Math.Clamp((t - occ.Start) / (end - occ.Start), 0, 1);
            }
            Update();
            _live.Add(Update);
            details.Children.Add(when);
            details.Children.Add(progress);
            if (!string.IsNullOrWhiteSpace(item.Location)) details.Children.Add(LocationLine(item.Location!));
            foreach (var run in scripts.Where(r => r.Item.Id == item.Id)) details.Children.Add(ScriptRow(run));
            panel.Children.Add(Entry(item, details));
        }
        // Scripts of events that are not themselves in progress: a task or timer that triggered, an on-end script, "run now".
        foreach (var group in scripts.Where(r => occurrences.All(o => o.Item.Id != r.Item.Id)).GroupBy(r => r.Item.Id))
        {
            var details = new StackPanel { Spacing = 3 };
            foreach (var run in group) details.Children.Add(ScriptRow(run));
            panel.Children.Add(Entry(group.First().Item, details));
        }

        var card = Ui.Card(panel);
        card.BorderBrush = Ui.Tk("accent");
        card.Classes.Add("glow");
        return card;
    }

    private Control ItemCard(EventItem e)
    {
        var next = new TextBlock { Text = NextText(e), FontSize = 12, Foreground = Ui.Tk("textSoft") };
        if (!e.Completed) _countdowns.Add((next, e));
        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new DockPanel
        {
            Children =
            {
                new Border
                {
                    Background = ItemBrush(e), CornerRadius = Ui.R(3), Padding = new Thickness(5, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = KindName(e.Kind), FontSize = 10.5, Foreground = ItemText(e) },
                }.Dock(Dock.Right),
                new TextBlock { Text = e.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });
        info.Children.Add(next);
        var when = e.AllDay ? e.Start.ToString("ddd yyyy-MM-dd") : e.Start.ToString("ddd yyyy-MM-dd HH:mm");
        if (e.End is { } end) when += e.AllDay ? (end.Date > e.Start.Date ? $" → {end:ddd yyyy-MM-dd}" : "") : $" → {(end.Date == e.Start.Date ? end.ToString("HH:mm") : end.ToString("ddd yyyy-MM-dd HH:mm"))}";
        info.Children.Add(Ui.Text($"{RecurrenceCalculator.Describe(e.Recurrence)} · {when}", "dim", 11.5));
        if (!string.IsNullOrWhiteSpace(e.Location)) info.Children.Add(LocationLine(e.Location!));
        if (e.Attachments.Count > 0) info.Children.Add(AttachmentLine(e));
        if (!string.IsNullOrWhiteSpace(e.OnStartScript)) info.Children.Add(ScriptLine("on start", e.OnStartScript!));
        if (!string.IsNullOrWhiteSpace(e.OnEndScript)) info.Children.Add(ScriptLine("on end", e.OnEndScript!));
        if (!string.IsNullOrWhiteSpace(e.LastRunSummary)) info.Children.Add(Ui.Text("last run " + e.LastRunSummary, "dim", 11.5));

        var enabled = new CheckBox { IsChecked = e.Enabled, Content = "Enabled", FontSize = 12, MinHeight = 0, Padding = new Thickness(4, 0), IsVisible = !e.Completed };
        enabled.IsCheckedChanged += (_, _) => { e.Enabled = enabled.IsChecked == true; Events.Save(); };
        var actions = new DockPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                Ui.Row(2,
                    Ui.Apply(Ui.IconButton(Icons.Play, "Run the on-start script now", () => _ = RunAsync(e)), b => b.IsVisible = !string.IsNullOrWhiteSpace(e.OnStartScript)),
                    Ui.IconButton(Icons.Pencil, "Edit", () => _ = EditAsync(e, e.Kind, null)),
                    Ui.IconButton(Icons.Delete, "Delete", () => _ = DeleteAsync(e))).Dock(Dock.Right),
                enabled,
            },
        };
        info.Children.Add(actions);
        var card = Ui.Card(info);
        card.Padding = new Thickness(12, 10, 8, 6);
        card.Opacity = e.Completed || !e.Enabled ? 0.6 : 1;
        return card;
    }

    private static Control ScriptLine(string label, string script) => new TextBlock
    {
        Text = $"{label}: {script.ReplaceLineEndings(" ⏎ ")}", FontFamily = Ui.Mono, FontSize = 11.5, Foreground = Ui.Tk("ok"), TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static bool IsLink(string text) => text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A location is free text; when it is a link it can be clicked.</summary>
    private static Control LocationLine(string location)
    {
        var text = location.Trim();
        Control value = IsLink(text)
            ? Ui.Apply(Ui.Link(text, () => Shell.Open(text), "Open in the browser"), b => ((TextBlock)b.Content!).FontSize = 12)
            : new SelectableTextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Tk("textSoft") };
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("18,*"),
            Children = { new PathIcon { Data = Icons.MapMarker, Width = 12, Height = 12, Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) }, value.At(0, 1) },
        };
    }

    private static Control AttachmentLine(EventItem e)
    {
        var files = new WrapPanel();
        foreach (var name in e.Attachments)
        {
            var path = Events.AttachmentPath(e, name);
            files.Children.Add(Ui.Apply(Ui.Link(name, () => Shell.Open(path), "Open " + path), b => { ((TextBlock)b.Content!).FontSize = 12; b.Margin = new Thickness(0, 0, 10, 0); }));
        }
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("18,*"),
            Children = { new PathIcon { Data = Icons.Paperclip, Width = 12, Height = 12, Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) }, files.At(0, 1) },
        };
    }

    /// <summary>One line of the selected day's panel: title, time, and where, what and which files if the event has them.</summary>
    private Control DayEntry(EventItem item, string time)
    {
        var column = Ui.Column(2, Ui.Apply(Ui.Link(item.Title, () => _ = EditAsync(item, item.Kind, null)), b => b.HorizontalAlignment = HorizontalAlignment.Left), Ui.Text(time, "dim", 12));
        if (!string.IsNullOrWhiteSpace(item.Location)) column.Children.Add(LocationLine(item.Location!));
        if (!string.IsNullOrWhiteSpace(item.Notes)) column.Children.Add(Ui.Apply(MarkdownView.Build(item.Notes, 12.5), c => c.Margin = new Thickness(0, 3, 0, 0)));
        if (item.Attachments.Count > 0) column.Children.Add(AttachmentLine(item));
        return column;
    }

    private async Task RunAsync(EventItem e)
    {
        Ui.Toast(this, "Running script", e.Title);
        await Ui.TryAsync(this, async () =>
        {
            var r = await Events.RunScriptAsync(e, "start", new Occurrence(DateTime.Now, null), e.OnStartScript!);
            Ui.Toast(this, r.TimedOut ? "Script timed out" : $"Script finished (exit {r.ExitCode})", "Output is saved under Logs → scripts.");
        });
    }

    private async Task DeleteAsync(EventItem e)
    {
        if (await Ui.ConfirmAsync(Ui.Owner(this), "Delete", $"Delete \"{e.Title}\"?")) Ui.Try(this, () => Events.Remove(e.Id));
    }

    private async Task TimerAsync()
    {
        var duration = new TextBox { Text = "5m", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var title = new TextBox { PlaceholderText = "Optional" };
        var script = new TextBox { PlaceholderText = "Optional shell script to run when the timer goes off", Classes = { "mono" } };
        await Ui.FormAsync(Ui.Owner(this), "Start a timer", Ui.Column(12,
            Ui.Field("Duration", duration, "For example 90s, 5m, 1h30m or 2d. A bare number means minutes."),
            Ui.Field("Title", title),
            Ui.Field("Run when it goes off", script)), () =>
        {
            var d = TimeParsing.ParseDuration(duration.Text ?? "");
            if (d <= TimeSpan.Zero) return Task.FromResult<string?>("The duration must be longer than zero.");
            Events.AddTimer(string.IsNullOrWhiteSpace(title.Text) ? $"Timer {RecurrenceCalculator.FormatMinutes(d.TotalMinutes)}" : title.Text.Trim(), d,
                string.IsNullOrWhiteSpace(script.Text) ? null : script.Text);
            return Task.FromResult<string?>(null);
        }, "Start", 460);
    }

    private static TimeSpan ParseClock(string? text, string what)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return TimeSpan.Zero;
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var d)) return d.TimeOfDay;
        throw new ArgumentException($"{what}: could not understand the time \"{t}\". Use 24-hour HH:mm, e.g. 17:30.");
    }

    private async Task EditAsync(EventItem? existing, EventKind defaultKind, DateTime? day, TimeSpan? time = null)
    {
        var e = existing;
        var start0 = e?.Start ?? (day is { } dd ? dd.Date + (time ?? TimeSpan.FromHours(9)) : RoundUp(DateTime.Now.AddMinutes(5)));
        var title = new TextBox { Text = e?.Title ?? "" };
        var kind = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Items = { "Calendar event", "Scheduled task", "Timer" } };
        kind.SelectedIndex = (e?.Kind ?? defaultKind) switch { EventKind.Calendar => 0, EventKind.Task => 1, _ => 2 };
        var startDate = new CalendarDatePicker { SelectedDate = start0.Date, MinWidth = 170 };
        var startTime = new TextBox { Text = start0.ToString("HH:mm"), Width = 90 };
        var allDay = new CheckBox { Content = "All day", IsChecked = e?.AllDay ?? false };
        var hasEnd = new CheckBox { Content = "Has an end", IsChecked = e?.End is not null };
        var end0 = e?.End ?? start0.AddHours(1);
        var endDate = new CalendarDatePicker { SelectedDate = end0.Date, MinWidth = 170 };
        var endTime = new TextBox { Text = end0.ToString("HH:mm"), Width = 90 };
        var endRow = Ui.Row(8, endDate, endTime);

        var r = e?.Recurrence ?? new Recurrence();
        var repeat = new ComboBox { MinWidth = 200, Items = { "Does not repeat", "Fixed interval", "Daily", "Weekly", "Monthly", "Yearly" } };
        repeat.SelectedIndex = (int)r.Freq;
        var every = new TextBox { Width = 110, Text = r.Freq == RecurrenceFreq.Interval ? RecurrenceCalculator.FormatMinutes(r.IntervalMinutes) : Math.Max(1, r.Interval).ToString() };
        var everyUnit = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Dim };
        var everyRow = Ui.Row(8, Ui.Apply(Ui.Text("Every"), t => t.VerticalAlignment = VerticalAlignment.Center), every, everyUnit);
        var dayToggles = Week.Select(d => new ToggleButton { Content = d.ToString()[..3], IsChecked = r.Days.Contains(d), Tag = d, Padding = new Thickness(8, 4), FontSize = 12 }).ToList();
        var daysRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var t in dayToggles) daysRow.Children.Add(t);
        var until = new CalendarDatePicker { SelectedDate = r.Until?.Date, MinWidth = 170, PlaceholderText = "No end date" };
        var count = new TextBox { Text = r.Count?.ToString() ?? "", Width = 90, PlaceholderText = "∞" };
        var endsRow = Ui.Row(8, Ui.Apply(Ui.Text("Until"), t => t.VerticalAlignment = VerticalAlignment.Center), until,
            Ui.Apply(Ui.Text("or after"), t => t.VerticalAlignment = VerticalAlignment.Center), count, Ui.Apply(Ui.Text("times"), t => t.VerticalAlignment = VerticalAlignment.Center));
        var onStart = new TextBox { Text = e?.OnStartScript ?? "", AcceptsReturn = true, Classes = { "mono" }, MinHeight = 50, TextWrapping = TextWrapping.Wrap };
        var onEnd = new TextBox { Text = e?.OnEndScript ?? "", AcceptsReturn = true, Classes = { "mono" }, MinHeight = 50, TextWrapping = TextWrapping.Wrap };
        var onEndField = Ui.Field("Run when it ends", onEnd, "Needs an end time.");
        var location = new TextBox { Text = e?.Location ?? "", PlaceholderText = "Optional: an address, a link, a room…" };

        // Run-on-start: the script box shows when it has text or the switch is on, and the switch cannot be
        // turned off while there is a script (clearing the text is how a script is removed).
        var runStart = new ToggleSwitch { IsChecked = !string.IsNullOrWhiteSpace(e?.OnStartScript) || (e is null && defaultKind == EventKind.Task), OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, -6, 0, -6), VerticalAlignment = VerticalAlignment.Center };
        var onStartHelp = new TextBlock { Text = "Shell script. It receives AVAMUX_EVENT_TITLE, AVAMUX_EVENT_PHASE, AVAMUX_EVENT_START and friends.", Foreground = Ui.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        void SyncRunStart()
        {
            var hasText = !string.IsNullOrWhiteSpace(onStart.Text);
            if (hasText && runStart.IsChecked != true) runStart.IsChecked = true;
            onStart.IsVisible = onStartHelp.IsVisible = hasText || runStart.IsChecked == true;
            ToolTip.SetTip(runStart, hasText ? "Clear the script to switch this off" : "Run a script when this starts or triggers");
        }
        runStart.IsCheckedChanged += (_, _) => { SyncRunStart(); if (runStart.IsChecked == true && onStart.IsVisible) onStart.Focus(); };
        onStart.TextChanged += (_, _) => SyncRunStart();
        SyncRunStart();
        var onStartField = Ui.Column(4,
            Ui.Row(8, runStart, new TextBlock { Text = "Run when it starts / triggers", FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }),
            onStart, onStartHelp);

        // Description: markdown, with a preview whose on/off state belongs to the dialog, not to the event.
        var notes = new TextBox { Text = e?.Notes ?? "", AcceptsReturn = true, AcceptsTab = false, MinHeight = 70, MaxHeight = 220, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Markdown: **bold**, lists, [links](https://…)" };
        var previewHost = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = Ui.R(5), Padding = new Thickness(10, 8), Background = Ui.Tk("bg") };
        var preview = new ToggleSwitch { IsChecked = App.Services.Ui.Data.EventDescriptionPreview, OnContent = "Preview", OffContent = "Preview", MinWidth = 0, Margin = new Thickness(0, -6, 0, -6), FontSize = 12 };
        void RenderNotes()
        {
            previewHost.IsVisible = preview.IsChecked == true;
            if (preview.IsChecked != true) return;
            previewHost.Child = string.IsNullOrWhiteSpace(notes.Text) ? Ui.Text("Nothing to preview yet.", "dim", 12) : MarkdownView.Build(notes.Text);
        }
        var renderSoon = Ui.Debounced(RenderNotes, 180);
        notes.TextChanged += (_, _) => renderSoon();
        preview.IsCheckedChanged += (_, _) =>
        {
            App.Services.Ui.Data.EventDescriptionPreview = preview.IsChecked == true;
            App.Services.Ui.SaveSoon();
            RenderNotes();
        };
        RenderNotes();
        var notesField = Ui.Column(4,
            new DockPanel { Children = { preview.Dock(Dock.Right), new TextBlock { Text = "Description", FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center } } },
            notes, previewHost);

        // Colour: the kind's colour by default, or one picked here.
        var color = Rgb.TryParse(e?.Color, out var c0) ? c0.Hex : null;
        var swatches = new WrapPanel();
        var colorBox = new TextBox { Text = color ?? "", PlaceholderText = "#rrggbb", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Classes = { "mono" } };
        void BuildSwatches()
        {
            swatches.Children.Clear();
            Border Swatch(string? hex, string tip)
            {
                var b = new Border
                {
                    Width = 22, Height = 22, CornerRadius = Ui.R(11), Margin = new Thickness(0, 0, 6, 4), Cursor = new Cursor(StandardCursorType.Hand),
                    Background = hex is null ? Brushes.Transparent : Ui.Raw(hex), BorderBrush = color == hex ? Ui.Strong : Ui.Tk("lineStrong"), BorderThickness = new Thickness(color == hex ? 2 : 1),
                    Child = hex is null ? new TextBlock { Text = "∅", FontSize = 12, Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
                };
                ToolTip.SetTip(b, tip);
                b.PointerPressed += (_, _) => { color = hex; colorBox.Text = hex ?? ""; BuildSwatches(); };
                return b;
            }
            swatches.Children.Add(Swatch(null, "The colour of its kind"));
            foreach (var hex in Themes.AccentPresets) swatches.Children.Add(Swatch(hex, hex));
        }
        colorBox.TextChanged += (_, _) =>
        {
            var text = (colorBox.Text ?? "").Trim();
            var parsed = Rgb.TryParse(text, out var rgb) ? rgb.Hex : null;
            if (text.Length > 0 && parsed is null) return; // still typing
            if (parsed != color) { color = parsed; BuildSwatches(); }
        };
        BuildSwatches();

        // Attachments: existing ones can be removed, new ones are copied in when the event is saved.
        var kept = e?.Attachments.ToList() ?? [];
        var added = new List<string>();
        var attachList = new StackPanel { Spacing = 3 };
        void BuildAttachments()
        {
            attachList.Children.Clear();
            foreach (var name in kept.ToList())
                attachList.Children.Add(new DockPanel
                {
                    Children =
                    {
                        Ui.IconButton(Icons.Close, "Remove this attachment", () => { kept.Remove(name); BuildAttachments(); }).Dock(Dock.Right),
                        Ui.Apply(Ui.Link(name, () => Shell.Open(Events.AttachmentPath(e!, name)), "Open"), b => b.HorizontalAlignment = HorizontalAlignment.Left),
                    },
                });
            foreach (var path in added.ToList())
                attachList.Children.Add(new DockPanel
                {
                    Children =
                    {
                        Ui.IconButton(Icons.Close, "Do not attach this file", () => { added.Remove(path); BuildAttachments(); }).Dock(Dock.Right),
                        Ui.Apply(new TextBlock { Text = Path.GetFileName(path) + "   (new)", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, t => ToolTip.SetTip(t, path)),
                    },
                });
            attachList.IsVisible = attachList.Children.Count > 0;
        }
        BuildAttachments();
        var attachButton = Ui.IconButton(Icons.Paperclip, "Attach files. They are copied into avamux's data folder.", () => { }, "Add files…");
        attachButton.HorizontalAlignment = HorizontalAlignment.Left;
        attachButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(attachButton) is not { } top) return;
            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach files", AllowMultiple = true });
            foreach (var f in picked)
                if (f.TryGetLocalPath() is { } local && !added.Contains(local)) added.Add(local);
            BuildAttachments();
        };
        // Skipped occurrences: dates the event does not happen on, each removable.
        var skipped = e?.SkippedDates.ToList() ?? [];
        var skippedList = new StackPanel { Spacing = 3 };
        void BuildSkipped()
        {
            skippedList.Children.Clear();
            foreach (var day in skipped.ToList())
                skippedList.Children.Add(new DockPanel
                {
                    Children =
                    {
                        Ui.IconButton(Icons.Close, "Bring this occurrence back", () => { skipped.Remove(day); BuildSkipped(); }).Dock(Dock.Right),
                        new TextBlock { Text = $"Skipping {day:ddd yyyy-MM-dd}", VerticalAlignment = VerticalAlignment.Center },
                    },
                });
            skippedList.IsVisible = skippedList.Children.Count > 0;
        }
        BuildSkipped();
        var repeatExtras = Ui.Column(10, everyRow, daysRow, endsRow, skippedList);

        void Sync()
        {
            var freq = (RecurrenceFreq)Math.Max(0, repeat.SelectedIndex);
            repeatExtras.IsVisible = freq != RecurrenceFreq.None;
            daysRow.IsVisible = freq == RecurrenceFreq.Weekly;
            everyUnit.Text = freq switch
            {
                RecurrenceFreq.Interval => "(a duration: 15m, 2h, 90s)", RecurrenceFreq.Daily => "day(s)", RecurrenceFreq.Weekly => "week(s) on",
                RecurrenceFreq.Monthly => "month(s)", RecurrenceFreq.Yearly => "year(s)", _ => "",
            };
            startTime.IsVisible = allDay.IsChecked != true;
            endTime.IsVisible = allDay.IsChecked != true;
            endRow.IsVisible = hasEnd.IsChecked == true;
            onEndField.IsVisible = hasEnd.IsChecked == true || allDay.IsChecked == true;
        }
        var lastFreq = r.Freq;
        repeat.SelectionChanged += (_, _) =>
        {
            var freq = (RecurrenceFreq)Math.Max(0, repeat.SelectedIndex);
            // The "every" box means a duration for intervals and a count otherwise; reset it when crossing over.
            if (freq == RecurrenceFreq.Interval && lastFreq != RecurrenceFreq.Interval) every.Text = "15m";
            else if (freq != RecurrenceFreq.Interval && lastFreq == RecurrenceFreq.Interval) every.Text = "1";
            lastFreq = freq;
            Sync();
        };
        allDay.IsCheckedChanged += (_, _) => Sync();
        hasEnd.IsCheckedChanged += (_, _) => Sync();
        Sync();

        var body = Ui.Column(12,
            Ui.Field("Title", title),
            Ui.Field("Kind", kind, "Events show on the calendar; tasks exist to run a script; timers go off once."),
            Ui.Field("Starts", Ui.Row(8, startDate, startTime, allDay)),
            Ui.Column(6, hasEnd, endRow),
            Ui.Field("Repeat", Ui.Column(10, repeat, repeatExtras)),
            Ui.Field("Location", location),
            notesField,
            Ui.Field("Attachments", Ui.Column(6, attachList, attachButton)),
            Ui.Field("Colour", Ui.Column(6, swatches, colorBox), "Shown on the calendar. The default colours per kind are under Settings → Events."),
            onStartField,
            onEndField);

        await Ui.FormAsync(Ui.Owner(this), existing is null ? "New" : "Edit", body, () =>
        {
            if (string.IsNullOrWhiteSpace(title.Text)) return Task.FromResult<string?>("Give it a title.");
            if (startDate.SelectedDate is not { } sd) return Task.FromResult<string?>("Pick a start date.");
            var isAllDay = allDay.IsChecked == true;
            var start = isAllDay ? sd.Date : sd.Date + ParseClock(startTime.Text, "Start");
            DateTime? end = null;
            if (hasEnd.IsChecked == true)
            {
                if (endDate.SelectedDate is not { } ed) return Task.FromResult<string?>("Pick an end date.");
                end = isAllDay ? ed.Date.AddDays(1).AddSeconds(-1) : ed.Date + ParseClock(endTime.Text, "End");
            }
            else if (isAllDay) end = start.AddDays(1).AddSeconds(-1);
            if (end is { } en && en < start) return Task.FromResult<string?>("The end is before the start.");

            var rec = new Recurrence { Freq = (RecurrenceFreq)Math.Max(0, repeat.SelectedIndex) };
            if (rec.Freq == RecurrenceFreq.Interval)
            {
                rec.IntervalMinutes = TimeParsing.ParseDuration(every.Text ?? "").TotalMinutes;
                if (rec.IntervalMinutes <= 0) return Task.FromResult<string?>("The interval must be longer than zero.");
            }
            else if (rec.Freq != RecurrenceFreq.None)
            {
                if (!int.TryParse((every.Text ?? "").Trim(), out var n) || n < 1) return Task.FromResult<string?>("\"Every\" must be a whole number of 1 or more.");
                rec.Interval = n;
            }
            if (rec.Freq == RecurrenceFreq.Weekly) rec.Days = dayToggles.Where(t => t.IsChecked == true).Select(t => (DayOfWeek)t.Tag!).ToList();
            if (rec.Freq != RecurrenceFreq.None)
            {
                rec.Until = until.SelectedDate?.Date.AddDays(1).AddSeconds(-1);
                if (!string.IsNullOrWhiteSpace(count.Text))
                {
                    if (!int.TryParse(count.Text.Trim(), out var c) || c < 1) return Task.FromResult<string?>("The number of times must be a whole number of 1 or more.");
                    rec.Count = c;
                }
            }

            var item = existing ?? new EventItem();
            item.Title = title.Text!.Trim();
            item.Kind = kind.SelectedIndex switch { 0 => EventKind.Calendar, 1 => EventKind.Task, _ => EventKind.Timer };
            item.Start = start; item.End = end; item.AllDay = isAllDay; item.Recurrence = rec;
            item.SkippedDates = rec.Freq == RecurrenceFreq.None ? [] : skipped.ToList();
            item.OnStartScript = string.IsNullOrWhiteSpace(onStart.Text) ? null : onStart.Text;
            item.OnEndScript = string.IsNullOrWhiteSpace(onEnd.Text) || end is null ? null : onEnd.Text;
            item.Notes = string.IsNullOrWhiteSpace(notes.Text) ? null : notes.Text;
            item.Location = string.IsNullOrWhiteSpace(location.Text) ? null : location.Text.Trim();
            var typed = (colorBox.Text ?? "").Trim();
            if (typed.Length > 0 && !Rgb.TryParse(typed, out _)) return Task.FromResult<string?>("The colour must look like #rrggbb.");
            item.Color = color;
            item.Completed = false;
            if (existing is null) Events.Add(item);
            foreach (var name in item.Attachments.Except(kept).ToList()) Events.Detach(item, name);
            foreach (var path in added) Events.Attach(item, path);
            added.Clear();
            Events.Save();
            _selectedDay = start.Date;
            _month = new DateTime(start.Year, start.Month, 1);
            return Task.FromResult<string?>(null);
        }, existing is null ? "Add" : "Save", 620);
        Rebuild();
    }

    private static DateTime RoundUp(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddMinutes(Math.Ceiling(t.Minute / 5.0) * 5);
}
