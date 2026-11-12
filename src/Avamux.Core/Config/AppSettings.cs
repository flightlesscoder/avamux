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
    public AiSettings Ai { get; set; } = new();
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
}

public sealed class UiSettings
{
    public string StartPage { get; set; } = "terminals";
    /// <summary>standard (icons with labels) or mini (small icons with tooltips).</summary>
    public string NavStyle { get; set; } = "standard";
}

public sealed class ThemeSettings
{
    public string Name { get; set; } = "simple";
    /// <summary>Accent colour as #rrggbb. Empty uses the theme's own.</summary>
    public string Accent { get; set; } = "";
    /// <summary>auto (the theme decides), system (the desktop's title bar) or custom (drawn by avamux).</summary>
    public string WindowChrome { get; set; } = "auto";
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
    public bool Chat { get; set; }
    /// <summary>Show the Workloads page in the navigation.</summary>
    public bool Workloads { get; set; }
}
