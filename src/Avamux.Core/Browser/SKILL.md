---
name: avamux-browser
description: "Drive a real browser pane beside your avamux terminal tab: open your dev server, snapshot the page with element refs, click, fill, wait, read the console and network, take screenshots. Use when running inside avamux (AVAMUX_TERMINAL_ID is set) and you need to look at or test a web page."
---

# Browser panes in avamux

avamux can dock a browser pane beside the terminal tab you are running in. The user sees
the same page you drive, and can open the Web Inspector on it. Everything goes through the
`avamux browser` command.

## Your pane

Commands without `--surface` act on the pane beside *your* tab (avamux knows your tab from
`AVAMUX_TERMINAL_ID`). Run `avamux browser identify` to see it, or `avamux browser list` for every pane.
To act on another pane, pass `--surface b3` (ids look like `b1`, `b2`, …).

Opening a pane does not take the keyboard from the user. Add `--focus` only if they asked to see it.

## Core loop

```bash
avamux browser open http://localhost:3000          # opens beside your tab, waits for the load, prints the id
avamux browser snapshot --interactive              # buttons, links, fields… each with a ref like e12
avamux browser fill e3 "ada@example.com"
avamux browser click e7 --snapshot-after           # act, then get a fresh snapshot back
avamux browser wait --text "Welcome"               # or --selector, --url-contains, --load-state networkidle
avamux browser errors                              # JavaScript errors since the page started
```

Refs (`e12`) come from `snapshot` and stay valid while the element stays on the page. After a
navigation, a dialog or a big re-render, take a new snapshot. A target can also be a CSS selector
(`"#email"`, `"button[type=submit]"`), `text=Sign in`, or an XPath (`//button[1]`).

`snapshot` without `--interactive` gives the whole accessibility tree (headings, text, lists),
useful to read a page. Narrow it with `--selector "main"`, `--compact` or `--max-depth 4`.

## Commands

Navigation: `goto <url>`, `back`, `forward`, `reload [--hard]`, `get url`, `get title`.
`open` makes a new pane (a new tab in your dock when you already have one); `goto` reuses the pane.

Reading: `get text|html|value|attr|count|box|styles [target]` (`--attr name`, `--property color`),
`is visible|enabled|checked|editable|focused <target>`, `eval '<js>'` (an expression, or statements
with `return`; promises are awaited).

Finding without a snapshot: `find role button --name "Save"`, `find text "Sign in" click`,
`find label "Email" fill "ada@example.com"`, `find placeholder|alt|title|testid <value> [action] [text]`.

Acting: `click|dblclick|hover|focus <target>`, `fill <target> [text]` (empty clears), `type <target> <text>`,
`press Enter` / `press Control+a` / `press "Tab Tab Enter"`, `keydown Shift` … `keyup Shift`,
`select <target> <value...>`, `check|uncheck <target>`, `scroll down [px]` / `scroll --selector .list --dy 400`,
`scrollintoview <target>`, `drag <from> <to>`, `upload <target> <files...>`.
Mutating commands take `--snapshot-after`.

Waiting: `wait --selector <css> [--state visible|hidden|attached|detached]`, `wait --text <text>`,
`wait --url-contains /dashboard`, `wait --url "**/done"`, `wait --load-state load|networkidle`,
`wait --function "window.appReady === true"`, `wait 500` (ms). `--timeout-ms` (default 30000).

Debugging: `console [--level error] [--limit 20]`, `errors`, `network [--filter api] [--failed]`,
`messages [--origin x]` (each with `clear`), `highlight <target>`, `screenshot [--full] [--out file.png]`, `pdf [--out file]`.
`devtools open` shows the Web Inspector to the user.

State: `cookies [get|set|clear]`, `storage local|session [get|set|remove|clear] [key] [value]`,
`state save <file>` / `state load <file>` (cookies visible to scripts plus local and session storage; HttpOnly cookies are not included).

Dialogs: an `alert`/`confirm`/`prompt` blocks the page; other commands then fail with
`invalid_state`. Answer it with `dialog accept [text]` or `dialog dismiss`; `dialog status` shows it.

Tabs and frames: `tab list|new [url]|switch <index|id>|close`, `frame <iframe selector>` / `frame main`.
Same-site iframes are also included in snapshots directly.

## Cross-origin iframes and scripts before the page

`frame` and the other commands cannot reach into an iframe from another site. An init script can: it runs at
the start of every page the pane loads, before the page's own scripts, in every frame, cross-origin ones included.
Have it send what it finds to the main page with `postMessage`, then read it with `messages`:

```bash
avamux browser addinitscript --file probe.js     # or: addinitscript 'window.__ready = false'
avamux browser reload                            # init scripts start with the next load
avamux browser messages --origin payments.example
```

```js
// probe.js: runs in the main page and in every frame
if (window !== window.top && location.hostname.endsWith('payments.example')) {
  addEventListener('load', () => parent.postMessage({ probe: 'card-form', fields: document.querySelectorAll('input').length }, '*'));
}
```

`messages` lists every postMessage the main page received (`origin`, `from` frame/self, the frame's
`#id` or address, `data`), kept across loads; `messages clear` empties it. A frame's `console.log` also
shows up in `console`. `--main-frame` limits a script to the main page. `initscripts` lists them,
`initscripts remove i1` / `initscripts clear` take them away (pages already loaded keep what they ran).
Init scripts need WebKitGTK (Linux) or WKWebView (macOS); elsewhere `addinitscript` answers `not_supported`.

Viewport: `viewport 390 844` (CSS pixels, fitted into the pane), `viewport reset`, `zoom in|out|reset|1.5`.

Panes: `show [--focus]`, `move window|dock`, `close`, `keep-alive on|off` (the page keeps running
and believes it is visible while the user looks elsewhere), `design-mode enable` (the user clicks an
element and its details go to their clipboard to paste to you).

## Errors

Errors start with a code: `not_found` (stale ref or no match — snapshot again), `invalid_state`
(hidden, disabled, or a dialog is open), `timeout`, `invalid_params`, `js_error`, `not_supported`.

## Good habits

- Prefer refs from a fresh `snapshot --interactive` over guessing selectors.
- Use `--snapshot-after` on clicks that change the page instead of a separate snapshot call.
- Check `errors` and `network --failed` when something does not work.
- Do not print secrets: `fill` works with values from environment variables.
- Close panes you opened for a one-off check (`avamux browser close`), unless the user wants to look.
