using System.Text.Json.Nodes;

namespace Avamux.Plugins;

/// <summary>
/// A plugin: a .NET class library that adds things to avamux without (necessarily) a page of its own. What it adds,
/// it registers in <see cref="Activate"/>: right-click menu items, watch kinds, AI chat providers, a settings panel.
/// avamux takes every registration back itself when the plugin is switched off, so <see cref="Deactivate"/> only has
/// to stop what the plugin started on its own (timers, processes).
///
/// Put the built DLL (with anything it needs that avamux does not have) in a folder of its own under avamux's plugins
/// folder (<c>avamux app paths</c>), or install it from Settings → Plugins or <c>avamux plugin install</c>. Every
/// plugin is listed under Settings → Plugins, where it can be switched off, reloaded or uninstalled. The class needs a
/// public parameterless constructor. Plugins are fully trusted: only load DLLs you trust.
/// </summary>
public interface IAvamuxPlugin
{
    /// <summary>A stable id, lower case with dashes ("ai-actions"). Settings and the on/off state are kept under it.</summary>
    string Id { get; }
    string Name { get; }
    /// <summary>One or two sentences for Settings → Plugins.</summary>
    string Description { get; }

    /// <summary>Called when avamux starts (or the plugin is switched on): register what the plugin adds here.</summary>
    void Activate(IPluginContext context);

    /// <summary>Called when the plugin is switched off, reloaded or avamux quits. Registrations are removed by avamux.</summary>
    void Deactivate();
}

/// <summary>What avamux gives a plugin. Every member may be called from any thread unless it says otherwise.</summary>
public interface IPluginContext
{
    /// <summary>The plugin's id.</summary>
    string PluginId { get; }
    /// <summary>avamux's version, e.g. "0.1.0".</summary>
    string Version { get; }
    /// <summary>A folder for the plugin's own files (settings, caches), created for it: &lt;data&gt;/plugin-data/&lt;id&gt;.</summary>
    string DataDirectory { get; }
    /// <summary>avamux's wiki folder.</summary>
    string WikiDirectory { get; }

    // ---- registrations (all taken back when the plugin is switched off) ----

    /// <summary>Adds items to right-click menus (see <see cref="Menus"/> for which ones).</summary>
    void AddMenuItems(IMenuContributor contributor);
    /// <summary>Adds a kind of watch, as a watch plugin would.</summary>
    void AddWatchProvider(IWatchProvider provider);
    /// <summary>Adds a kind of AI provider, which can then be configured under Settings → AI Chat.</summary>
    void AddAiProvider(IAiProvider provider);
    /// <summary>Adds a source of AI workloads (servers, queue jobs) to the AI Workloads page.</summary>
    void AddWorkloadSource(IWorkloadSource source);
    /// <summary>Adds something workload profiles can take a lease from before they start (see <see cref="IWorkloadLeaseProvider"/>).</summary>
    void AddLeaseProvider(IWorkloadLeaseProvider provider);
    /// <summary>Adds a panel of the plugin's own settings, shown under the plugin in Settings → Plugins. The factory runs on the UI thread and returns an Avalonia Control.</summary>
    void AddSettingsView(Func<object> createView);

    // ---- avamux ----

    /// <summary>Runs any avamux command, as <see cref="IAvamuxHost.CommandAsync"/> does.</summary>
    Task<JsonNode?> CommandAsync(string name, JsonObject? args = null);
    /// <summary>Runs a command written as on the command line: <c>RunAsync("wiki", "read", "Home")</c>.</summary>
    Task<JsonNode?> RunAsync(params string[] argv);
    /// <summary>A desktop-style notification inside avamux.</summary>
    void Notify(string title, string message = "");
    /// <summary>A short message in the corner of the window that goes away by itself.</summary>
    void Toast(string title, string message = "");
    /// <summary>Switches the main window to a page ("wiki", "chat" …).</summary>
    void Navigate(string page);
    string? GetSetting(string path);
    void SetSetting(string path, string value);
    /// <summary>Raised on the UI thread after settings changed.</summary>
    event Action? SettingsChanged;
    /// <summary>A colour of the active theme as #rrggbb, by token ("accent", "bg", "panel", "text", "dim" …).</summary>
    string ThemeColor(string token);
    /// <summary>Raised on the UI thread after the theme changed.</summary>
    event Action? ThemeChanged;
    void Log(string message);
    /// <summary>Runs an action on the UI thread.</summary>
    void Post(Action action);
    /// <summary>avamux's own services (an <c>Avamux.Core.AppServices</c>), for plugins that reference Avamux.Core.</summary>
    object Services { get; }

    // ---- windows and questions (call from the UI thread) ----

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string label, string initial = "");
    /// <summary>Asks a yes/no question; true when the action was chosen.</summary>
    Task<bool> ConfirmAsync(string title, string message, string action = "OK");
    /// <summary>Shows an Avalonia control in a window of avamux's (themed, owned by the main window).</summary>
    IPluginWindow ShowWindow(string title, object content, double width = 760, double height = 560);

    /// <summary>Reads text aloud with Kokoro, asking to switch it on first if it is off (call from the UI thread).</summary>
    Task SpeakAsync(string text);

    /// <summary>avamux's AI providers: run a prompt, or open a conversation in AI Chat.</summary>
    IAiService Ai { get; }
}

/// <summary>A window opened with <see cref="IPluginContext.ShowWindow"/>.</summary>
public interface IPluginWindow
{
    string Title { get; set; }
    void Close();
    /// <summary>Raised once the window has closed.</summary>
    event Action? Closed;
}
