using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avamux.Core;
using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Theming;

namespace Avamux.Views;

/// <summary>Every setting, grouped, with the file it currently comes from. Edits are written to the chosen file.</summary>
public sealed class SettingsPage : UserControl, IPage
{
    private static readonly Dictionary<string, (string Label, string Help)> Docs = new()
    {
        ["terminal.scrollback"] = ("Scrollback lines", "Lines of history kept per terminal tab."),
        ["terminal.fontFamily"] = ("Font family", "CSS font list. A bundled symbols font is always appended so powerline and Nerd Font glyphs render."),
        ["terminal.fontSize"] = ("Font size", "In pixels."),
        ["terminal.shell"] = ("Shell", "Program started in new tabs. Empty uses $SHELL."),
        ["terminal.term"] = ("TERM", "Value of the TERM variable inside terminals."),
        ["terminal.copyOnSelect"] = ("Copy on select", "Copy text to the clipboard as soon as it is selected."),
        ["terminal.cursorBlink"] = ("Blinking cursor", "Not used while the heartbeat cursor is on."),
        ["terminal.crt"] = ("CRT monitor effect", "Scanlines, softly glowing text and darkened corners over the terminal, like an old monitor."),
        ["terminal.crtIntensity"] = ("CRT strength", "0 to 100. Lower is subtler and easier to read."),
        ["terminal.crtFlicker"] = ("CRT flicker", "A faint, slow flicker of the screen."),
        ["terminal.cursorPulse"] = ("Heartbeat cursor", "The cursor beats like a pulse with a soft neon glow instead of blinking."),
        ["terminal.cursorPulseShape"] = ("Heartbeat cursor shape", "block is filled, outline is hollow, bar is a thin line."),
        ["terminal.cursorPulseColor"] = ("Heartbeat cursor colour", "Empty follows the theme's accent."),
        ["terminal.cursorPulseDouble"] = ("Double beat", "Pulse twice per beat, like a heart, instead of once."),
        ["terminal.cursorPulseRate"] = ("Heartbeat rate", "Beats per minute, 20 to 240. The default is a slow 30."),
        ["terminal.cursorPulseGlow"] = ("Heartbeat glow", "How strong the glow is, 0 to 100. 0 keeps the beat without the glow."),
        ["terminal.closeTabOnExit"] = ("Close tab on exit", "Remove a tab when its program ends."),
        ["terminal.preserveDeadSessions"] = ("Keep history of ended terminals", "Save a terminal's scrollback to Logs when it ends."),
        ["terminal.background"] = ("Background colour", "Empty follows the theme."),
        ["terminal.foreground"] = ("Text colour", "Empty follows the theme."),
        ["terminal.agentProcesses"] = ("AI agent programs", "Program names treated as AI agents. A tab running one shows whether the agent is working or waiting for input."),
        ["terminal.agentIdleSeconds"] = ("Agent quiet time (seconds)", "An agent that prints nothing for this long is taken to be waiting for input."),
        ["terminal.notifyAgentWaiting"] = ("Notify when an agent waits", "Show a notification when an agent in another tab starts waiting for input."),
        ["events.calendarColor"] = ("Calendar event colour", "Colour of calendar events on the calendar. Empty follows the theme. An event's own colour wins."),
        ["events.taskColor"] = ("Task colour", "Colour of scheduled tasks. Empty follows the theme."),
        ["events.timerColor"] = ("Timer colour", "Colour of timers. Empty follows the theme."),
        ["repositories.pullRequestCommand"] = ("Pull request command", "Command that prints a repository's pull requests as JSON, used by Repositories → Pull Requests. Empty uses the GitHub CLI (gh pr list). A watch can override it."),
        ["wiki.dailyNoteFormat"] = ("Daily note name", "Date format of the file \"Today\" opens, e.g. yyyy-MM-dd dddd gives \"2026-11-12 Thursday.md\"."),
        ["wiki.dailyNoteFolder"] = ("Daily note folder", "Folder inside the wiki for daily notes. Empty puts them at the top."),
        ["wiki.dailyNoteTemplate"] = ("Daily note template", "Text of a new daily note. {title} is the file name, {date} the date; \\n starts a new line."),
        ["wiki.rememberedPositions"] = ("Remembered scroll positions", "How many wiki files have their last scroll position remembered."),
        ["ui.navStyle"] = ("Navigation bar", "Standard shows icons with labels; mini is a slim bar of smaller icons with tooltips."),
        ["theme.name"] = ("Theme", "Colours, shapes and sounds of the whole window."),
        ["theme.accent"] = ("Accent colour", "Empty uses the theme's own accent."),
        ["theme.windowChrome"] = ("Window chrome", "auto lets the theme decide; system uses the desktop's title bar; custom draws a title bar in the theme's colours."),
        ["theme.glow"] = ("Neon glow", "A glowing edge around whatever is selected or current: list and tree selections, the active navigation button and tab, today in the calendar, the current terminal tab."),
        ["theme.glowIntensity"] = ("Glow strength", "0 to 100."),
        ["theme.glowColor"] = ("Glow colour", "Empty follows the theme's accent."),
        ["theme.sounds"] = ("Sounds", "Play a sound when something happens."),
        ["theme.soundVolume"] = ("Sound volume", "How loud every theme sound plays, including the previews below. 0 is silent."),
        ["theme.soundSet"] = ("Sound set", "theme uses the sounds that belong to the theme."),
        ["theme.soundOnEvent"] = ("Sound: event starts or ends", ""),
        ["theme.soundOnTimer"] = ("Sound: timer ends", ""),
        ["theme.soundOnWatch"] = ("Sound: watched item changes", ""),
        ["theme.soundOnPullRequest"] = ("Sound: pull request changes", ""),
        ["theme.soundOnAgentWaiting"] = ("Sound: AI agent waits for input", ""),
        ["ai.chat"] = ("AI Chat", "Show the AI Chat page in the navigation."),
        ["ai.workloads"] = ("Workloads", "Show the Workloads page (providers, work queues, models on disk) in the navigation."),
        ["watching.enabled"] = ("Watching enabled", "Turn all periodic checks on or off."),
        ["watching.defaultIntervalMinutes"] = ("Minutes between checks", "Used by watches that do not set their own interval."),
        ["watching.timeoutSeconds"] = ("Check timeout (seconds)", "A check that runs longer is stopped and reported as an error."),
        ["watching.notifyOnChange"] = ("Notify on change", "Show a notification when a watch's result changes."),
        ["watching.checkOnStartup"] = ("Check on startup", "Run every watch shortly after avamux starts."),
        ["events.enabled"] = ("Events enabled", "Turn timers, tasks and calendar triggers on or off."),
        ["events.scriptTimeoutSeconds"] = ("Script timeout (seconds)", "Scripts started by events are stopped after this long."),
        ["events.desktopNotifications"] = ("Desktop notifications", "Also notify through the desktop when something triggers."),
        ["events.missedGraceSeconds"] = ("Missed-trigger grace (seconds)", "Triggers missed by more than this (app closed, machine asleep) are skipped."),
        ["logs.level"] = ("Log level", "Least severe level written to the app log."),
        ["logs.maxTotalSizeMb"] = ("Maximum total size (MB)", "Oldest files are deleted when logs exceed this."),
        ["logs.maxAgeDays"] = ("Maximum age (days)", "Files older than this are deleted. 0 keeps them forever."),
        ["logs.maxFileCount"] = ("Maximum number of files", "0 means no limit."),
        ["logs.maxFileSizeMb"] = ("Maximum app log file size (MB)", "The app log rolls over to a new file at this size."),
        ["dashboard.port"] = ("Local port", "Port of the local web server (dashboard and terminals). 0 picks a free one. Applies after a restart."),
        ["dashboard.allowAppControl"] = ("Allow app control from JavaScript", "Lets scripts on the dashboard page run avamux commands."),
        ["dashboard.developerTools"] = ("Developer tools", "Enable the web inspector. Applies after a restart."),
        ["repositories.editor"] = ("Editor", "Program used for \"Edit\" in Repositories, e.g. nvim or vim."),
        ["repositories.logCount"] = ("Commits shown", "How many recent commits the history lists."),
        ["ui.startPage"] = ("Start page", "Page shown when avamux starts."),
        ["ui.taskbarAlert"] = ("Taskbar alerts", "Flash or badge the taskbar icon when something needs attention while avamux is in the background."),
        ["ui.bossKey"] = ("Boss key", "Swaps the window for the boss screen and back (Esc also brings avamux back). Written like Ctrl+Shift+B or F12; empty turns it off."),
        ["ui.bossScreen"] = ("Boss screen", "What it pretends to be: a budget spreadsheet, or a build that never quite finishes."),
        ["ui.bossHidesWindows"] = ("Boss screen minimizes other windows", "Also minimize avamux's other windows (detached terminals, the theme editor, …) while it is up."),
        ["caffeine.graceMinutes"] = ("Minutes awake after terminals finish", "\"While terminals are busy\" keeps the computer awake this long after the last one goes quiet."),
        ["caffeine.keepDisplayOn"] = ("Keep the screen on", "Stop the screen blanking or locking too, not just the computer sleeping."),
    };

