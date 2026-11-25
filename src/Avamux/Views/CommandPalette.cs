using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Avamux.Views;

/// <summary>
/// The key that opens the command palette (Settings → Window → Command palette key): a modifier pressed twice in
/// quick succession ("Shift Shift", "Ctrl Ctrl", "Alt Alt", "Cmd Cmd") or an ordinary shortcut ("Ctrl+Shift+P"). A press only
/// counts as a tap when nothing else was pressed while it was down, so Shift held for capitals never opens it.
/// </summary>
public sealed class PaletteKey
{
    public enum Mod { None, Shift, Ctrl, Alt, Meta }

    private string? _spec;
    private Mod _tap;
    private KeyGesture? _gesture;
    private Mod _held;
    private bool _clean;
    private long _downAt, _lastTap = long.MinValue / 2;

    /// <summary>How quickly the second tap has to follow the first, in milliseconds.</summary>
    public int TapMs { get; set; } = 400;

    /// <summary>A double tap of this modifier opens the palette (None for a shortcut, or when off).</summary>
    public Mod Tap => _tap;
    public KeyGesture? Gesture => _gesture;

    public void Configure(string? spec)
    {
        spec = (spec ?? "").Trim();
        if (spec == _spec) return;
        _spec = spec;
        _tap = Mod.None;
        _gesture = null;
        var words = spec.ToLowerInvariant().Replace("+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 2 && (words[0] == words[1] || words[0] == "double"))
            _tap = words[1] switch
            {
                "shift" => Mod.Shift, "ctrl" or "control" => Mod.Ctrl, "alt" or "option" or "opt" => Mod.Alt,
                "cmd" or "command" or "meta" or "super" or "win" => Mod.Meta, _ => Mod.None,
            };
        if (_tap == Mod.None && spec.Length > 0)
            try { _gesture = KeyGesture.Parse(spec); } catch { _gesture = null; }
    }

    public static Mod ModOf(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift => Mod.Shift,
        Key.LeftCtrl or Key.RightCtrl => Mod.Ctrl,
        Key.LeftAlt or Key.RightAlt => Mod.Alt,
        Key.LWin or Key.RWin => Mod.Meta,
        _ => Mod.None,
    };

    /// <summary>A key went down (<paramref name="mod"/> None for any other key). True when this opens the palette.</summary>
    public bool Down(Mod mod, long ms)
    {
        if (_tap == Mod.None) return false;
        if (mod == _tap)
        {
            if (_held == mod) return false; // auto-repeat
            _held = mod;
            _clean = true;
            _downAt = ms;
            return false;
        }
        // Anything else pressed in between: not a tap, and not a double tap.
        _clean = false;
        _lastTap = long.MinValue / 2;
        return false;
    }

    /// <summary>A key came up. True when this completes a double tap.</summary>
    public bool Up(Mod mod, long ms)
    {
        if (_tap == Mod.None || mod != _tap || _held != mod) return false;
        _held = Mod.None;
        if (!_clean || ms - _downAt > 500) { _lastTap = long.MinValue / 2; return false; }
        if (ms - _lastTap <= TapMs) { _lastTap = long.MinValue / 2; return true; }
        _lastTap = ms;
        return false;
    }

    public bool Matches(Key key, KeyModifiers modifiers) => _gesture is { } g && g.Key == key && g.KeyModifiers == modifiers;

    /// <summary>For keys reported by name (GDK's or AppKit's: "p", "P", "F5", "1"), with their modifiers.</summary>
    public bool Matches(string keyName, bool ctrl, bool shift, bool alt, bool meta = false)
    {
        if (_gesture is not { } g) return false;
        var want = g.Key.ToString();
        var name = keyName.Length == 1 && char.IsDigit(keyName[0]) ? "D" + keyName : keyName;
        return string.Equals(want, name, StringComparison.OrdinalIgnoreCase)
               && g.KeyModifiers.HasFlag(KeyModifiers.Control) == ctrl && g.KeyModifiers.HasFlag(KeyModifiers.Shift) == shift && g.KeyModifiers.HasFlag(KeyModifiers.Alt) == alt && g.KeyModifiers.HasFlag(KeyModifiers.Meta) == meta;
    }
}

/// <summary>One thing the palette can do.</summary>
public sealed record PaletteCommand(string Title, string Group, Geometry? Icon, Action Run, string? Detail = null);

/// <summary>Typing narrows the list: every word typed has to be found in the title, in order, as one piece or letter by letter.</summary>
public static class PaletteMatch
{
    /// <summary>A score (higher is better) and the matched character positions, or null when the text does not match.</summary>
    public static (int Score, List<int> Hits)? Match(string query, string text)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<int>();
        var score = 0;
        foreach (var word in words)
        {
            if (Word(word, text) is not { } m) return null;
            score += m.Score;
            hits.AddRange(m.Hits);
        }
        hits.Sort();
        return (score - text.Length / 8, hits);
    }

