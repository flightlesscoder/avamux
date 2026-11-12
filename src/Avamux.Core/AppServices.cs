using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Dashboard;
using Avamux.Core.Events;
using Avamux.Core.Logging;
using Avamux.Core.Repos;
using Avamux.Core.Search;
using Avamux.Core.Theming;
using Avamux.Core.Terminal;
using Avamux.Core.Watching;
using Avamux.Core.Web;
using Avamux.Core.Wiki;

namespace Avamux.Core;

/// <summary>Composition root: every long-lived service of a running avamux instance.</summary>
public sealed class AppServices : IAsyncDisposable
{
    private IAppHost _host = new NullAppHost();

    public AppServices(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        Config = new ConfigService(Paths);
        ApplyLogSettings();
        Config.Changed += ApplyLogSettings;
        Logs = new LogStore(Paths, Config);
        Ui = new UiStateStore(Paths, () => Config.Settings.Wiki.RememberedPositions);
        Sounds = new SoundService(Paths, Config);
        Terminals = new TerminalManager(Paths, Config);
        Watches = new WatchService(Paths, Config);
        Events = new EventService(Paths, Config);
        Wiki = new WikiService(Paths);
        Repos = new RepoService(Paths, Config);
        Web = new WebServer(this);
        Dashboard = new DashboardService(this);
        Search = new SearchService(this);
        Commands = CommandRegistry.Build(this);

        // Theme sounds for the things worth hearing about.
        Events.Fired += (item, phase, _) => Sounds.Play(item.Kind == EventKind.Timer ? SoundEvent.Timer : phase == "end" ? SoundEvent.EventEnd : SoundEvent.EventStart);
        Watches.ResultChanged += item => { if (item.LastChecked is not null && item.Notified) Sounds.Play(WatchService.IsPullRequestKind(item.Kind) ? SoundEvent.PullRequest : SoundEvent.WatchChanged); };
        Terminals.AgentWaiting += session =>
        {
            // Nothing to announce when that very tab is already in front of the user.
            if (Host.CurrentPage == "terminals" && Host.SelectedTerminal == session.Id) return;
            Sounds.Play(SoundEvent.AgentWaiting);
            if (Config.Settings.Terminal.NotifyAgentWaiting) Host.Notify($"{session.Agent ?? "Agent"} is waiting for input", session.Title);
        };
    }

    public AppPaths Paths { get; }
    public ConfigService Config { get; }
    public LogStore Logs { get; }
    public UiStateStore Ui { get; }
    public SoundService Sounds { get; }
    public SearchService Search { get; }
    public TerminalManager Terminals { get; }
    public WatchService Watches { get; }
    public EventService Events { get; }
    public WikiService Wiki { get; }
    public RepoService Repos { get; }
    public WebServer Web { get; }
    public DashboardService Dashboard { get; }
    public CommandRegistry Commands { get; }
    public DateTime Started { get; } = DateTime.Now;

    public IAppHost Host
    {
        get => _host;
        set { _host = value; Watches.Host = value; Events.Host = value; }
    }

    private void ApplyLogSettings()
    {
        var s = Config.Settings.Logs;
        Log.Configure(Paths.AppLogsDir, (long)s.MaxFileSizeMb * 1024 * 1024, Enum.TryParse<LogLevel>(s.Level, true, out var l) ? l : LogLevel.Info);
    }

    /// <summary>Starts background work: web server, config watching, watch checks, the event scheduler, log retention.</summary>
    public async Task StartAsync()
    {
        await Web.StartAsync();
        Config.Watch();
        Logs.Start();
        Watches.Start();
        Events.Start();
    }

    public async ValueTask DisposeAsync()
    {
        Ui.Save();
        Watches.Dispose();
        Events.Dispose();
        Terminals.Dispose();
        await Web.DisposeAsync();
        Config.Dispose();
    }
}