    private static readonly Dictionary<string, string[]> Choices = new()
    {
        ["logs.level"] = ["debug", "info", "warn", "error"],
        ["ui.startPage"] = CommandRegistry.Pages,
        ["ui.navStyle"] = ["standard", "mini"],
        ["ui.bossScreen"] = BossScreen.Kinds,
        ["theme.name"] = Themes.All.Select(t => t.Id).ToArray(),
        ["theme.windowChrome"] = ["auto", "system", "custom"],
        ["theme.soundSet"] = ["theme", .. SoundService.Sets],
        ["terminal.cursorPulseShape"] = ["block", "outline", "bar"],
    };

    private static bool IsColor(string key) => key is "theme.accent" or "theme.glowColor" or "terminal.background" or "terminal.foreground" or "terminal.cursorPulseColor" || key.StartsWith("events.", StringComparison.Ordinal) && key.EndsWith("Color", StringComparison.Ordinal);

    private static readonly (string Key, string Title)[] Sections =
    [
        ("ui", "Window"), ("theme", "Themes"), ("caffeine", "Caffeine"), ("terminal", "Terminals"), ("watching", "Watching"), ("events", "Events"), ("wiki", "Wiki"),
        ("logs", "Logs"), ("dashboard", "Dashboard"), ("repositories", "Repositories"), ("ai", "AI Workloads"),
    ];