    private static bool Start(string text, int i) => i == 0 || !char.IsLetterOrDigit(text[i - 1]) || char.IsUpper(text[i]) && char.IsLower(text[i - 1]);

    private static (int Score, List<int> Hits)? Word(string word, string text)
    {
        var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
            return (1000 + word.Length * 10 - at + (Start(text, at) ? 300 : 0), Enumerable.Range(at, word.Length).ToList());
        // Letter by letter: each letter later than the last, preferring the start of a word.
        var hits = new List<int>();
        var from = 0;
        var score = 300;
        foreach (var c in word)
        {
            var next = -1;
            for (var i = from; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(c)) continue;
                if (next < 0) next = i;
                if (Start(text, i)) { next = i; break; }
            }
            if (next < 0) return null;
            score += Start(text, next) ? 25 : 0;
            score -= next - from;
            hits.Add(next);
            from = next + 1;
        }
        return (score, hits);
    }
}

/// <summary>
/// The command palette (a preview of what is to come): pressed from anywhere, the terminals and the wiki's preview
/// included, it lists pages to go to, a few actions and the wiki's pages, narrows them as you type, and runs the one
/// picked. Keys typed into a web view never reach the window, so they are watched where they can be seen: on macOS
/// an AppKit monitor sees every key of the app (<see cref="MacKeyMonitor"/>); elsewhere a class handler sees every
/// window's keys and, on Linux, each web view's GTK key events are watched too (<see cref="FromWebView"/>).
/// </summary>
public static class CommandPalette
{
    private static readonly PaletteKey Key = new();
    private static CommandPaletteWindow? _open;
    private static bool _installed;

