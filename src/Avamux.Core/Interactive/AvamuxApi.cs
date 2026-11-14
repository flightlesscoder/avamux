using System.Text.Json;
using System.Text.Json.Nodes;
using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Events;
using Avamux.Core.Logging;
using Avamux.Core.Nav;
using Avamux.Core.Repos;
using Avamux.Core.Search;
using Avamux.Core.Terminal;
using Avamux.Core.Watching;
using Avamux.Core.Wiki;

namespace Avamux.Core.Interactive;

/// <summary>
/// The running avamux as C# Interactive sees it (the <c>avamux</c> variable): the services themselves, and the
/// same commands the command line and the dashboard's <c>window.avamux</c> have.
/// </summary>
public sealed class AvamuxApi(AppServices services)
{
    /// <summary>Every long-lived service of this avamux.</summary>
    public AppServices Services => services;
    public TerminalManager Terminals => services.Terminals;
    public WatchService Watches => services.Watches;
    public EventService Events => services.Events;
    public WikiService Wiki => services.Wiki;
    public RepoService Repos => services.Repos;
    /// <summary>Theme files (the themes folder); built-in and file themes together are Avamux.Core.Theming.Themes.All.</summary>
    public Avamux.Core.Theming.ThemeStore Themes => services.ThemeFiles;
    /// <summary>Keep the computer awake: Indefinite(), For(TimeSpan), WhileTerminalsBusy(), Off(), Status.</summary>
    public Avamux.Core.Awake.CaffeineService Caffeine => services.Caffeine;
    public NavService Nav => services.Nav;
    public ConfigService Config => services.Config;
    public AppSettings Settings => services.Config.Settings;
    public SearchService Search => services.Search;
    public LogStore Logs => services.Logs;
    public AppPaths Paths => services.Paths;
    /// <summary>The window: navigate, select a terminal, the clipboard.</summary>
    public IAppHost Host => services.Host;
    public string Version => typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>
    /// Runs an avamux command by name ("terminal new", "watch list", "event timer" …) and returns its result.
    /// Arguments are a <see cref="JsonObject"/>, a dictionary, or any object whose properties are the option names:
    /// <c>await avamux.Command("terminal new", new { name = "build" })</c>.
    /// </summary>
    public async Task<JsonNode?> Command(string name, object? args = null)
    {
        var json = args switch
        {
            null => new JsonObject(),
            JsonObject o => (JsonObject)o.DeepClone(),
            _ => JsonSerializer.SerializeToNode(args) as JsonObject ?? throw new ArgumentException("Arguments must be an object with one property per option."),
        };
        var response = await services.Commands.ExecuteAsync(name, json);
        if (response["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? $"'{name}' failed.");
        return response["result"]?.DeepClone();
    }

    /// <summary>Runs a command written as on the command line: <c>await avamux.Run("terminal", "new", "--name", "top", "--", "top")</c>.</summary>
    public Task<JsonNode?> Run(params string[] argv)
    {
        var parsed = CliParser.Parse(services.Commands, argv);
        if (parsed.Error is not null) throw new InvalidOperationException(parsed.Error);
        if (parsed.Command is null) throw new InvalidOperationException("No command given.");
        return Command(parsed.Command.Name, parsed.Args);
    }

    /// <summary>The names of every command.</summary>
    public IReadOnlyList<string> Commands() => services.Commands.Describe().Select(c => c?["command"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList();

    /// <summary>Shows a notification in avamux.</summary>
    public void Notify(string title, string message = "") => services.Host.Notify(title, message);

    /// <summary>Switches the window to a page: "terminals", "wiki" …, or a custom page's name.</summary>
    public void Navigate(string page) => services.Host.Navigate(page);

    public override string ToString() => $"avamux {Version}";
}