    private readonly ComboBox _scope = new() { MinWidth = 250 };
    private readonly TextBlock _scopeFile = new() { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _files = new() { Spacing = 3 };
    private readonly Dictionary<string, Action<ConfigValue>> _updaters = new();
    private readonly List<ConfigScope> _scopes = [];
    private readonly Dictionary<string, Control> _rows = new();
    private readonly WrapPanel _themeTiles = new();
    private readonly StackPanel _themeProblems = new() { Spacing = 2 };
    private readonly ScrollViewer _scroll = new();
    private readonly TextBox _find = new() { PlaceholderText = "Search settings…", FontSize = 14, Padding = new Thickness(12, 8) };
    private readonly TextBlock _findStatus = new() { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(Control Card, List<string> Keys)> _cards = [];
    private readonly List<Control> _filterHidden = [];
    private readonly CustomPagesCard _customPages = new();
    private bool _loading;

    private static ConfigService Config => App.Services.Config;

    public SettingsPage()
    {
        var paths = App.Services.Paths;
        var sameDir = Path.GetFullPath(paths.HomeDir) == Path.GetFullPath(paths.WorkingDir);
        foreach (var (scope, label) in new[]
                 {
                     (ConfigScope.Home, "Home folder"), (ConfigScope.HomeLocal, "Home folder, local (.local)"),
                     (ConfigScope.Working, "Working folder"), (ConfigScope.WorkingLocal, "Working folder, local (.local)"),
                 })
        {
            if (sameDir && scope is ConfigScope.Working or ConfigScope.WorkingLocal && sameDir) continue;
            _scopes.Add(scope);
            _scope.Items.Add(label);
        }
        _scope.SelectedIndex = 0;
        _scope.SelectionChanged += (_, _) => UpdateScopeFile();

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(20, 0, 20, 28), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Text("Where settings are saved", "h2"),
                Ui.Apply(Ui.Text("Settings are read from four files and merged; later files win: home → home .local → working folder → working folder .local. " +
                                 "A file only needs the keys it changes. Use the .local files for values you do not want to share.", "dim"), t => t.FontSize = 12.5),
                new DockPanel { Children = { Ui.Text("Save changes to").Dock(Dock.Left), Ui.Apply(_scope, c => c.Margin = new Thickness(10, 0)).Dock(Dock.Left), _scopeFile } },
                _files,
            },
        }));

        var all = Config.Describe();
        foreach (var (prefix, title) in Sections)
        {
            var rows = new StackPanel { Spacing = 12 };
            rows.Children.Add(Ui.Text(title, "h2"));
            if (prefix == "theme")
            {
                var tools = Ui.Row(8,
                    Ui.IconButton(Icons.Palette, "Edit themes and see the changes as you make them", () => ThemeEditorWindow.Open(this), "Theme Editor…"),
                    Ui.IconButton(Icons.FolderOpen, $"Theme files (.yaml, .json) are read from {App.Services.ThemeFiles.Dir}", () => Shell.Open(App.Services.ThemeFiles.Dir), "Themes Folder"));
                rows.Children.Add(Ui.Apply(tools, c => _filterHidden.Add(c)));
                rows.Children.Add(_themeProblems);
                _filterHidden.Add(_themeProblems);
                rows.Children.Add(_themeTiles);
            }
            if (prefix == "ai") rows.Children.Add(Ui.Text("AI Chat and Workloads are placeholders for now. Switch them on to show them in the navigation.", "dim", 12.5));
            foreach (var v in all.Where(v => v.Path.StartsWith(prefix + ".", StringComparison.Ordinal))) rows.Children.Add(_rows[v.Path] = BuildRow(v));
            if (prefix == "theme") rows.Children.Add(Ui.Apply(SoundTests(), c => _filterHidden.Add(c)));
            if (rows.Children.Count > 1)
            {
                var card = Ui.Card(rows);
                body.Children.Add(card);
                _cards.Add((card, all.Where(v => v.Path.StartsWith(prefix + ".", StringComparison.Ordinal)).Select(v => v.Path).ToList()));
            }
            // The navigation is part of the window, so the user's own pages come right after its settings.
            if (prefix == "ui") body.Children.Add(_customPages);
        }
        _filterHidden.Add(_themeTiles);
