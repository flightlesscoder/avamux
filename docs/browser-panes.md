# Browser panes

Each terminal tab can have a browser beside it, for you and for the AI agent working in that terminal. The agent
drives its own browser with `avamux browser …` while you watch, and either of you can open the Web Inspector on the
same page.

It is a real web view: WebKitGTK on Linux, WKWebView on macOS.

## Opening and arranging

- The globe button at the end of the tab strip, or Ctrl+Shift+L (in the terminal or in the page), opens a pane, then
  hides and shows it again.
- Drag the line between the terminal and the browser to make either wider. avamux remembers the width.
- A split tab has one browser, shared by its panes.
- A pane can move into a window of its own and back (the ⋮ menu, or `avamux browser move window|dock`). Closing that
  window puts it back beside its tab.
- Closing a terminal tab closes the panes docked beside it.
- Links a page opens in a new window become new tabs in the same dock.

Panes are not restored when avamux restarts, as terminals are not.

## In the pane

- Tabs of its own, back, forward, reload, and zoom (Ctrl+plus, Ctrl+minus, Ctrl+0).
- An address bar. Text that is not an address is searched for with `browser.searchUrl` (DuckDuckGo to begin with).
- The page's own alert, confirm and prompt dialogs, shown as a bar above it.
- The Web Inspector: F12, Ctrl+Shift+I, or the bug button, which gets a red dot when the page has thrown errors. The
  inspector docks inside the pane when the pane is at least 750 px wide, and opens in a window of its own otherwise.
  `browser.developerTools` switches it off.

The full list of keys is in [Keyboard shortcuts](keyboard-shortcuts.md#browser-panes).

### Keep alive

The pin button keeps a pane's page running while you look at another tab or page. It stays loaded at its size, keeps
its timers and animations going, and always reports itself visible and focused (`document.visibilityState`, `blur`
and `visibilitychange` included), so a running app or game is where you left it. `browser.keepAliveByDefault` turns it
on for new panes.

On Linux, WebKitGTK keeps panes running when they are out of view anyway; there keep alive also pins what the page is
told. WKWebView on macOS slows hidden pages down; keep alive is meant to prevent that there, but has not been tried on a
Mac yet.

### Design mode

The hand button (Ctrl+Shift+C in the page) lets you click an element. Its selector, ref, text, box, styles and HTML go
to the clipboard, ready to paste to an agent: "make this one wider" without describing which one. Esc stops picking.

## For agents

Every terminal gets `AVAMUX_TERMINAL_ID`, and `avamux browser …` run in a tab acts on the pane beside that tab.
`avamux browser identify` says which tab and pane that is. The commands follow
[agent-browser](https://github.com/vercel-labs/agent-browser) and cmux, so agents that know those know these.

```sh
avamux browser open localhost:3000            # beside this tab; waits for the load; prints the pane id (b1)
avamux browser snapshot -i                    # what can be clicked or typed into, with refs: - button "Save" [ref=e7]
avamux browser fill e3 "ada@example.com"
avamux browser click e7 --snapshot-after      # act, then see the page again
avamux browser wait --text "Saved"            # or --selector, --url-contains, --load-state networkidle, --function
avamux browser find role button --name "Delete" click
avamux browser errors && avamux browser network --failed
avamux browser dialog accept                  # the confirm() the click opened
avamux browser eval 'document.title'
avamux browser screenshot --full --out page.png
avamux browser state save login.json          # cookies and storage, to load into another pane
```

The rest:

| Group | Commands |
|---|---|
| Moving around | `goto`, `back`, `forward`, `reload [--hard]`, `tab list\|new\|switch\|close`, `frame` |
| Reading | `snapshot`, `wait`, `get text\|html\|value\|attr\|count\|box\|styles\|url\|title`, `is visible\|hidden\|enabled\|disabled\|checked\|editable\|focused`, `console`, `errors`, `network`, `messages` |
| Acting | `click`, `dblclick`, `hover`, `focus`, `type`, `fill`, `press "Control+a Backspace"`, `keydown` / `keyup`, `select`, `check` / `uncheck`, `scroll`, `scrollintoview`, `drag`, `upload`, `highlight` |
| State | `cookies`, `storage`, `state save\|load` |
| Output | `screenshot`, `pdf` |
| The pane | `zoom`, `viewport 390 844`, `devtools`, `keep-alive`, `design-mode`, `show`, `move`, `close`, `list` |

Each takes `--surface b2` to name another pane. `avamux browser /?` lists them all, and `avamux browser <command> /?`
explains one.

Errors start with a code an agent can act on:

| Code | Meaning |
|---|---|
| `not_found` | A stale ref, or no such element: snapshot again. |
| `invalid_state` | The page cannot do that now: a dialog is waiting for an answer, the page is still loading, no dialog is open, nothing to go back to. The message says what to run next. |
| `timeout` | A wait ran out. |
| `js_error` | The page's JavaScript threw. |
| `not_supported` | The system web view cannot do this (see below). |

### Cross-origin iframes

`frame` only reaches iframes of the same site. For a cross-origin iframe, `addinitscript` (as in cmux) adds a script
that runs at the start of every page from the next load on, before the page's own scripts, in every frame,
cross-origin ones included (`--main-frame` for the main page only). The script can `postMessage` what it finds to the
main page, and `avamux browser messages` lists what the main page received (origin, which frame, data).
`initscripts` lists the scripts; `initscripts remove i1` and `initscripts clear` take them away.

### Teaching agents about it

`avamux browser skill --install` puts an `avamux-browser` skill for Claude Code (and Codex, when installed) in
`~/.claude/skills`, so agents know all this without being told. `avamux browser skill` prints it.

## Links from terminals

- With `browser.setBrowserVariable`, new terminals get `BROWSER` pointing at avamux, so a program that opens a web
  page (a CLI login, a dev server's `--open`) opens it in the tab's pane.
- Ctrl+click on a link in a terminal opens it in the pane; a plain click opens the system browser.
  `browser.terminalLinksInPane` swaps the two.
- `browser.externalSites` lists sites that always go to the system browser: host names or `*.example.com`, separated
  by commas.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `browser.agentControl` | true | Let `avamux browser` commands (and the agents in terminals) drive panes. |
| `browser.homePage` | `about:blank` | What a new pane opens when no address is given. |
| `browser.searchUrl` | `https://duckduckgo.com/?q={query}` | Where address-bar text is searched for. |
| `browser.keepAliveByDefault` | false | New panes keep running while out of view. |
| `browser.terminalLinksInPane` | false | A plain click on a terminal link opens it in the pane. |
| `browser.externalSites` | empty | Sites that always open in the system browser. |
| `browser.developerTools` | true | The Web Inspector, for panes opened afterwards. |
| `browser.setBrowserVariable` | false | Set `BROWSER` in new terminals. |

Pages' data (cookies, storage, cache) and screenshots are kept under `browser/` in the data folder.

## What the system web view cannot do

As in cmux, a system web view cannot intercept or mock network requests, emulate being offline or a location,
record traces, or report response bodies and HttpOnly cookies.

| | Linux (WebKitGTK) | macOS (WKWebView) |
|---|---|---|
| Exact viewport | Yes | No |
| Inspector docked in the pane | Yes | No (own window) |
| Page dialogs as a bar above the page | Yes | No |
| Screenshots | Yes | Yes, not yet tried; `--full` captures at most 16,384 points, and content the GPU draws (WebGL, video) can come out blank |
| Init scripts and console from the very start of every page | Yes | Yes |
