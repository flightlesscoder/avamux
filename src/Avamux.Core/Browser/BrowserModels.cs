using System.Text.Json.Nodes;

namespace Avamux.Core.Browser;

/// <summary>One browser pane as the commands see it.</summary>
/// <param name="Id">Short handle, b1, b2, … never reused while avamux runs.</param>
/// <param name="Terminal">The terminal tab the pane belongs to (its dock), or null when it has none.</param>
/// <param name="Placement">"dock" (beside its terminal tab) or "window" (a window of its own).</param>
/// <param name="Active">The pane shown in its tab's dock (the other panes of that tab are behind it).</param>
/// <param name="OnScreen">Whether it can be seen right now.</param>
/// <param name="Loads">How many page loads have finished; used to wait for the next one.</param>
public sealed record BrowserSurfaceInfo(
    string Id, string Url, string Title, bool Loading, string? Terminal, string Placement, bool Active, bool OnScreen,
    bool KeepAlive, string? Profile, double Zoom, int Loads, BrowserDialogInfo? Dialog, bool DevTools, bool DesignMode, string? Viewport);

/// <summary>An alert, confirm, prompt or "leave this page?" question the page is waiting on.</summary>
public sealed record BrowserDialogInfo(string Type, string Message, string? DefaultText);

/// <summary>A script the pane puts at the start of every page it loads, before the page's own scripts run.</summary>
/// <param name="Id">Short handle within the pane: i1, i2, …</param>
/// <param name="MainFrameOnly">Only in the main page; otherwise in every frame too, cross-origin iframes included.</param>
public sealed record BrowserInitScript(string Id, string Source, bool MainFrameOnly);

public sealed record BrowserOpenOptions(string? Url, string? Terminal, bool Window, bool Focus, bool? KeepAlive, string? Profile, bool Private);

/// <summary>A command failed in a way the agent can act on (not_found, invalid_state, timeout, not_supported, …).</summary>
public class BrowserException(string code, string message) : InvalidOperationException(code + ": " + message)
{
    public string Code { get; } = code;
}

/// <summary>The page opened an alert, confirm or prompt while a command ran (and stopped there until it is answered).</summary>
public sealed class BrowserDialogOpened(BrowserDialogInfo dialog)
    : BrowserException("dialog_opened", $"The page opened a {dialog.Type}: \"{dialog.Message}\". Answer it with `avamux browser dialog accept [text]` or `dismiss`.")
{
    public BrowserDialogInfo Dialog { get; } = dialog;
    public string Describe() => $"the page opened a {Dialog.Type}: \"{Dialog.Message}\" (answer it with `avamux browser dialog accept{(Dialog.Type == "prompt" ? " [text]" : "")}` or `dismiss`)";
}

/// <summary>What the browser commands need from the UI: the panes themselves. The UI implements it.</summary>
public interface IBrowserSurfaces
{
    IReadOnlyList<BrowserSurfaceInfo> List();
    Task<BrowserSurfaceInfo> OpenAsync(BrowserOpenOptions options);
    Task CloseAsync(string id);
    /// <summary>Brings a pane into view: selects its terminal tab and dock tab, or raises its window.</summary>
    Task ShowAsync(string id, bool focus);
    /// <summary>Moves a pane to "dock" (of <paramref name="terminal"/>, or its own tab) or to "window".</summary>
    Task MoveAsync(string id, string placement, string? terminal);
    /// <summary>Runs JavaScript in the page's main frame and returns what it evaluated to, as a string.</summary>
    Task<string?> EvalAsync(string id, string script);
    Task NavigateAsync(string id, string url);
    /// <summary>back, forward, reload, hardreload or stop.</summary>
    Task GoAsync(string id, string action);
    /// <summary>Waits until more than <paramref name="loads"/> page loads have finished. False on timeout.</summary>
    Task<bool> WaitForLoadAsync(string id, int loads, TimeSpan timeout);
    /// <summary>Writes a PNG of the visible page (or the whole document) to <paramref name="file"/>.</summary>
    Task ScreenshotAsync(string id, string file, bool fullPage);
    Task PdfAsync(string id, string file);
    /// <summary>open, close, toggle, attach, detach or status. Returns "open" or "closed".</summary>
    Task<string> DevToolsAsync(string id, string action);
    Task SetKeepAliveAsync(string id, bool on);
    /// <summary>in, out, reset, or a factor such as 1.25. Returns the zoom afterwards.</summary>
    Task<double> ZoomAsync(string id, string action);
    /// <summary>Gives the page an exact viewport size in CSS pixels (fitted into the pane), or the pane's own size with nulls.</summary>
    Task<string?> SetViewportAsync(string id, int? width, int? height);
    Task<bool> DesignModeAsync(string id, bool? on);
    /// <summary>Answers the page's open dialog (accept or dismiss, with text for a prompt).</summary>
    Task<bool> AnswerDialogAsync(string id, bool accept, string? text);
    /// <summary>Replaces the pane's init scripts; they run at document start from the next page load on.</summary>
    Task SetInitScriptsAsync(string id, IReadOnlyList<BrowserInitScript> scripts);
}