    /// <summary>Starts listening to every window's keys. Call once.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // On macOS the monitor sees every key, Avalonia's included: listening to the windows as well would count each twice.
        if (OperatingSystem.IsMacOS() && MacKeyMonitor.Start(OnMacKey)) return;
        InputElement.KeyDownEvent.AddClassHandler<Window>((w, e) => OnKey(w, e, down: true), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyUpEvent.AddClassHandler<Window>((w, e) => OnKey(w, e, down: false), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static PaletteKey Current
    {
        get
        {
            Key.Configure(App.Services.Config.Settings.Ui.CommandPaletteKey);
            return Key;
        }
    }

    private static void OnKey(Window window, KeyEventArgs e, bool down)
    {
        var key = Current;
        var now = Environment.TickCount64;
        if (down && key.Matches(e.Key, e.KeyModifiers)) { e.Handled = true; Toggle(window, null); return; }
        var mod = PaletteKey.ModOf(e.Key);
        if (down ? key.Down(mod, now) : key.Up(mod, now)) Toggle(window, null);
    }

    /// <summary>A key anywhere in the app on macOS (on the main thread). True swallows it: the shortcut that opens the palette.</summary>
    private static bool OnMacKey(MacKeyMonitor.KeyEvent e)
    {
        var key = Current;
        var now = Environment.TickCount64;
        if (e.IsKey)
        {
            if (!e.Down) return false;
            if (key.Gesture is not null && key.Matches(e.Name, e.Ctrl, e.Shift, e.Alt, e.Meta))
            {
                Dispatcher.UIThread.Post(() => ToggleOverActive());
                return true;
            }
            key.Down(PaletteKey.Mod.None, now);
            return false;
        }
        if (e.Down ? key.Down(e.Mod, now) : key.Up(e.Mod, now)) Dispatcher.UIThread.Post(() => ToggleOverActive());
        return false;
    }

    /// <summary>Opens the palette over the window in front (the main one when none of ours is).</summary>
    private static void ToggleOverActive()
    {
        var windows = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows ?? [];
        var owner = windows.FirstOrDefault(w => w.IsActive && w is not CommandPaletteWindow) ?? Ui.Main;
        if (owner is not null) Toggle(owner, null); else _open?.Close();
    }

    /// <summary>
    /// Feeds the keys typed into a web view (Linux, GTK) to the palette key. Called on the GLib thread; the result is
    /// handled on the UI thread.
    /// </summary>
    public static void FromWebView(Avalonia.Controls.NativeWebView web, uint keyval, string keyName, uint state, bool down)
    {
        var now = Environment.TickCount64;
        Dispatcher.UIThread.Post(() =>
        {
            var key = Current;
            var window = TopLevel.GetTopLevel(web) as Window;
            if (window is null) return;
            const uint shiftMask = 1, ctrlMask = 4, altMask = 8, superMask = 1 << 26;
            if (down && key.Gesture is not null && key.Matches(keyName, (state & ctrlMask) != 0, (state & shiftMask) != 0, (state & altMask) != 0, (state & superMask) != 0))
            {
                Toggle(window, web);
                return;
            }
            var mod = keyval switch
            {
                0xffe1 or 0xffe2 => PaletteKey.Mod.Shift, 0xffe3 or 0xffe4 => PaletteKey.Mod.Ctrl, 0xffe9 or 0xffea => PaletteKey.Mod.Alt,
                0xffeb or 0xffec => PaletteKey.Mod.Meta, _ => PaletteKey.Mod.None,
            };
            if (down ? key.Down(mod, now) : key.Up(mod, now)) Toggle(window, web);
        });
    }

    /// <summary>Opens the palette over <paramref name="owner"/>, or closes it when it is open.</summary>
    public static void Toggle(Window owner, Avalonia.Controls.NativeWebView? from)
    {
        if (_open is { } open) { open.Close(); return; }
        if (owner is CommandPaletteWindow) return;
        var palette = new CommandPaletteWindow(Commands(), owner, from);
        _open = palette;
        palette.Closed += (_, _) => { if (ReferenceEquals(_open, palette)) _open = null; };
        palette.ShowOver(owner);
    }

    public static bool IsOpen => _open is not null;

    /// <summary>What the palette offers (for now a sample: pages, a few actions, the wiki's pages).</summary>
    public static List<PaletteCommand> Commands()
    {
        var list = new List<PaletteCommand>();
        if (Ui.Main is not { } main) return list;
        foreach (var (key, label, icon) in main.Destinations())
            list.Add(new PaletteCommand(label, "Go to", icon, () => { main.Activate(); main.Navigate(key); }));
        list.Add(new PaletteCommand("New Terminal", "Terminal", Icons.Console, () => { main.Activate(); main.Navigate("terminals"); main.Terminals.NewTerminal(); }));
        if (App.Services.Config.Settings.Ai.Chat)
            list.Add(new PaletteCommand("New AI Chat", "AI Chat", Icons.Forum, () => { main.Activate(); main.Navigate("chat"); main.Page<ChatPage>("chat")?.NewChat(); }));
        list.Add(new PaletteCommand("Open Today's Note", "Wiki", Icons.CalendarToday, () => { main.Activate(); main.OpenToday(); }));
        list.Add(new PaletteCommand("Boss Screen", "Window", Icons.Monitor, () => main.ToggleBoss(null)));
        list.Add(new PaletteCommand("Change the Command Palette Key", "Settings", Icons.Cog, () =>
        {
            main.Activate();
            main.Navigate("settings");
            main.Page<SettingsPage>("settings")?.Find("command palette");
        }));
        foreach (var page in App.Services.Wiki.Pages())
            list.Add(new PaletteCommand(page, "Wiki page", Icons.File, () =>
            {
                main.Activate();
                main.Navigate("wiki");
                main.Page<WikiPage>("wiki")?.Open(page);
            }));
        return list;
    }
}

/// <summary>The palette's window: a filter box, the matching commands, and a line of hints.</summary>
public sealed class CommandPaletteWindow : Window
{
    /// <summary>The most rows shown; typing narrows the rest down.</summary>
    public const int MaxRows = 200;

    private readonly List<PaletteCommand> _commands;
    private readonly Window _owner;
    private readonly Avalonia.Controls.NativeWebView? _from;
    private readonly IInputElement? _focusBefore;
    private readonly TextBox _filter = new()
    {
        PlaceholderText = "Type a page, an action or a wiki page…", FontSize = 15, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Padding = new Thickness(4, 8), VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ListBox _list = new() { Classes = { "plain" }, MaxHeight = 380, Background = Brushes.Transparent };
    private readonly TextBlock _count = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private bool _ran;

    public CommandPaletteWindow(List<PaletteCommand> commands, Window owner, Avalonia.Controls.NativeWebView? from)
    {
        _commands = commands;
        _owner = owner;
        _from = from;
        _focusBefore = TopLevel.GetTopLevel(owner)?.FocusManager?.GetFocusedElement();
        Title = "Command palette — avamux";
        Icon = AppIcon.Window;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        _count.Foreground = Ui.Dim;
        var preview = new Border
        {
            Background = Ui.Tk("bgDeep"), CornerRadius = Ui.R(4), Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "preview", FontSize = 10.5, Foreground = Ui.Dim },
        };
        ToolTip.SetTip(preview, "The command palette is a first look: more commands are coming.");
        Content = new Border
        {
            Background = Ui.Tk("panel"), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1), CornerRadius = Ui.R(10),
            Padding = new Thickness(10, 6, 10, 8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new DockPanel
                    {
                        Children =
                        {
                            new PathIcon { Data = Icons.Magnify, Width = 16, Height = 16, Foreground = Ui.Dim, Margin = new Thickness(4, 0, 6, 0) }.Dock(Dock.Left),
                            preview.Dock(Dock.Right),
                            _filter,
                        },
                    },
                    new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(-10, 0) },
                    _list,
                    new DockPanel
                    {
                        Margin = new Thickness(4, 2, 4, 0),
                        Children =
                        {
                            _count.Dock(Dock.Right),
                            new TextBlock { Text = "↑ ↓ choose   Enter run   Esc close", FontSize = 11.5, Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                },
            },
        };
        _filter.TextChanged += (_, _) => Fill();
        _filter.AddHandler(KeyDownEvent, OnFilterKey, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true) is { Tag: PaletteCommand c }) Run(c);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // Clicking elsewhere puts it away, as a menu would.
        Deactivated += (_, _) => { if (IsVisible) Close(); };
        Closed += (_, _) => GiveFocusBack();
        Opened += (_, _) => { _filter.Focus(); Fill(); };
        Fill();
    }

    /// <summary>The commands shown now, best first.</summary>
    public IReadOnlyList<PaletteCommand> Shown => _list.Items.OfType<ListBoxItem>().Select(i => (PaletteCommand)i.Tag!).ToList();
    public PaletteCommand? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as PaletteCommand;
    public TextBox Filter => _filter;

    /// <summary>Shows the palette near the top of <paramref name="owner"/>, in the middle.</summary>
    public void ShowOver(Window owner)
    {
        var scale = owner.RenderScaling;
        var width = (int)(Width * scale);
        var ownerWidth = (int)(owner.Bounds.Width * scale);
        Position = new PixelPoint(owner.Position.X + Math.Max(0, (ownerWidth - width) / 2), owner.Position.Y + (int)(70 * scale));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Show(owner);
        Activate();
        if (OperatingSystem.IsLinux()) WebViews.FocusWindow(this);
        _filter.Focus();
    }

    private void Fill()
    {
        var query = (_filter.Text ?? "").Trim();
        IEnumerable<(PaletteCommand Command, List<int> Hits)> matches;
        int total;
        if (query.Length == 0)
        {
            // Before anything is typed: everything but the wiki's pages, which come in as soon as you type.
            var top = _commands.Where(c => c.Group != "Wiki page").ToList();
            total = top.Count;
            matches = top.Select(c => (c, new List<int>()));
            var pages = _commands.Count - top.Count;
            _count.Text = pages > 0 ? $"type to search {pages:N0} wiki pages too" : "";
        }
        else
        {
            var scored = _commands.Select(c => (Command: c, M: PaletteMatch.Match(query, c.Title)))
                .Where(x => x.M is not null)
                .OrderByDescending(x => x.M!.Value.Score).ThenBy(x => x.Command.Group == "Wiki page").ThenBy(x => x.Command.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            total = scored.Count;
            matches = scored.Select(x => (x.Command, x.M!.Value.Hits));
            _count.Text = total == 0 ? "nothing matches" : total > MaxRows ? $"{MaxRows} of {total:N0}" : $"{total:N0} of {_commands.Count:N0}";
        }
        _list.Items.Clear();
        foreach (var (command, hits) in matches.Take(MaxRows)) _list.Items.Add(Row(command, hits));
        _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
        _list.IsVisible = _list.Items.Count > 0;
    }

    private static ListBoxItem Row(PaletteCommand c, List<int> hits)
    {
        var title = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // The letters that matched stand out.
        var hit = new HashSet<int>(hits);
        var i = 0;
        while (i < c.Title.Length)
        {
            var on = hit.Contains(i);
            var j = i;
            while (j < c.Title.Length && hit.Contains(j) == on) j++;
            var run = new Run(c.Title[i..j]);
            if (on) { run.FontWeight = FontWeight.Bold; run.Foreground = Ui.Tk("accent"); }
            title.Inlines!.Add(run);
            i = j;
        }
        return new ListBoxItem
        {
            Tag = c, Padding = new Thickness(8, 5),
            Content = new DockPanel
            {
                Children =
                {
                    new PathIcon { Data = c.Icon ?? Icons.ChevronRight, Width = 14, Height = 14, Foreground = Ui.Dim, Margin = new Thickness(0, 0, 10, 0) }.Dock(Dock.Left),
                    new TextBlock { Text = c.Group, FontSize = 11.5, Foreground = Ui.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }.Dock(Dock.Right),
                    title,
                },
            },
        };
    }

    private void OnFilterKey(object? sender, KeyEventArgs e)
    {
        var count = _list.Items.Count;
        switch (e.Key)
        {
            case Avalonia.Input.Key.Escape: e.Handled = true; Close(); break;
            case Avalonia.Input.Key.Enter: e.Handled = true; if (Selected is { } c) Run(c); break;
            case Avalonia.Input.Key.Down when count > 0: e.Handled = true; Move(1); break;
            case Avalonia.Input.Key.Up when count > 0: e.Handled = true; Move(-1); break;
            case Avalonia.Input.Key.PageDown when count > 0: e.Handled = true; Move(8); break;
            case Avalonia.Input.Key.PageUp when count > 0: e.Handled = true; Move(-8); break;
        }
    }

    private void Move(int by)
    {
        var count = _list.Items.Count;
        var at = _list.SelectedIndex;
        _list.SelectedIndex = Math.Abs(by) == 1 ? ((at + by) % count + count) % count : Math.Clamp(at + by, 0, count - 1);
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    public void Run(PaletteCommand command)
    {
        _ran = true;
        Close();
        Dispatcher.UIThread.Post(() =>
        {
            try { command.Run(); }
            catch (Exception ex) { Ui.Toast(_owner, "Command palette", ex.Message); }
        });
    }

    /// <summary>Back to where the keys were going before, unless a command took them somewhere else.</summary>
    private void GiveFocusBack()
    {
        if (_ran) return;
        _owner.Activate();
        if (_from is not null) Dispatcher.UIThread.Post(() => WebViews.FocusNative(_from));
        else (_focusBefore as Control)?.Focus();
    }
}
