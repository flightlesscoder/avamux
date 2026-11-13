using System.Text.Json.Nodes;

namespace Avamux.Plugins;

/// <summary>
/// Implement this in a .NET class library to add a page of your own to avamux's navigation. Add the page under
/// Settings → Custom pages (kind "plugin") and point it at the built DLL; it can be switched on and off, and
/// loaded again after a rebuild, without restarting avamux. The class needs a public parameterless constructor.
///
/// A plugin runs inside avamux with everything avamux can do open to it, so only load DLLs you trust.
/// </summary>
public interface IPagePlugin
{
    /// <summary>
    /// Builds the page. Return an Avalonia <c>Control</c> (reference the same Avalonia version avamux uses, and
    /// do not ship Avalonia with the plugin: avamux supplies it). Called once each time the page is loaded.
    /// If the plugin, or the control it returns, implements <see cref="IDisposable"/> it is disposed when the
    /// page is switched off, removed or reloaded.
    /// </summary>
    object CreateView(IAvamuxHost host);
}

/// <summary>Optional: implement next to <see cref="IPagePlugin"/> to hear when the page comes into view or leaves it.</summary>
public interface IPageVisibility
{
    void OnShown();
    void OnHidden();
}

/// <summary>What avamux offers a page plugin. Every member may be called from any thread.</summary>
public interface IAvamuxHost
{
    /// <summary>The id and name of the page this plugin instance is shown as.</summary>
    string PageId { get; }
    string PageName { get; }
    /// <summary>avamux's version, e.g. "0.1.0".</summary>
    string Version { get; }
    /// <summary>The folder avamux keeps its data in.</summary>
    string DataDirectory { get; }

    /// <summary>
    /// Runs any avamux command, the same ones the command line has ("terminal new", "watch list", "event add" …),
    /// and returns its result. Arguments use the option names shown by <c>avamux &lt;command&gt; /?</c>.
    /// Throws <see cref="InvalidOperationException"/> with avamux's message when the command fails.
    /// </summary>
    Task<JsonNode?> CommandAsync(string name, JsonObject? args = null);

    /// <summary>Runs a command written as on the command line: <c>RunAsync("terminal", "new", "htop", "--name", "top")</c>.</summary>
    Task<JsonNode?> RunAsync(params string[] argv);

    /// <summary>Every command with its help and parameters.</summary>
    JsonArray Commands();

    /// <summary>Shows a notification inside avamux.</summary>
    void Notify(string title, string message = "");

    /// <summary>Switches the window to a page: a built-in one ("terminals", "wiki" …) or a custom page's name.</summary>
    void Navigate(string page);

    /// <summary>Marks this page in the navigation as wanting the user, with a notification unless it is the page in view.</summary>
    void RequestAttention(string? message = null);
    void ClearAttention();
    /// <summary>A few characters shown on this page's navigation entry. Null or empty removes them.</summary>
    void SetBadge(string? text);

    /// <summary>A setting's effective value as JSON text's plain form ("true", "13", "nvim"), or null when unknown.</summary>
    string? GetSetting(string path);
    /// <summary>Changes a setting in the home configuration file. Throws when the setting or value is not valid.</summary>
    void SetSetting(string path, string value);
    /// <summary>Raised on the UI thread after settings changed.</summary>
    event Action? SettingsChanged;

    /// <summary>A colour of the active theme as #rrggbb, by token: "accent", "bg", "panel", "text", "dim", "ok", "warn", "error" …</summary>
    string ThemeColor(string token);
    /// <summary>Raised on the UI thread after the theme changed.</summary>
    event Action? ThemeChanged;

    /// <summary>Writes a line to avamux's log (see the Logs page).</summary>
    void Log(string message);

    /// <summary>Runs an action on the UI thread.</summary>
    void Post(Action action);

    /// <summary>
    /// avamux's own services (an <c>Avamux.Core.AppServices</c>): terminals, watches, events, the wiki, repositories,
    /// configuration and the rest, for plugins that reference Avamux.Core and want more than commands.
    /// </summary>
    object Services { get; }
}