#if EASTER_EGGS
        EasterEggs.DriveA.Attach(_find, body, _filterHidden, () => { if (_find.Text?.Length > 0) _find.Text = ""; });
#endif
        _find.TextChanged += (_, _) => _filterSoon();
        _find.KeyDown += (_, e) => { if (e.Key == Key.Escape && _find.Text?.Length > 0) { e.Handled = true; _find.Text = ""; } };

        Content = new DockPanel
        {
            Children =
            {
                Ui.Header("Settings", "Changes apply immediately unless noted.",
                    Ui.IconButton(Icons.Refresh, "Re-read the configuration files", () => Config.Reload(), "Reload")).Dock(Dock.Top),
                new DockPanel { Margin = new Thickness(20, 0, 20, 10), Width = 860, HorizontalAlignment = HorizontalAlignment.Left, Children = { _findStatus.Dock(Dock.Right), Ui.Apply(_find, f => f.Margin = new Thickness(0, 0, 12, 0)) } }.Dock(Dock.Top),
                Ui.Apply(_scroll, v => v.Content = body),
            },
        };
        _filterSoon = Ui.Debounced(() => _ = ApplyFilterAsync(), 150);
        Config.Changed += Ui.Debounced(Refresh);
        Themes.ListChanged += Ui.Debounced(Refresh);
        Refresh();
    }

    public void OnShown() => Refresh();

    private Action _filterSoon = () => { };
    private int _filterRun;

    /// <summary>
    /// Narrows the page to the settings that match the search box. The matching is the Settings source of the main search
    /// (name and value), plus the label and help text shown on this page.
    /// </summary>
    private async Task ApplyFilterAsync()
    {
        var query = (_find.Text ?? "").Trim();
        var run = ++_filterRun;
        HashSet<string>? hits = null;
        if (query.Length > 0)
        {
            var found = await Task.Run(() => App.Services.Search.MatchingSettings(query));
            if (run != _filterRun) return;
            hits = found;
            foreach (var (key, (label, help)) in Docs)
                if (label.Contains(query, StringComparison.OrdinalIgnoreCase) || help.Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(key);
        }
        var shown = 0;
        foreach (var (card, keys) in _cards)
        {
            var any = false;
            foreach (var key in keys)
            {
                if (!_rows.TryGetValue(key, out var row)) continue;
                var on = hits is null || hits.Contains(key);
                row.IsVisible = on;
                if (on) { any = true; shown++; }
            }
            card.IsVisible = any;
        }
        foreach (var c in _filterHidden) c.IsVisible = hits is null;
        _customPages.IsVisible = hits is null || _customPages.Matches(query);
        _findStatus.Text = hits is null ? "" : shown > 0 ? $"{shown} setting{(shown == 1 ? "" : "s")}" : _customPages.IsVisible ? "" : "Nothing found";
        if (hits is not null) _scroll.Offset = default;
    }

    /// <summary>Scrolls to a setting and marks it for a moment (used by Search).</summary>
    public void Reveal(string key)
    {
        if (!_rows.TryGetValue(key, out var row)) return;
        // A filter left in the box would hide the very setting being revealed.
        if (_find.Text?.Length > 0) { _find.Text = ""; _filterRun++; _ = ApplyFilterAsync(); }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            row.BringIntoView();
            if (row is Panel panel)
            {
                panel.Background = Ui.Tk("cellSel");
                Avalonia.Threading.DispatcherTimer.RunOnce(() => panel.Background = null, TimeSpan.FromSeconds(1.6));
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>One tile per theme: a small sample of its colours. Clicking one switches to it.</summary>
    private void BuildThemeTiles()
    {
        _themeTiles.Children.Clear();
        var current = Themes.Find(Config.Settings.Theme.Name).Id;
        foreach (var t in Themes.All)
        {
            var selected = t.Id == current;
            var sample = new Border
            {
                Width = 150, Height = 54, Background = Ui.Raw(t["bg"]), CornerRadius = new CornerRadius(t.Radius > 0 ? 5 : 0), ClipToBounds = true,
                Child = new DockPanel
                {
                    Children =
                    {
                        new Border { Height = 12, Background = Ui.Raw(t["titleBg"]) }.Dock(Dock.Top),
                        new Border { Width = 22, Background = Ui.Raw(t["bgDeep"]), BorderBrush = Ui.Raw(t["line"]), BorderThickness = new Thickness(0, 0, 1, 0) }.Dock(Dock.Left),
                        new StackPanel
                        {
                            Margin = new Thickness(7, 6), Spacing = 5,
                            Children =
                            {
                                new Border { Height = 5, Width = 70, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Raw(t["text"]) },
                                Ui.Row(4, new[] { "accent", "ok", "warn", "error", "purple" }.Select(k => (Control)new Border { Width = 14, Height = 9, Background = Ui.Raw(t[k]) }).ToArray()),
                            },
                        },
                    },
                },
            };
            var tile = new Border
            {
                Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(5), CornerRadius = Ui.R(7), Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(2), BorderBrush = selected ? Ui.Tk("accent") : Brushes.Transparent, Background = selected ? Ui.Tk("cellSel") : Brushes.Transparent,
                Child = Ui.Column(4, sample, new TextBlock { Text = t.Name, FontSize = 12, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal },
                    new TextBlock { Text = t.File is null ? "built in" : Themes.IsBuiltinId(t.Id) ? "file · replaces built-in" : "file", FontSize = 10.5, Foreground = Ui.Tk("faint") }),
            };
            ToolTip.SetTip(tile, string.IsNullOrEmpty(t.Description) ? t.File ?? t.Name : t.Description + (t.File is null ? "" : "\n" + t.File));
            tile.ContextMenu = new ContextMenu
            {
                ItemsSource = new List<MenuItem>
                {
                    Ui.Item("Use This Theme", () => Commit("theme.name", t.Id)),
                    Ui.Item("Edit in Theme Editor…", () => ThemeEditorWindow.Open(this, t.Id), icon: Icons.Palette),
                    Ui.Item("Show File", () => Shell.ShowInFolder(t.File!), t.File is not null, Icons.FolderOpen),
                },
            };
            if (selected) tile.Classes.Add("glow");
            tile.PointerPressed += (_, _) => Commit("theme.name", t.Id);
            _themeTiles.Children.Add(tile);
        }
    }

    private Control SoundTests()
    {
        var sounds = App.Services.Sounds;
        var row = new WrapPanel();
        foreach (var (e, label) in new[]
                 {
                     (SoundEvent.EventStart, "Event starts"), (SoundEvent.EventEnd, "Event ends"), (SoundEvent.Timer, "Timer"),
                     (SoundEvent.WatchChanged, "Watch changes"), (SoundEvent.PullRequest, "Pull request"), (SoundEvent.AgentWaiting, "Agent waiting"),
                 })
            row.Children.Add(Ui.Apply(Ui.IconButton(Icons.Play, "Play this sound", () => sounds.Play(e, force: true), label), b => b.Margin = new Thickness(0, 0, 4, 4)));
        return Ui.Column(6,
            new TextBlock { Text = "Try the sounds" },
            row,
            Ui.Row(6, Ui.Apply(Ui.Text("To use your own, put event-start, event-end, timer, watch-changed, pull-request or agent-waiting (.wav, .ogg, .flac, .mp3) in", "dim", 11.5), t => t.MaxWidth = 560),
                Ui.Apply(Ui.Link("the custom sounds folder", () => { Directory.CreateDirectory(sounds.CustomFolder); Shell.Open(sounds.CustomFolder); }, sounds.CustomFolder), b => ((TextBlock)b.Content!).FontSize = 11.5)));
    }

    private ConfigScope Scope => _scopes[Math.Max(0, _scope.SelectedIndex)];

    private void UpdateScopeFile() => _scopeFile.Text = Config.FileFor(Scope);

    private Control BuildRow(ConfigValue initial)
    {
        var key = initial.Path;
        var (label, help) = Docs.TryGetValue(key, out var d) ? d : (key, "");
        var source = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = Ui.IconButton(Icons.Close, "Remove this override so the next file (or the default) applies", () => { });
        reset.Opacity = 0.7;
        var current = initial;
        reset.Click += (_, _) => Ui.Try(this, () => Config.Set(key, null, current.Source), "Could not reset the setting");

        Control editor;
        Action<JsonNode?> show;
        var kind = (JsonUtil.GetPath(ConfigService.Defaults(), key) ?? JsonValue.Create("")).GetValueKind();
        if (kind is JsonValueKind.True or JsonValueKind.False)
        {
            var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
            toggle.IsCheckedChanged += (_, _) => { if (!_loading) Commit(key, toggle.IsChecked == true ? "true" : "false"); };
            editor = toggle;
            show = v => toggle.IsChecked = v?.GetValueKind() == JsonValueKind.True;
        }
        else if (Choices.TryGetValue(key, out var options))
        {
            var combo = new ComboBox { MinWidth = 180 };
            foreach (var o in options) combo.Items.Add(o);
            combo.SelectionChanged += (_, _) => { if (!_loading && combo.SelectedItem is string s) Commit(key, s); };
            editor = combo;
            show = v =>
            {
                // Theme files come and go, so the list of themes is read again each time.
                if (key == "theme.name" && Themes.All.Select(t => t.Id).ToArray() is var ids && !ids.SequenceEqual(options))
                {
                    options = ids;
                    combo.Items.Clear();
                    foreach (var o in options) combo.Items.Add(o);
                }
                combo.SelectedItem = options.FirstOrDefault(o => string.Equals(o, v?.ToString(), StringComparison.OrdinalIgnoreCase));
            };
        }
        else if (key is "theme.soundVolume" or "theme.glowIntensity")
        {
            // A slider that saves once the user pauses, so a drag does not rewrite the config file on every tick.
            var slider = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 260, VerticalAlignment = VerticalAlignment.Center };
            var readout = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var pending = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            pending.Tick += (_, _) =>
            {
                pending.Stop();
                Commit(key, ((int)Math.Round(slider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            };
            slider.ValueChanged += (_, e) =>
            {
                readout.Text = $"{Math.Round(e.NewValue)}%";
                if (_loading) return;
                pending.Stop();
                pending.Start();
            };
            editor = Ui.Row(10, slider, readout);
            show = v =>
            {
                var volume = v is JsonValue jv && jv.TryGetValue<int>(out var n) ? n : key == "theme.glowIntensity" ? 60 : 70;
                readout.Text = $"{volume}%";
                // Do not pull the slider back while a save from a drag is still on its way.
                if (!pending.IsEnabled) slider.Value = Math.Clamp(volume, 0, 100);
            };
        }
        else
        {
            var number = kind == JsonValueKind.Number;
            var box = new TextBox { Width = number ? 130 : double.NaN, HorizontalAlignment = number ? HorizontalAlignment.Left : HorizontalAlignment.Stretch };
            if (key.EndsWith("fontFamily", StringComparison.Ordinal)) box.Classes.Add("mono");
            var shown = "";
            void Save()
            {
                var text = (box.Text ?? "").Trim();
                if (text == shown) return;
                if (!Commit(key, text)) box.Text = shown;
            }
            box.LostFocus += (_, _) => Save();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Save(); }
                else if (e.Key == Key.Escape) { e.Handled = true; box.Text = shown; }
            };
            editor = box;
            Border? swatch = null;
            if (IsColor(key))
            {
                box.Width = 130; box.HorizontalAlignment = HorizontalAlignment.Left; box.PlaceholderText = "theme"; box.Classes.Add("mono");
                swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
                var line = Ui.Row(8, swatch, box);
                if (key == "theme.accent")
                {
                    var presets = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    foreach (var hex in Themes.AccentPresets)
                    {
                        var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 6, 4), Background = Ui.Raw(hex), Cursor = new Cursor(StandardCursorType.Hand), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1) };
                        ToolTip.SetTip(dot, hex);
                        dot.PointerPressed += (_, _) => Commit(key, hex);
                        presets.Children.Add(dot);
                    }
                    editor = Ui.Column(0, line, presets);
                }
                else editor = line;
            }
            show = v =>
            {
                var text = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";
                var untouched = !box.IsFocused || box.Text == shown;
                shown = text;
                if (untouched) box.Text = text;
                if (swatch is not null) swatch.Background = Rgb.TryParse(text, out var rgb) ? Ui.Raw(rgb.Hex) : key == "theme.accent" ? Ui.Tk("accent") : Brushes.Transparent;
            };
        }

        _updaters[key] = v =>
        {
            current = v;
            show(v.Value);
            var overridden = v.Source != ConfigScope.Default;
            source.Text = v.Source switch
            {
                ConfigScope.Home => "home", ConfigScope.HomeLocal => "home .local", ConfigScope.Working => "working folder",
                ConfigScope.WorkingLocal => "working .local", _ => "default",
            };
            source.Foreground = overridden ? Ui.Tk("warn") : Ui.Dim;
            ToolTip.SetTip(source, overridden ? $"Set in {Config.FileFor(v.Source)}" : "Built-in default");
            reset.IsVisible = overridden;
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label });
        text.Children.Add(new TextBlock { Text = help.Length > 0 ? $"{help}  ({key})" : key, Foreground = Ui.Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        editor.VerticalAlignment = VerticalAlignment.Center;
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("320,*,110,36"),
            Children = { text, editor.At(0, 1), source.At(0, 2), reset.At(0, 3) },
        };
    }

    private bool Commit(string key, string text)
    {
        try
        {
            Config.Set(key, ConfigService.Coerce(key, text), Scope);
            // A higher-precedence file may still override what was just written; say so rather than look broken.
            var now = Config.Describe().First(v => v.Path == key);
            if (now.Source > Scope) Ui.Toast(this, "Saved, but overridden", $"{key} is also set in {Config.FileFor(now.Source)}, which wins.");
            return true;
        }
        catch (Exception ex)
        {
            Ui.Toast(this, "Could not save the setting", ex.GetBaseException().Message);
            Refresh();
            return false;
        }
    }

    private void Refresh()
    {
        _loading = true;
        try
        {
            foreach (var v in Config.Describe())
                if (_updaters.TryGetValue(v.Path, out var update)) update(v);
            BuildThemeTiles();
            _themeProblems.Children.Clear();
            foreach (var (file, problems) in App.Services.ThemeFiles.Problems)
                _themeProblems.Children.Add(new TextBlock
                {
                    Text = $"⚠ {Path.GetFileName(file)} could not be read: {string.Join(" ", problems.Take(3))}{(problems.Count > 3 ? " …" : "")}",
                    Foreground = Ui.Tk("warn"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                });
            UpdateScopeFile();
            _files.Children.Clear();
            foreach (var scope in _scopes)
            {
                var file = Config.FileFor(scope);
                var exists = File.Exists(file);
                _files.Children.Add(new TextBlock
                {
                    Text = $"{(exists ? "●" : "○")}  {file}{(exists ? "" : "   (not created yet)")}", FontSize = 12,
                    Foreground = exists ? Ui.Tk("textSoft") : Ui.Dim, FontFamily = Ui.Mono,
                });
            }
        }
        finally { _loading = false; }
    }
}
