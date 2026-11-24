namespace Avamux.Core.Config;

/// <summary>The application's default settings. Every value can be overridden from avamux.json / avamux.local.json.</summary>
public sealed class AppSettings
{
    public TerminalSettings Terminal { get; set; } = new();
    public WatchingSettings Watching { get; set; } = new();
    public EventSettings Events { get; set; } = new();
    public LogSettings Logs { get; set; } = new();
    public DashboardSettings Dashboard { get; set; } = new();
    public RepositorySettings Repositories { get; set; } = new();
    public WikiSettings Wiki { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
    public CaffeineSettings Caffeine { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
}

public sealed class TerminalSettings
{
    /// <summary>Lines of scrollback kept per terminal tab.</summary>
    public int Scrollback { get; set; } = 10000;
    public string FontFamily { get; set; } = "JetBrainsMono Nerd Font Mono, JetBrainsMono Nerd Font, Symbols Nerd Font Mono, Symbols Nerd Font, Menlo, DejaVu Sans Mono, Noto Color Emoji, monospace";
    public double FontSize { get; set; } = 13;
    /// <summary>Shell to launch. Empty means $SHELL.</summary>
    public string Shell { get; set; } = "";
    public string Term { get; set; } = "xterm-256color";
    public bool CopyOnSelect { get; set; }
    public bool CursorBlink { get; set; } = true;
    /// <summary>Replaces the blinking cursor with one that beats like a pulse and glows.</summary>
    public bool CursorPulse { get; set; }
    /// <summary>block, outline or bar.</summary>
    public string CursorPulseShape { get; set; } = "block";
    /// <summary>Empty follows the theme's accent.</summary>
    public string CursorPulseColor { get; set; } = "";
    /// <summary>Beat twice per cycle, like a heart, instead of once.</summary>
    public bool CursorPulseDouble { get; set; }
    /// <summary>Beats per minute.</summary>
    public int CursorPulseRate { get; set; } = 30;
    /// <summary>Strength of the glow, 0 to 100.</summary>
    public int CursorPulseGlow { get; set; } = 60;
    /// <summary>Old monitor look for the terminal view: scanlines, glowing text, darkened corners.</summary>
    public bool Crt { get; set; }
    /// <summary>Strength of the CRT effect, 0 to 100.</summary>
    public int CrtIntensity { get; set; } = 50;
    /// <summary>A faint flicker, like a tube that is not quite steady.</summary>
    public bool CrtFlicker { get; set; }
    public bool CloseTabOnExit { get; set; } = true;
    /// <summary>Save the scrollback of a terminal to the Logs page when it ends.</summary>
    public bool PreserveDeadSessions { get; set; } = true;
    /// <summary>Empty follows the theme.</summary>
    public string Background { get; set; } = "";
    /// <summary>Empty follows the theme.</summary>
    public string Foreground { get; set; } = "";
    /// <summary>Program names treated as AI agents, so a tab can show when one is waiting for input.</summary>
    public string AgentProcesses { get; set; } = "claude, codex, gemini, aider, opencode, goose, amp, cursor-agent, copilot, qwen, crush, droid, kiro-cli, pi";
    /// <summary>An agent that has printed nothing for this long is taken to be waiting for input.</summary>
    public double AgentIdleSeconds { get; set; } = 3;
    public bool NotifyAgentWaiting { get; set; } = true;
}

public sealed class WatchingSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minutes between checks for watches that do not set their own interval.</summary>
    public double DefaultIntervalMinutes { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 60;
    public bool NotifyOnChange { get; set; } = true;
    public bool CheckOnStartup { get; set; } = true;
}

public sealed class EventSettings
{
    public bool Enabled { get; set; } = true;
    public int ScriptTimeoutSeconds { get; set; } = 300;
    public bool DesktopNotifications { get; set; } = true;
    /// <summary>An occurrence missed by more than this many seconds (app closed, machine asleep) is skipped.</summary>
    public int MissedGraceSeconds { get; set; } = 120;
    /// <summary>Calendar colours per kind. Empty follows the theme. An event's own colour wins.</summary>
    public string CalendarColor { get; set; } = "";
    public string TaskColor { get; set; } = "";
    public string TimerColor { get; set; } = "";
}

public sealed class LogSettings
{
    public string Level { get; set; } = "info";
    public int MaxTotalSizeMb { get; set; } = 200;
    public int MaxAgeDays { get; set; } = 30;
    public int MaxFileCount { get; set; } = 500;
    public int MaxFileSizeMb { get; set; } = 10;
}

public sealed class DashboardSettings
{
    /// <summary>Local port for the dashboard / terminal web server. 0 picks a free port.</summary>
    public int Port { get; set; } = 47600;
    /// <summary>Allow JavaScript on the dashboard page to run avamux commands.</summary>
    public bool AllowAppControl { get; set; } = true;
    public bool DeveloperTools { get; set; } = true;
}

public sealed class RepositorySettings
{
    public string Editor { get; set; } = "nvim";
    public int LogCount { get; set; } = 50;
    /// <summary>Default command that lists a repository's pull requests. Empty uses the GitHub CLI (gh).</summary>
    public string PullRequestCommand { get; set; } = "";
}

public sealed class WikiSettings
{
    /// <summary>.NET date format of the daily note's file name.</summary>
    public string DailyNoteFormat { get; set; } = "yyyy-MM-dd dddd";
    /// <summary>Folder inside the wiki for daily notes. Empty is the wiki root.</summary>
    public string DailyNoteFolder { get; set; } = "Daily";
    /// <summary>Content of a new daily note. {date} and {title} are replaced.</summary>
    public string DailyNoteTemplate { get; set; } = "# {title}\n\n";
    /// <summary>How many files have their last scroll position remembered.</summary>
    public int RememberedPositions { get; set; } = 500;
    /// <summary>Save a page automatically once it has not been edited for <see cref="AutoSaveSeconds"/>.</summary>
    public bool AutoSave { get; set; } = true;
    /// <summary>Pictures, PDFs, audio and video larger than this many megabytes are previewed only after a click. 0 loads every file at once.</summary>
    public double ClickToPreviewMegabytes { get; set; } = 20;
    /// <summary>Seconds without a change before an edited page is saved.</summary>
    public double AutoSaveSeconds { get; set; } = 2;
    /// <summary>Show the pictures a page embeds in the editor too, under the line that embeds them, not just in the preview.</summary>
    public bool EditorPictures { get; set; } = true;
    /// <summary>Tallest a picture is drawn in the editor, in pixels. Smaller pictures keep their own size.</summary>
    public double EditorPictureHeight { get; set; } = 220;
    /// <summary>With the editor and the preview side by side, scrolling one scrolls the other to the same place.</summary>
    public bool SyncScroll { get; set; } = true;
    /// <summary>Before committing the wiki, point out files that are big or not text (pictures excepted), which git keeps forever.</summary>
    public bool CommitWarning { get; set; } = true;
    /// <summary>Files this many megabytes or more count as big for <see cref="CommitWarning"/>.</summary>
    public double CommitWarningMegabytes { get; set; } = 5;
}

public sealed class UiSettings
{
    public string StartPage { get; set; } = "terminals";
    /// <summary>standard (icons with labels) or mini (small icons with tooltips).</summary>
    public string NavStyle { get; set; } = "standard";
    /// <summary>Flash or badge the taskbar / dock icon when something needs attention while avamux is in the background.</summary>
    public bool TaskbarAlert { get; set; } = true;
    /// <summary>Key that swaps the window for the boss screen and back, e.g. Ctrl+Shift+B. Empty turns it off.</summary>
    public string BossKey { get; set; } = "Ctrl+Shift+B";
    /// <summary>Opens the command palette from anywhere: a modifier pressed twice ("Shift Shift", "Ctrl Ctrl", "Alt Alt") or a shortcut such as Ctrl+Shift+P. Empty turns it off.</summary>
    public string CommandPaletteKey { get; set; } = "Shift Shift";
    /// <summary>What the boss screen pretends to be: spreadsheet or build.</summary>
    public string BossScreen { get; set; } = "spreadsheet";
    /// <summary>Minimize avamux's other windows while the boss screen is up.</summary>
    public bool BossHidesWindows { get; set; } = true;
}

public sealed class CaffeineSettings
{
    /// <summary>"While terminals are busy" keeps the computer awake this many minutes after the last one finishes.</summary>
    public int GraceMinutes { get; set; } = 5;
    /// <summary>Keep the screen on too, not just stop the computer sleeping.</summary>
    public bool KeepDisplayOn { get; set; } = true;
}

public sealed class ThemeSettings
{
    public string Name { get; set; } = "simple";
    /// <summary>Accent colour as #rrggbb. Empty uses the theme's own.</summary>
    public string Accent { get; set; } = "";
    /// <summary>auto (the theme decides), system (the desktop's title bar) or custom (drawn by avamux).</summary>
    public string WindowChrome { get; set; } = "auto";
    /// <summary>A neon glow around whatever is selected or current: list and tree selections, the active navigation button and tab, today in the calendar.</summary>
    public bool Glow { get; set; }
    /// <summary>Strength of the glow, 0 to 100.</summary>
    public int GlowIntensity { get; set; } = 60;
    /// <summary>Colour of the glow as #rrggbb. Empty follows the theme's accent.</summary>
    public string GlowColor { get; set; } = "";
    public bool Sounds { get; set; } = true;
    /// <summary>0 to 100.</summary>
    public int SoundVolume { get; set; } = 70;
    /// <summary>theme (the theme's own), soft, retro or synth.</summary>
    public string SoundSet { get; set; } = "theme";
    public bool SoundOnEvent { get; set; } = true;
    public bool SoundOnTimer { get; set; } = true;
    public bool SoundOnWatch { get; set; } = true;
    public bool SoundOnPullRequest { get; set; } = true;
    public bool SoundOnAgentWaiting { get; set; } = true;
}

public sealed class AiSettings
{
    /// <summary>Show the AI Chat page in the navigation.</summary>
    public bool Chat { get; set; } = true;
    /// <summary>Show the Workloads page in the navigation.</summary>
    public bool Workloads { get; set; }
    /// <summary>The provider new chats start with (an id from Settings → AI Chat). Empty: the first one.</summary>
    public string ChatProvider { get; set; } = "";
    /// <summary>The agent's working folder for chats and actions. Empty: the wiki folder.</summary>
    public string ChatFolder { get; set; } = "";
    /// <summary>Show the model's thinking (when the provider shows it) open rather than folded away.</summary>
    public bool ExpandThinking { get; set; }
    /// <summary>The provider AI Actions use unless an action names another.</summary>
    public string ActionsProvider { get; set; } = "claude-code";
    /// <summary>The model AI Actions use with their default provider. Empty: the provider's own.</summary>
    public string ActionsModel { get; set; } = "haiku";
    /// <summary>The effort AI Actions use with their default provider. Empty: the provider's own.</summary>
    public string ActionsEffort { get; set; } = "low";
    public TtsSettings Tts { get; set; } = new();
}

/// <summary>Speaking replies aloud with Kokoro, run by a small Python helper. Off until switched on.</summary>
public sealed class TtsSettings
{
    /// <summary>Off by default: nothing is started until it is switched on (avamux asks the first time something is to be spoken).</summary>
    public bool Enabled { get; set; }
    /// <summary>The Python that has kokoro_onnx (and onnxruntime) installed, e.g. a project's .venv/bin/python.</summary>
    public string Python { get; set; } = "";
    /// <summary>The Kokoro model file (kokoro-v1.0.int8.onnx or kokoro-v1.0.onnx).</summary>
    public string Model { get; set; } = "";
    /// <summary>The voices file (voices-v1.0.bin).</summary>
    public string Voices { get; set; } = "";
    public string Voice { get; set; } = "af_nova";
    public double Speed { get; set; } = 1.05;
    /// <summary>0 to 1.</summary>
    public double Volume { get; set; } = 0.85;
    /// <summary>Speak every reply in AI Chat as it is written.</summary>
    public bool AutoSpeak { get; set; }
    /// <summary>Unload the model after this many minutes without speaking. 0 keeps it loaded until switched off.</summary>
    public double IdleMinutes { get; set; } = 10;
}

public sealed class BrowserSettings
{
    /// <summary>Let `avamux browser` commands (and the agents in terminal tabs) drive browser panes.</summary>
    public bool AgentControl { get; set; } = true;
    /// <summary>Address a new browser pane opens when none is given.</summary>
    public string HomePage { get; set; } = "about:blank";
    /// <summary>Where text typed in the address bar is searched for; {query} is replaced by it.</summary>
    public string SearchUrl { get; set; } = "https://duckduckgo.com/?q={query}";
    /// <summary>New panes keep running (and believe they are visible) while another tab or page is in front.</summary>
    public bool KeepAliveByDefault { get; set; }
    /// <summary>A plain click on a link in a terminal opens it in the tab's browser pane; Ctrl+click does the opposite.</summary>
    public bool TerminalLinksInPane { get; set; }
    /// <summary>Sites whose links always open in the system browser: host names or *.example.com, separated by commas.</summary>
    public string ExternalSites { get; set; } = "";
    /// <summary>The Web Inspector in browser panes. Applies to panes opened afterwards.</summary>
    public bool DeveloperTools { get; set; } = true;
    /// <summary>Set BROWSER in new terminals, so programs that open a web page open it in the tab's browser pane.</summary>
    public bool SetBrowserVariable { get; set; }
}
