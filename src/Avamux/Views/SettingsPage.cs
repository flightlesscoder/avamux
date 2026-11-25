using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avamux.Core;
using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Theming;
using Avamux.Plugins;

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
        ["terminal.crtGlow"] = ("CRT text glow", "fast: a soft halo over all the text. colour: every character glows in its own colour, but drawing is several times slower (nvim and other full-screen programs lag). off: no glow."),
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
        ["wiki.autoSave"] = ("Save automatically", "Save a page by itself once it has not been edited for the time below."),
        ["wiki.autoSaveSeconds"] = ("Seconds before saving", "How long a page must go without a change before it is saved automatically."),
        ["wiki.clickToPreviewMegabytes"] = ("Load big files on a click (MB)", "Pictures, PDFs, audio and video larger than this wait behind a \"Load preview\" button, and are not drawn in the editor. 0 loads everything at once."),
        ["wiki.editorPictures"] = ("Pictures in the editor", "Show the pictures a page embeds in the editor as well as the preview, each under the line that embeds it. The markdown stays as it is: click a picture to select its text."),
        ["wiki.commitWarning"] = ("Warn about big or binary files", "Before committing the wiki, list files that are big or not text (pictures are fine), which git would keep forever, with a way to leave them out."),
        ["wiki.commitWarningMegabytes"] = ("Big file size", "Files this many megabytes or larger count as big for the warning above."),
        ["wiki.syncScroll"] = ("Scroll editor and preview together", "With both shown side by side, scrolling one scrolls the other to the same part of the page."),
        ["wiki.editorPictureHeight"] = ("Picture height in the editor", "Tallest a picture is drawn in the editor, in pixels (40 to 2000). Smaller pictures keep their own size."),
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
        ["ai.workloads"] = ("AI Workloads", "Show the AI Workloads page in the navigation: model servers, agents, queues and models on disk."),
        ["ai.workloadScripts"] = ("Script folders", "Folders of start-*.sh / stop-*.sh pairs to offer as profiles on AI Workloads (comma separated)."),
        ["ai.workloadModels"] = ("Model folders", "Folders to search for model files besides the usual places (comma separated)."),
        ["ai.chatProvider"] = ("Provider for new chats", "Which of the providers above a new chat starts with."),
        ["ai.chatFolder"] = ("Working folder", "The folder agents work in for chats and AI Actions. Empty: the wiki folder."),
        ["ai.expandThinking"] = ("Show thinking open", "Show the model's thinking (for providers that show it) open instead of folded away."),
        ["ai.actionsProvider"] = ("AI Actions provider", "Which of the providers above AI Actions use, unless an action names another. Picking another one sets the model and effort below back to its own when it does not offer them."),
        ["ai.actionsModel"] = ("AI Actions model", "The model AI Actions use with that provider: pick one, or type its name."),
        ["ai.actionsEffort"] = ("AI Actions effort", "How hard the model thinks for AI Actions (the levels that provider offers)."),
        ["ai.tts.enabled"] = ("Speak with Kokoro", "Off by default. When something is to be spoken avamux asks to switch it on. Switching it off ends Kokoro and anything it is playing."),
        ["ai.tts.python"] = ("Kokoro: Python", "A Python with kokoro_onnx installed, e.g. ~/.venvs/kokoro/bin/python."),
        ["ai.tts.model"] = ("Kokoro: model file", "kokoro-v1.0.int8.onnx (or kokoro-v1.0.onnx)."),
        ["ai.tts.voices"] = ("Kokoro: voices file", "voices-v1.0.bin."),
        ["ai.tts.voice"] = ("Kokoro: voice", "af_nova, af_heart, af_bella, bm_george … (English voices; the Test Voice button lists them once Kokoro is on)."),
        ["ai.tts.speed"] = ("Kokoro: speed", "1 is normal."),
        ["ai.tts.volume"] = ("Kokoro: volume", "0 to 1."),
        ["ai.tts.autoSpeak"] = ("Speak replies as they are written", "Every reply in the chat open is spoken as it comes (Kokoro must be on)."),
        ["ai.tts.idleMinutes"] = ("Kokoro: unload after", "Minutes without speaking before the model is unloaded. 0 keeps it loaded until switched off."),
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
        ["ui.commandPaletteKey"] = ("Command palette key", "Opens the command palette from anywhere, terminals included: Shift Shift (press Shift twice quickly), Ctrl Ctrl, Alt Alt, Cmd Cmd, or a shortcut such as Ctrl+Shift+P (Cmd+Shift+P on a Mac). Empty turns it off."),
        ["ui.bossScreen"] = ("Boss screen", "What it pretends to be: a budget spreadsheet, or a build that never quite finishes."),
        ["ui.bossHidesWindows"] = ("Boss screen minimizes other windows", "Also minimize avamux's other windows (detached terminals, the theme editor, …) while it is up."),
        ["browser.agentControl"] = ("Let agents drive the browser", "`avamux browser` commands (and the AI agents in terminal tabs that run them) can open panes, read pages, click and type. Off, only listing works."),
        ["browser.homePage"] = ("Home page", "Address a new browser pane opens when none is given."),
        ["browser.searchUrl"] = ("Search", "Where words typed in the address bar are searched for; {query} is replaced by them."),
        ["browser.keepAliveByDefault"] = ("Keep new panes alive", "New panes keep running, and believe they are visible, while another tab or page is in front. Each pane has its own pin to change it."),
        ["browser.terminalLinksInPane"] = ("Open terminal links beside the tab", "A plain click on a link in a terminal opens it in the tab's browser pane; Ctrl+click opens the system browser. Off, it is the other way round."),
        ["browser.externalSites"] = ("Sites for the system browser", "Links to these always open in the system browser: host names or *.example.com, separated by commas."),
        ["browser.developerTools"] = ("Developer tools", "The Web Inspector in browser panes (F12). Applies to panes opened afterwards."),
        ["browser.setBrowserVariable"] = ("Set BROWSER in terminals", "Programs that open a web page (a login, a dev server's --open) open it in the tab's browser pane. Applies to new terminals."),
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
        ["terminal.crtGlow"] = ["fast", "colour", "off"],
    };

    private static bool IsColor(string key) => key is "theme.accent" or "theme.glowColor" or "terminal.background" or "terminal.foreground" or "terminal.cursorPulseColor" || key.StartsWith("events.", StringComparison.Ordinal) && key.EndsWith("Color", StringComparison.Ordinal);

    private static readonly (string Key, string Title)[] Sections =
    [
        ("ui", "Window"), ("theme", "Themes"), ("caffeine", "Caffeine"), ("terminal", "Terminals"), ("browser", "Browser panes"), ("watching", "Watching"), ("events", "Events"), ("wiki", "Wiki"),
        ("logs", "Logs"), ("dashboard", "Dashboard"), ("repositories", "Repositories"), ("ai", "AI Chat"),
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
    private readonly PluginsCard _plugins = new();
    private readonly AiProvidersCard _aiProviders = new();
    private readonly StackPanel _hotkeys = new() { Spacing = 16 };
    private readonly Border _hotkeysCard;
    private string _hotkeyQuery = "";
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
            if (prefix == "ai")
            {
                rows.Children.Add(_aiProviders);
                rows.Children.Add(Ui.Apply(Ui.Row(8,
                    Ui.IconButton(Icons.VolumeHigh, "Speak a sentence with the voice set below (asks to switch Kokoro on if it is off)", () => _ = SpeechUi.ToggleAsync(this, "Hello! This is how replies will sound.", "settings-test"), "Test Voice"),
                    Ui.IconButton(Icons.Stop, "Stop speaking", () => App.Services.Speech.Stop(), "Stop")), c => _filterHidden.Add(c)));
            }
            foreach (var v in all.Where(v => v.Path.StartsWith(prefix + ".", StringComparison.Ordinal))) rows.Children.Add(_rows[v.Path] = BuildRow(v));
            if (prefix == "theme") rows.Children.Add(Ui.Apply(SoundTests(), c => _filterHidden.Add(c)));
            if (rows.Children.Count > 1)
            {
                var card = Ui.Card(rows);
                body.Children.Add(card);
                _cards.Add((card, all.Where(v => v.Path.StartsWith(prefix + ".", StringComparison.Ordinal)).Select(v => v.Path).ToList()));
            }
            // The navigation is part of the window, so the user's own pages come right after its settings.
            if (prefix == "ui") { body.Children.Add(_customPages); body.Children.Add(_plugins); }
        }
        _hotkeysCard = Ui.Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Ui.Text("Hotkeys", "h2"),
                Ui.Text("Every key avamux handles, by where it works. Only the boss key can be changed (Window, above).", "dim", 12.5),
                _hotkeys,
            },
        });
        body.Children.Add(_hotkeysCard);
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
        App.Services.Ai.Changed += Ui.Debounced(Refresh); // providers added, renamed or removed: the AI dropdowns list them
        Themes.ListChanged += Ui.Debounced(Refresh);
        Refresh();
    }

    public void OnShown() => Refresh();

    /// <summary>Narrows the page to what matches <paramref name="query"/>, as typing it in the search box does.</summary>
    public void Find(string query) => _find.Text = query;

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
        var providersMatch = hits is null || _aiProviders.Matches(query);
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
            // The AI Chat card also holds the providers list, which matches on its own.
            card.IsVisible = any || providersMatch && _aiProviders.GetVisualAncestors().Contains(card);
        }
        foreach (var c in _filterHidden) c.IsVisible = hits is null;
        _customPages.IsVisible = hits is null || _customPages.Matches(query);
        _plugins.IsVisible = hits is null || _plugins.Matches(query);
        _aiProviders.IsVisible = providersMatch;
        _hotkeyQuery = query;
        var hotkeys = BuildHotkeys();
        var counts = new List<string>();
        if (shown > 0) counts.Add($"{shown} setting{(shown == 1 ? "" : "s")}");
        if (hotkeys > 0) counts.Add($"{hotkeys} hotkey{(hotkeys == 1 ? "" : "s")}");
        _findStatus.Text = hits is null ? "" : counts.Count > 0 ? string.Join(", ", counts) : _customPages.IsVisible || _plugins.IsVisible || providersMatch ? "" : "Nothing found";
        if (hits is not null) _scroll.Offset = default;
    }

    /// <summary>Fills the Hotkeys card with the shortcuts that match the search box (all of them when it is empty); returns how many.</summary>
    private int BuildHotkeys()
    {
        _hotkeys.Children.Clear();
        var count = 0;
        foreach (var group in Hotkeys.All(Config.Settings))
        {
            var entries = group.Entries.Where(e => _hotkeyQuery.Length == 0 || Hotkeys.Matches(group, e, _hotkeyQuery)).ToList();
            if (entries.Count == 0) continue;
            count += entries.Count;
            var block = new StackPanel { Spacing = 5 };
            block.Children.Add(new TextBlock { Text = group.Where, FontWeight = FontWeight.SemiBold, FontSize = 13.5 });
            if (group.Note is { } note) block.Children.Add(Ui.Text(note, "dim", 12));
            foreach (var entry in entries)
            {
                var keys = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
                for (var i = 0; i < entry.Keys.Length; i++)
                {
                    if (i > 0) keys.Children.Add(new TextBlock { Text = "or", Foreground = Ui.Dim, FontSize = 11, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center });
                    keys.Children.Add(KeyCap(entry.Keys[i]));
                }
                block.Children.Add(new DockPanel
                {
                    Children =
                    {
                        Ui.Apply(keys, k => k.Margin = new Thickness(16, 2, 0, 2)).Dock(Dock.Right),
                        new TextBlock { Text = entry.Action, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center },
                    },
                });
            }
            _hotkeys.Children.Add(block);
        }
        _hotkeysCard.IsVisible = count > 0;
        return count;
    }

    private static Border KeyCap(string keys) => new()
    {
        Background = Ui.Tk("bgEditor"), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 1), Margin = new Thickness(0, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = keys, FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Tk("textCode") },
    };

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
        else if (key is "ai.chatProvider" or "ai.actionsProvider" or "ai.actionsModel" or "ai.actionsEffort")
            editor = AiChoice(key, out show);
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

    /// <summary>
    /// A dropdown for the AI settings that name a provider, model or effort: the providers set up above by name (the
    /// setting keeps the provider's id), and the models and effort levels the AI Actions provider offers. A value that
    /// is not in the list (a provider since removed, a model typed by hand) is kept and shown as it is.
    /// </summary>
    private Control AiChoice(string key, out Action<JsonNode?> show)
    {
        var ai = App.Services.Ai;
        var isModel = key == "ai.actionsModel";
        var combo = new ComboBox { MinWidth = 240, IsEditable = isModel };
        var shown = "";
        var filling = false;
        var generation = 0;

        void Fill(IEnumerable<(string Label, string Value, string? Tip)> items, string current, string missing)
        {
            filling = true;
            try
            {
                combo.Items.Clear();
                foreach (var (label, value, tip) in items)
                    combo.Items.Add(Ui.Apply(new ComboBoxItem { Content = label, Tag = value }, i => { if (tip is not null) ToolTip.SetTip(i, tip); }));
                if (combo.Items.OfType<ComboBoxItem>().All(i => (string)i.Tag! != current))
                    combo.Items.Add(new ComboBoxItem { Content = current + missing, Tag = current });
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag! == current);
            }
            finally { filling = false; }
        }

        // The AI Actions provider as set now (the model and effort lists follow it).
        AiProviderConfig? ActionsProvider() => ai.DefaultProviderId is { } id ? ai.Config(id) : null;

        void Choose(string value)
        {
            if (value == shown) return;
            if (!Commit(key, value)) return;
            shown = value;
            if (key == "ai.actionsProvider") _ = FitModelAndEffortAsync(value);
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || filling || combo.SelectedItem is not ComboBoxItem { Tag: string v }) return;
            Choose(v);
        };
        if (isModel)
        {
            // A typed name is kept as it is; one that matches an item's label means that item.
            void Typed()
            {
                var text = (combo.Text ?? "").Trim();
                var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Content as string, text, StringComparison.OrdinalIgnoreCase));
                Choose(item is { Tag: string v } ? v : text);
            }
            combo.LostFocus += (_, _) => Typed();
            combo.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Typed(); } };
        }

        show = v =>
        {
            var current = v is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : "";
            shown = current;
            var configs = ai.Configs;
            switch (key)
            {
                case "ai.chatProvider" or "ai.actionsProvider":
                    Fill([("The first one in the list", "", "Whichever provider is first above (and switched on)"),
                          .. configs.Select(c => (ProviderLabel(c, configs), c.Id, (string?)$"{ai.Kind(c.Kind)?.DisplayName ?? c.Kind} · id {c.Id}"))],
                        current, "  (not set up)");
                    break;
                case "ai.actionsEffort":
                {
                    var p = ActionsProvider();
                    var levels = p is null ? [] : ai.Kind(p.Kind)?.Capabilities.EffortLevels ?? [];
                    Fill([("The provider's own", "", null), .. levels.Select(l => (l, l, (string?)null))], current, levels.Count == 0 ? "" : "  (not offered)");
                    combo.IsEnabled = levels.Count > 0 || current.Length > 0;
                    break;
                }
                default:
                {
                    var p = ActionsProvider();
                    var run = ++generation;
                    Fill([("The provider's own", "", null)], current, "");
                    if (p is null) break;
                    _ = Load();
                    async Task Load()
                    {
                        var models = await ai.ModelsAsync(p.Id);
                        if (run != generation || models.Count == 0) return;
                        var items = models.Select(m => (m.Id.Length == 0 ? "The provider's own" : m.Name, m.Id, m.Note)).ToList();
                        if (items.All(i => i.Id != "")) items.Insert(0, ("The provider's own", "", null));
                        Fill(items, shown, "");
                    }
                    break;
                }
            }
        };
        return combo;
    }

    /// <summary>A provider's name in a list, with its kind when another provider has the same name, and whether it is off.</summary>
    private static string ProviderLabel(AiProviderConfig c, IReadOnlyList<AiProviderConfig> all)
    {
        var kind = App.Services.Ai.Kind(c.Kind)?.DisplayName;
        var label = c.Name;
        if (kind is not null && all.Count(o => string.Equals(o.Name, c.Name, StringComparison.OrdinalIgnoreCase)) > 1) label += " · " + kind;
        return c.Enabled ? label : label + "  (switched off)";
    }

    /// <summary>
    /// After the AI Actions provider changes: a model or effort the new provider does not offer would make every action
    /// fail, so those go back to the provider's own.
    /// </summary>
    private async Task FitModelAndEffortAsync(string providerId)
    {
        var ai = App.Services.Ai;
        var settings = Config.Settings.Ai;
        if ((providerId.Length > 0 ? ai.Config(providerId) : ai.DefaultProviderId is { } d ? ai.Config(d) : null) is not { } p) return;
        var levels = ai.Kind(p.Kind)?.Capabilities.EffortLevels ?? [];
        var model = settings.ActionsModel;
        var effort = settings.ActionsEffort;
        var reset = new List<string>();
        if (effort.Length > 0 && !levels.Contains(effort)) { Commit("ai.actionsEffort", ""); reset.Add("effort"); }
        if (model.Length > 0 && (await ai.ModelsAsync(p.Id)).All(m => m.Id != model)) { Commit("ai.actionsModel", ""); reset.Add("model"); }
        if (reset.Count > 0) Ui.Toast(this, "AI Actions", $"{p.Name} does not offer {string.Join(" or ", reset.Select(r => r == "model" ? "the model " + model : "the effort " + effort))}, so it uses its own.");
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
            BuildHotkeys(); // the boss key and the terminal link click come from settings
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
