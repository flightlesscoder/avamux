using System.Text.Json.Nodes;
using Avamux.Core.Browser;

namespace Avamux.Core.Commands;

public sealed partial class CommandRegistry
{
    private void AddBrowserCommands(AppServices s)
    {
        var surface = O("surface", "Browser pane (b1, b2, …). Default: the one beside the terminal tab you run this in.");
        var after = F("snapshot-after", "Wait for the page to settle, then print a fresh interactive snapshot.");
        var timeout = O("timeout-ms", "How long to wait, in milliseconds (default 30000).");
        var target = P("target", "A ref from snapshot (e5), a CSS selector, text=…, or an XPath.", true);

        Add("browser list", "List the open browser panes.", [], _ => s.Browser.List());
        Add("browser identify", "Which terminal tab this is, and which browser pane commands act on by default.", [surface], a => s.Browser.Identify(a));
        Add("browser open", "Open a browser pane beside this terminal tab (a new tab in its dock), or in a window of its own. Prints the pane's id.",
        [
            P("url", "Address to open (example.com, localhost:3000, a file path, or words to search for)."),
            F("window", "Open in a window of its own instead of beside the terminal."),
            F("focus", "Bring it to the front and give it the keyboard (default: leave the user where they are)."),
            F("keep-alive", "Keep the page running, believing it is visible, while something else is in front."),
            O("profile", "Separate cookies and storage under this name (default: the shared profile)."),
            F("private", "Nothing is kept: cookies and storage go when the pane closes."),
            O("terminal", "Dock it beside this terminal tab instead of the calling one."),
            F("no-wait", "Return at once instead of waiting for the page to load."),
            timeout,
        ], a => s.Browser.OpenAsync(a));
        Add("browser close", "Close a browser pane.", [surface], a => s.Browser.CloseAsync(a));
        Add("browser show", "Bring a browser pane into view (its terminal tab, its dock tab, or its window).", [surface, F("focus", "Also give it the keyboard.")], a => s.Browser.ShowAsync(a));
        Add("browser move", "Move a browser pane into a window of its own, or back into a terminal tab's dock.",
            [P("to", "window or dock.", true), O("terminal", "dock: the terminal tab to dock it beside (default: the one it belongs to)."), surface], a => s.Browser.MoveAsync(a));

        Add("browser goto", "Open an address in the pane and wait for it to load.", [P("url", "The address.", true), F("no-wait", "Return without waiting for the load."), timeout, after, surface], a => s.Browser.GotoAsync(a));
        Add("browser back", "Go back.", [F("no-wait", "Return without waiting."), after, surface], a => s.Browser.GoAsync(a, "back"));
        Add("browser forward", "Go forward.", [F("no-wait", "Return without waiting."), after, surface], a => s.Browser.GoAsync(a, "forward"));
        Add("browser reload", "Reload the page.", [F("hard", "Ignore the cache."), F("no-wait", "Return without waiting."), after, surface], a => s.Browser.GoAsync(a, a.Flag("hard") ? "hardreload" : "reload"));
        Add("browser stop", "Stop loading.", [surface], a => s.Browser.GoAsync(a, "stop"));
        Add("browser url", "The page's address.", [surface], a => Task.FromResult<JsonNode?>(s.Browser.Resolve(a).Url));
        Add("browser title", "The page's title.", [surface], a => Task.FromResult<JsonNode?>(s.Browser.Resolve(a).Title));

        Add("browser snapshot", "The page as an accessibility tree, with refs (e1, e2, …) to act on.",
        [
            F("interactive", "Only what can be clicked or typed into, as a flat list.", 'i'),
            F("compact", "Leave out text and unnamed structure.", 'c'),
            O("max-depth", "How deep to go."),
            O("selector", "Only this part of the page (CSS selector or ref)."),
            F("cursor", "Also count elements with a pointer cursor as clickable."),
            O("max-lines", "Stop after this many lines (default 1500)."),
            surface,
        ], a => s.Browser.SnapshotAsync(a));
        Add("browser click", "Click an element.", [target, O("button", "left, right or middle."), O("count", "Number of clicks."), F("force", "Click even if hidden or disabled."),
            F("ctrl", "Hold Ctrl."), F("shift", "Hold Shift."), F("alt", "Hold Alt."), F("meta", "Hold Meta/Command."), after, surface], a => s.Browser.ClickAsync(a));
        Add("browser dblclick", "Double-click an element.", [target, after, surface], a => s.Browser.ClickAsync(a, 2));
        Add("browser hover", "Move the mouse over an element.", [target, after, surface], a => s.Browser.ActAsync(a, "hovered", "window.__avx.hover(" + BrowserService.Js(a.Require("target")) + ")"));
        Add("browser focus", "Give an element the keyboard focus.", [target, after, surface], a => s.Browser.ActAsync(a, "focused", "window.__avx.focus(" + BrowserService.Js(a.Require("target")) + ")"));
        Add("browser fill", "Set the text of a field (replacing what is there; no text clears it).", [target, Rest("text", "The text."), after, surface], a => s.Browser.FillAsync(a));
        Add("browser type", "Type text key by key into an element (or the focused one).", [P("target", "Where to type; see click."), Rest("text", "The text.", true), F("clear", "Empty the field first."), after, surface], a => s.Browser.TypeAsync(a));
        Add("browser press", "Press keys: Enter, Tab, Escape, ArrowDown, Control+a, \"Tab Tab Enter\" …", [Rest("key", "Key or keys, separated by spaces.", true), O("target", "Focus this element first."), after, surface], a => s.Browser.PressAsync(a));
        Add("browser keydown", "Hold a key down (Shift, Control, … stay held for later commands until keyup).", [P("key", "The key.", true), surface],
            a => s.Browser.ActAsync(a, "keydown", "window.__avx.keydown(" + BrowserService.Js(a.Require("key")) + ")"));
        Add("browser keyup", "Let go of a held key.", [P("key", "The key.", true), surface], a => s.Browser.ActAsync(a, "keyup", "window.__avx.keyup(" + BrowserService.Js(a.Require("key")) + ")"));
        Add("browser select", "Choose options of a <select> (by value or text), or of a custom dropdown.", [target, Rest("values", "Option values or texts.", true), after, surface], a => s.Browser.SelectAsync(a));
        Add("browser check", "Tick a checkbox or pick a radio button.", [target, after, surface], a => s.Browser.CheckAsync(a, true));
        Add("browser uncheck", "Untick a checkbox.", [target, after, surface], a => s.Browser.CheckAsync(a, false));
        Add("browser scroll", "Scroll the page or an element: up, down, left, right, top, bottom.",
            [P("direction", "up, down, left, right, top or bottom."), P("amount", "Pixels (default: most of a screen)."), O("dx", "Pixels across."), O("dy", "Pixels down."), O("selector", "Scroll this element instead of the page."), after, surface],
            a => s.Browser.ScrollAsync(a));
        Add("browser scrollintoview", "Scroll until an element is in view.", [target, surface], a => s.Browser.ActAsync(a, "scrolled", "window.__avx.scrollIntoView(" + BrowserService.Js(a.Require("target")) + ")"));
        Add("browser drag", "Drag one element onto another.", [P("source", "What to drag.", true), P("destination", "Where to drop it.", true), after, surface],
            a => s.Browser.ActAsync(a, "dragged", "window.__avx.drag(" + BrowserService.Js(a.Require("source")) + "," + BrowserService.Js(a.Require("destination")) + ")"));
        Add("browser upload", "Put files into a file input.", [target, Rest("files", "Files to upload.", true), after, surface], a => s.Browser.UploadAsync(a));

        Add("browser get", "Read something from the page: text, html, value, attr, count, box, styles, url or title.",
            [P("what", "text, html, value, attr, count, box, styles, url, title, ref or selector.", true), P("target", "Element (default: the whole page)."),
             O("attr", "attr: the attribute's name."), O("property", "styles: one CSS property."), F("outer", "html: include the element itself."), surface],
            a => s.Browser.GetAsync(a));
        Add("browser is", "Check an element: visible, hidden, enabled, disabled, checked, editable or focused.", [P("what", "What to check.", true), target, surface], a => s.Browser.IsAsync(a));
        Add("browser find", "Find elements by role, text, label, placeholder, alt, title or testid; optionally act on the first match.",
        [
            P("by", "role, text, label, placeholder, alt, title or testid.", true), P("value", "What to look for (a role name for role).", true),
            P("action", "click, dblclick, hover, focus, fill, type, check, uncheck, text or highlight."), Rest("text", "fill/type: the text."),
            O("name", "role: the accessible name to match."), F("exact", "Match the whole text exactly."), O("nth", "Use the nth match (0 is the first, -1 the last)."),
            F("first", "Only the first match."), F("last", "Only the last match."), after, surface,
        ], a => s.Browser.FindAsync(a));
        Add("browser eval", "Run JavaScript in the page and print the result (an expression, or statements with return; promises are awaited).",
            [Rest("script", "The script.", true), timeout, surface], a => s.Browser.EvalAsync(a));
        Add("browser wait", "Wait for an element, text, address, load state or condition (or a number of milliseconds).",
        [
            P("ms", "Just wait this many milliseconds."),
            O("selector", "An element (CSS, ref, text=…)."), O("state", "For --selector: visible (default), hidden, attached, detached or enabled."),
            O("text", "Text that appears on the page."), O("url-contains", "Part of the address."), O("url", "The address, with * and ** wildcards."),
            O("load-state", "load, domcontentloaded or networkidle."), O("function", "A JavaScript condition (may return a promise)."),
            timeout, surface,
        ], a => s.Browser.WaitAsync(a));

        Add("browser screenshot", "Save a PNG of the page. Prints the file's path.", [O("out", "File to write (default: in the data folder)."), F("full", "The whole page, not just what is in view."), surface], a => s.Browser.ScreenshotAsync(a));
        Add("browser pdf", "Save the page as a PDF. Prints the file's path.", [O("out", "File to write (default: in the data folder)."), surface], a => s.Browser.PdfAsync(a));
        CommandParam[] log = [P("action", "list (default) or clear."), O("limit", "How many of the latest (default 50)."), O("filter", "Only entries containing this text."), surface];
        Add("browser console", "Console messages of the pane's pages, oldest first.", [.. log, O("level", "log, info, warn, error or debug.")], a => s.Browser.LogAsync(a, "console"));
        Add("browser errors", "JavaScript errors and failed loads of the pane's pages.", log, a => s.Browser.LogAsync(a, "errors"));
        Add("browser network", "Requests the pane's pages made (fetch, XHR, scripts, images…).", [.. log, F("failed", "Only failures and HTTP errors.")], a => s.Browser.LogAsync(a, "net"));
        Add("browser messages", "Messages posted to the main page with postMessage (from its iframes, cross-origin ones included, or by itself), oldest first.",
            [.. log, O("origin", "Only messages from an origin containing this.")], a => s.Browser.LogAsync(a, "messages"));
        Add("browser highlight", "Outline an element in the page for a moment.", [target, O("ms", "How long (default 2000)."), surface], a => s.Browser.HighlightAsync(a));

        Add("browser cookies", "Cookies of the page's site that scripts can see: get [name], set <name> <value>, clear <name>|--all.",
            [P("action", "get (default), set or clear."), P("name", "Cookie name."), P("value", "set: the value."), O("domain", "set: domain."), O("path", "Path."),
             O("max-age", "set: lifetime in seconds."), F("secure", "set: HTTPS only."), O("same-site", "set: lax, strict or none."), F("all", "clear: every cookie."), surface],
            a => s.Browser.CookiesAsync(a));
        Add("browser storage", "localStorage or sessionStorage: get [key], set <key> <value>, remove <key>, clear.",
            [P("kind", "local or session.", true), P("action", "get (default), set, remove or clear."), P("key", "Key."), P("value", "set: the value."), surface], a => s.Browser.StorageAsync(a));
        Add("browser state", "Save a site's cookies and storage to a file, or load them back (to carry a login to another pane).",
            [P("action", "save or load.", true), P("file", "The file.", true), surface], a => s.Browser.StateAsync(a));
        Add("browser dialog", "The alert, confirm or prompt the page is waiting on: status, accept [text] or dismiss.",
            [P("action", "status (default), accept or dismiss."), Rest("text", "accept: the answer to a prompt."), surface], a => s.Browser.DialogAsync(a));
        Add("browser tab", "The panes in this tab's dock: list, new [url], switch <index|id>, close [index|id].",
            [P("action", "list (default), new, switch or close."), P("target", "new: address; switch/close: index or id."), F("focus", "Bring it to the front."), surface], a => s.Browser.TabAsync(a));
        Add("browser frame", "Work inside an iframe (same site only; for cross-origin ones see addinitscript) from now on, or go back to the main page.", [P("target", "The iframe (selector or ref), or main.", true), surface], a => s.Browser.FrameAsync(a));

        Add("browser addinitscript", "Run a script at the start of every page from the next load on, before the page's own scripts, in every frame (cross-origin iframes too).",
            [Rest("script", "The script."), O("file", "Read the script from this file instead."), F("main-frame", "Only in the main page, not in its frames."), surface],
            a => s.Browser.AddInitScriptAsync(a));
        Add("browser initscripts", "The pane's init scripts: list (default), remove <id> or clear.",
            [P("action", "list, remove or clear."), P("id", "remove: which (i1, i2, …)."), surface], a => s.Browser.InitScriptsAsync(a));
        Add("browser devtools", "The Web Inspector: open, close, toggle, attach (inside the pane, which must be 750 px wide or more), detach (own window) or status.", [P("action", "Default toggle."), surface], a => s.Browser.DevToolsAsync(a));
        Add("browser keep-alive", "Keep the page running and believing it is visible while something else is in front: on, off or status.", [P("action", "on, off or status."), surface], a => s.Browser.KeepAliveAsync(a));
        Add("browser zoom", "Zoom the page: in, out, reset, or a factor such as 1.25.", [P("level", "in, out, reset, a factor, or nothing to show it."), surface], a => s.Browser.ZoomAsync(a));
        Add("browser viewport", "Give the page an exact size in CSS pixels, fitted into the pane (viewport 390 844), or reset.",
            [P("width", "Width, or reset."), P("height", "Height."), surface], a => s.Browser.ViewportAsync(a));
        Add("browser design-mode", "Let the user click an element in the page to copy its details (selector, HTML, styles) for an agent: enable, disable or status.",
            [P("action", "enable, disable or status."), surface], a => s.Browser.DesignModeAsync(a));
        Add("browser skill", "Print the avamux-browser skill for AI agents, or install it for Claude Code (and Codex) with --install.",
            [F("install", "Write it to ~/.claude/skills/avamux-browser (and ~/.codex/skills when Codex is installed)."), O("dir", "install: a skills folder of your choice instead.")],
            a => s.Browser.InstallSkill(a));
    }
}