/// <summary>Used where there is no window (tests, headless runs).</summary>
public sealed class NullBrowserSurfaces : IBrowserSurfaces
{
    private static Exception None() => new BrowserException("not_supported", "There is no window to show a browser in.");
    public IReadOnlyList<BrowserSurfaceInfo> List() => [];
    public Task<BrowserSurfaceInfo> OpenAsync(BrowserOpenOptions options) => throw None();
    public Task CloseAsync(string id) => throw None();
    public Task ShowAsync(string id, bool focus) => throw None();
    public Task MoveAsync(string id, string placement, string? terminal) => throw None();
    public Task<string?> EvalAsync(string id, string script) => throw None();
    public Task NavigateAsync(string id, string url) => throw None();
    public Task GoAsync(string id, string action) => throw None();
    public Task<bool> WaitForLoadAsync(string id, int loads, TimeSpan timeout) => throw None();
    public Task ScreenshotAsync(string id, string file, bool fullPage) => throw None();
    public Task PdfAsync(string id, string file) => throw None();
    public Task<string> DevToolsAsync(string id, string action) => throw None();
    public Task SetKeepAliveAsync(string id, bool on) => throw None();
    public Task<double> ZoomAsync(string id, string action) => throw None();
    public Task<string?> SetViewportAsync(string id, int? width, int? height) => throw None();
    public Task<bool> DesignModeAsync(string id, bool? on) => throw None();
    public Task<bool> AnswerDialogAsync(string id, bool accept, string? text) => throw None();
    public Task SetInitScriptsAsync(string id, IReadOnlyList<BrowserInitScript> scripts) => throw None();
}

/// <summary>Console messages, errors, requests and postMessage messages a pane's pages reported, kept across navigations.</summary>
public sealed class BrowserLog
{
    public const int Limit = 2000;
    private readonly object _gate = new();
    private readonly List<JsonObject> _console = [], _errors = [], _net = [], _messages = [];

    public void Add(string kind, JsonObject record)
    {
        var list = kind switch { "console" => _console, "errors" => _errors, "net" => _net, "messages" => _messages, _ => null };
        if (list is null) return;
        lock (_gate)
        {
            list.Add(record);
            if (list.Count > Limit) list.RemoveRange(0, list.Count - Limit);
        }
    }

    public List<JsonObject> Get(string kind)
    {
        lock (_gate) return (kind switch { "console" => _console, "errors" => _errors, "messages" => _messages, _ => _net }).Select(x => (JsonObject)x.DeepClone()).ToList();
    }

    public int Clear(string kind)
    {
        lock (_gate)
        {
            var list = kind switch { "console" => _console, "errors" => _errors, "messages" => _messages, _ => _net };
            var n = list.Count;
            list.Clear();
            return n;
        }
    }

    public (int Console, int Errors, int Requests) Counts { get { lock (_gate) return (_console.Count, _errors.Count, _net.Count); } }
}
