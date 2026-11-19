using Avamux.Core.Config;

namespace Avamux.Views;

/// <summary>
/// Every key and key combination avamux itself handles, grouped by where it works. Settings → Hotkeys shows this list,
/// so a key added to (or taken from) a handler belongs here too: MainWindow (boss key), term/terminal.js (terminals),
/// Browser/agent.js and BrowserSurface (browser panes), wiki/preview.js (wiki pictures) and the pages' KeyDown handlers.
/// </summary>
public static class Hotkeys
{
    /// <summary>One shortcut: the keys (any one of them does it) and what it does.</summary>
    public sealed record Entry(string[] Keys, string Action);

    /// <summary>Shortcuts that work in one place.</summary>
    public sealed record Group(string Where, string? Note, Entry[] Entries);

    private static Entry E(string action, params string[] keys) => new(keys, action);

    /// <summary>The list, with the parts that come from settings (boss key, terminal links) filled in from them.</summary>
    public static IReadOnlyList<Group> All(AppSettings settings)
    {
        var mac = OperatingSystem.IsMacOS();
        string[] Cmd(params string[] keys) => mac ? keys : [];
        var boss = settings.Ui.BossKey?.Trim() ?? "";
        var linksInPane = settings.Browser.TerminalLinksInPane;

        return
        [
            new("Everywhere", null,
            [
                boss.Length > 0
                    ? E("Boss key: swap the window for the boss screen and back (Settings → Window)", boss)
                    : E("Boss key: switched off; set one under Settings → Window", "—"),
                E("Leave the boss screen", "Esc"),
            ]),
            new("Terminals", "Every other key goes to the program running in the tab.",
            [
                E("New terminal tab", "Ctrl+Shift+T"),
                E("Close the current tab", "Ctrl+Shift+W"),
                E("Previous tab", "Ctrl+PageUp"),
                E("Next tab", "Ctrl+PageDown"),
                E("Copy the selection", ["Ctrl+Shift+C", "Ctrl+Insert", .. Cmd("⌘C")]),
                E("Paste", ["Ctrl+Shift+V", "Shift+Insert", .. Cmd("⌘V")]),
                E("Open the browser beside this tab, or show / hide it", "Ctrl+Shift+L"),
                linksInPane
                    ? E("Open a link in the system browser (a plain click opens it beside the tab)", "Ctrl+Click")
                    : E("Open a link in the browser beside the tab (a plain click opens the system browser)", "Ctrl+Click"),
                E("Renaming a tab (double-click it): keep the name", "Enter"),
                E("Renaming a tab: cancel", "Esc"),
            ]),
            new("Browser panes", mac ? "While the page has the keyboard. ⌘ works wherever Ctrl is shown." : "While the page has the keyboard.",
            [
                E("Go to the address bar", "Ctrl+L"),
                E("Address bar: go there", "Enter"),
                E("Address bar: put the address back and return to the page", "Esc"),
                E("Reload", "F5", "Ctrl+R"),
                E("Reload, ignoring the cache", "Shift+F5", "Ctrl+Shift+R", "Ctrl+F5"),
                E("Back", "Alt+Left", "Mouse back button"),
                E("Forward", "Alt+Right", "Mouse forward button"),
                E("Zoom in", "Ctrl+=", "Ctrl++"),
                E("Zoom out", "Ctrl+-"),
                E("Actual size", "Ctrl+0"),
                E("Design mode: pick an element on the page", "Ctrl+Shift+C"),
                E("Stop picking", "Esc"),
                E("Web Inspector (when Developer tools is on)", "F12", "Ctrl+Shift+I"),
                E("Hide the browser and go back to its terminal tab", "Ctrl+Shift+L"),
                E("A page's prompt dialog: OK", "Enter"),
            ]),
            new("Wiki", null,
            [
                E("Save", ["Ctrl+S", .. Cmd("⌘S")]),
                E("Paste; a picture or a copied file is saved next to the page and linked", ["Ctrl+V", "Shift+Insert", .. Cmd("⌘V")]),
                E("Cut", ["Ctrl+X", .. Cmd("⌘X")]),
                E("Copy", ["Ctrl+C", .. Cmd("⌘C")]),
                E("Select all", ["Ctrl+A", .. Cmd("⌘A")]),
                E("Undo", "Ctrl+Z"),
                E("Redo", "Ctrl+Y"),
                E("Undo, redo, cut, copy, paste, delete, select all (editor menu)", "Right-click"),
                E("Back through visited pages", "Alt+Left", "Mouse back button"),
                E("Forward through visited pages", "Alt+Right", "Mouse forward button"),
            ]),
            new("Wiki pictures", "In the preview, while a picture is shown in its viewer or enlarged.",
            [
                E("Zoom in", "+", "="),
                E("Zoom out", "-"),
                E("Fit", "0"),
                E("Actual size", "1"),
                E("Move around", "← → ↑ ↓"),
                E("Close the enlarged picture or the menu", "Esc"),
            ]),
            new("Search", null,
            [
                E("Search", "Enter"),
                E("Clear the search", "Esc"),
                E("From the search box to the results", "↓"),
                E("Open the selected result", "Enter"),
            ]),
            new("C# Interactive", null,
            [
                E("Run (or a new line, while the statement is unfinished)", "Enter"),
                E("Always run", "Ctrl+Enter"),
                E("Always a new line", "Shift+Enter"),
                E("Earlier line from the history (on the prompt's first line)", "↑"),
                E("Later line from the history (on the prompt's last line)", "↓"),
                E("Empty the prompt", "Esc"),
                E("Empty the terminal", "Ctrl+L"),
                E("XAML tab: apply", "Ctrl+Enter"),
            ]),
            new("Repositories", "Resolving a merge conflict.",
            [
                E("Stop editing the result", "Esc"),
            ]),
            new("Disk map", "The window opened from System.",
            [
                E("Open the selected folder or file", "Enter"),
                E("Up one folder", "Backspace"),
                E("Back", "Alt+Left", "Mouse back button"),
                E("Forward", "Alt+Right", "Mouse forward button"),
                E("Scan again", "F5"),
            ]),
            new("Theme editor", null,
            [
                E("Save the theme", "Ctrl+S"),
                E("Name, font or colour field: apply", "Enter"),
                E("Colour field: put it back", "Esc"),
            ]),
            new("Settings", null,
            [
                E("Clear the settings search", "Esc"),
                E("Text setting: save it", "Enter"),
                E("Text setting: put it back", "Esc"),
            ]),
            new("Other pages", null,
            [
                E("Caffeine: start the custom time", "Enter"),
                E("Dashboard: add the source", "Enter"),
                E("Watching: save the link", "Enter"),
            ]),
        ];
    }

    /// <summary>Whether a shortcut matches the settings search: its keys, what it does, or where it works.</summary>
    public static bool Matches(Group group, Entry entry, string query) =>
        entry.Action.Contains(query, StringComparison.OrdinalIgnoreCase)
        || group.Where.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Keys.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))
        || query.Length >= 3 && "hotkeys shortcuts keyboard".Contains(query, StringComparison.OrdinalIgnoreCase);
}
