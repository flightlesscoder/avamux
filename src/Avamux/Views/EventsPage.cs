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

    private readonly Grid _calendar = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*"), RowDefinitions = new RowDefinitions("Auto,*,*,*,*,*,*") };
    private readonly TextBlock _monthLabel = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
    private readonly StackPanel _side = new() { Spacing = 14, Margin = new Thickness(16, 14, 16, 24) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(TextBlock Text, EventItem Item)> _countdowns = [];
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedDay = DateTime.Today;
    private DateTime _today = DateTime.Today;

    private static EventService Events => App.Services.Events;

    public EventsPage()
    {
        var nav = Ui.Row(4,
            Ui.TextButton("‹", () => { _month = _month.AddMonths(-1); Rebuild(); }, "Previous month"),
            Ui.TextButton("Today", () => { _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); _selectedDay = DateTime.Today; Rebuild(); }),
            Ui.TextButton("›", () => { _month = _month.AddMonths(1); Rebuild(); }, "Next month"),
            Ui.Apply(_monthLabel, t => t.Margin = new Thickness(10, 0, 0, 0)));
        nav.Margin = new Thickness(16, 10, 16, 8);

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
                            Child = new DockPanel { Children = { nav.Dock(Dock.Top), new Border { Margin = new Thickness(12, 0, 12, 12), Child = _calendar } } },
                        },
                        new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = new ScrollViewer { Content = _side } }.At(0, 1),
                    },
                },
            },
        };
        Events.Changed += Ui.Debounced(Rebuild);
        _clock.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            if (DateTime.Today != _today) { _today = DateTime.Today; Rebuild(); return; }
            foreach (var (text, item) in _countdowns) text.Text = NextText(item);
        };
        _clock.Start();
        Rebuild();
    }

    public void OnShown() => Rebuild();

    /// <summary>Shows an event's month and opens it for editing (used by Search).</summary>
    public void Reveal(string id)
    {
        if (Events.Find(id) is not { } e) { Ui.Toast(this, "That event no longer exists"); return; }
        var when = Events.NextOccurrence(e)?.Start ?? e.Start;
        _selectedDay = when.Date;
        _month = new DateTime(when.Year, when.Month, 1);
        Rebuild();
        _ = EditAsync(e, e.Kind, null);
    }

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
        BuildCalendar();
        BuildSide();
    }

    private void BuildCalendar()
    {
        _monthLabel.Text = _month.ToString("MMMM yyyy");
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
            var cell = new StackPanel { Spacing = 2 };
            var number = new TextBlock { Text = day.Day.ToString(), FontSize = 12, Foreground = day == DateTime.Today ? Ui.OnAccent : inMonth ? Ui.Tk("textSoft") : Ui.Tk("ghost"), FontWeight = day == DateTime.Today ? FontWeight.Bold : FontWeight.Normal };
            cell.Children.Add(day == DateTime.Today
                ? new Border { Background = Ui.Tk("accent"), CornerRadius = Ui.R(9), Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = number }
                : number);
            foreach (var (item, occ, count) in todays.Take(3))
            {
                var label = (count == 1 && occ.Start.Date == day && !item.AllDay ? occ.Start.ToString("HH:mm") + " " : "") + item.Title + (count > 1 ? $" ×{count}" : "");
                var chip = new Border
                {
                    Background = ItemBrush(item), CornerRadius = Ui.R(3), Padding = new Thickness(4, 0), Opacity = item.Enabled && !item.Completed ? 0.95 : 0.4, Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new TextBlock { Text = label, FontSize = 11, Foreground = ItemText(item), TextTrimming = TextTrimming.CharacterEllipsis },
                };
                ToolTip.SetTip(chip, item.Title + (string.IsNullOrWhiteSpace(item.Location) ? "" : "\n" + item.Location) + "\nDouble-click to edit");
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
                Background = day == _selectedDay ? Ui.Tk("cellSel") : inMonth ? Ui.Tk("cell") : Brushes.Transparent,
                BorderBrush = day == _selectedDay ? Ui.Tk("accent") : Ui.Line, BorderThickness = new Thickness(1), CornerRadius = Ui.R(4),
                Margin = new Thickness(2), Padding = new Thickness(5, 3), Child = cell, ClipToBounds = true, MinHeight = 56,
            };
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
        _side.Children.Clear();

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

    private async Task EditAsync(EventItem? existing, EventKind defaultKind, DateTime? day)
    {
        var e = existing;
        var start0 = e?.Start ?? (day is { } dd ? dd.Date.AddHours(9) : RoundUp(DateTime.Now.AddMinutes(5)));
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
        var repeatExtras = Ui.Column(10, everyRow, daysRow, endsRow);

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
