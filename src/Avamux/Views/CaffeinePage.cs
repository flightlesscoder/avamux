using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core.Awake;
using Avamux.Core.Commands;

namespace Avamux.Views;

/// <summary>Caffeine: keep the computer awake until turned off, for a while, or while terminals are working.</summary>
public sealed class CaffeinePage : UserControl, IPage
{
    private readonly PathIcon _cup = new() { Data = Icons.Coffee, Width = 56, Height = 56 };
    private readonly TextBlock _headline = new() { FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Foreground = Ui.Dim, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _problem = new() { Foreground = Ui.Tk("warn"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _off;
    private readonly Border _status;
    private readonly TextBox _custom = new() { PlaceholderText = "e.g. 45m, 1h30m or 17:30", Width = 220 };
    private readonly List<(Border Card, CaffeineMode Mode)> _cards = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private static CaffeineService Caffeine => App.Services.Caffeine;

    public CaffeinePage()
    {
        _off = Ui.TextButton("Turn Off", () => Run(Caffeine.Off), "Let the computer sleep as usual again");
        _status = new Border
        {
            Classes = { "card" }, Padding = new Thickness(22, 18),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Apply(_off, b => b.VerticalAlignment = VerticalAlignment.Center).Dock(Dock.Right),
                    Ui.Apply(_cup, c => c.Margin = new Thickness(0, 0, 20, 0)).Dock(Dock.Left),
                    Ui.Column(4, _headline, _detail, _problem),
                },
            },
        };

        var presets = new WrapPanel();
        foreach (var (label, minutes) in new[] { ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60), ("2 hours", 120), ("4 hours", 240), ("8 hours", 480) })
            presets.Children.Add(Ui.Apply(Ui.TextButton(label, () => Run(() => Caffeine.For(TimeSpan.FromMinutes(minutes)))), b => b.Margin = new Thickness(0, 0, 8, 8)));
        void StartCustom() => Run(() =>
        {
            var text = (_custom.Text ?? "").Trim();
            if (text.Contains(':') || text.StartsWith('+') || text.StartsWith("tomorrow", StringComparison.OrdinalIgnoreCase))
            {
                var when = TimeParsing.ParseDateTime(text);
                if (when <= DateTime.Now && !text.StartsWith('+')) when = when.AddDays(1);
                Caffeine.UntilTime(when);
            }
            else Caffeine.For(TimeParsing.ParseDuration(text));
        });
        _custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; StartCustom(); } };

        var grace = App.Services.Config.Settings.Caffeine.GraceMinutes;
        var body = new StackPanel
        {
            Spacing = 14, Margin = new Thickness(20, 0, 20, 24), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                _status,
                Option(CaffeineMode.Indefinite, "Until I turn it off", "Stays on until you come back here (or run  avamux caffeine off).",
                    Ui.Apply(Ui.TextButton("Keep Awake", () => Run(Caffeine.Indefinite)), b => b.Classes.Add("accent"))),
                Option(CaffeineMode.Timed, "For a while", "Turns itself off when the time is up.",
                    Ui.Column(4, presets, Ui.Row(8, _custom, Ui.TextButton("Start", StartCustom, "A length of time (45m, 1h30m) or a clock time (17:30)")))),
                Option(CaffeineMode.Terminals, "While terminals are busy",
                    $"Awake whenever a terminal is running a command or an AI agent is working, and for {grace} minute{(grace == 1 ? "" : "s")} after the last one finishes " +
                    "(Settings → Caffeine). At the prompt, or with an agent waiting for you, the computer may sleep.",
                    Ui.TextButton("Watch Terminals", () => Run(Caffeine.WhileTerminalsBusy))),
                Ui.Apply(Ui.Text(OperatingSystem.IsLinux() ? "Uses systemd-inhibit, which desktops such as KDE Plasma and GNOME respect. Closing avamux ends it."
                        : OperatingSystem.IsMacOS() ? "Uses caffeinate. Closing avamux ends it." : "Uses the Windows power API. Closing avamux ends it.", "dim"), t => t.FontSize = 12),
            },
        };

        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("Caffeine", "Keep the computer awake while something is going on.").Dock(Dock.Top),
                new ScrollViewer { Content = body },
            },
        };
        Caffeine.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _clock.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => _clock.Start();
        DetachedFromVisualTree += (_, _) => _clock.Stop();
        Refresh();
    }

    public void OnShown() => Refresh();

    private Border Option(CaffeineMode mode, string title, string description, Control action)
    {
        var card = new Border
        {
            Classes = { "card" }, BorderThickness = new Thickness(1),
            Child = Ui.Column(8, Ui.Text(title, "h2"), Ui.Apply(Ui.Text(description, "dim"), t => t.FontSize = 12.5), action),
        };
        _cards.Add((card, mode));
        return card;
    }

    private void Run(Action action) => Ui.Try(this, () => { action(); Refresh(); }, "Could not change Caffeine");

    private void Refresh()
    {
        var st = Caffeine.Status;
        var on = st.Mode != CaffeineMode.Off;
        _headline.Text = !on ? "The computer sleeps as usual" : st.Holding ? "Keeping the computer awake" : "Caffeine is on, nothing to wait for";
        _detail.Text = st.Describe(DateTime.Now);
        _problem.Text = st.Problem ?? "";
        _problem.IsVisible = st.Problem is not null;
        _cup.Foreground = st.Holding ? Ui.Tk("accent") : on ? Ui.Tk("warn") : Ui.Tk("faint");
        _status.BorderBrush = st.Holding ? Ui.Tk("accent") : Ui.Tk("line");
        _status.Classes.Set("glow", st.Holding);
        _off.IsVisible = on;
        foreach (var (card, mode) in _cards)
            card.BorderBrush = mode == st.Mode ? Ui.Tk("accent") : Ui.Tk("line");
    }
}
