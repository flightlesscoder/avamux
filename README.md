# avamux

Terminals, watches, events, a dashboard, logs, a wiki and git repositories in one Avalonia window, with a CLI
that can do everything the window can. Runs on Linux and macOS.

## Build and run

```sh
dotnet build
dotnet run --project src/Avamux          # start the app (a second start just brings the first to the front)
dotnet test                              # core test suite
```

The built executable is `src/Avamux/bin/Debug/net10.0/avamux`; put it (or a `dotnet publish` output) on your
`PATH` to use the CLI as `avamux`.

Linux needs WebKitGTK 4.1 (`libwebkit2gtk-4.1`). macOS uses the system WKWebView.

## Pages

| Page | What it does |
|---|---|
| Terminals | Tabbed terminals (xterm.js on a real pty). Tabs show what is running, or a name you give them (double-click), and a marker for what the terminal is doing: idle, running, an AI agent working, an AI agent waiting for input, or ended. Tabs can be moved to their own window and back (right-click, or the button at the end of the tab strip). Ctrl+Shift+C / V copy and paste, Ctrl+Shift+T / W new and close tab, Ctrl+PageUp / PageDown switch tabs. Every other key combination goes to the program. |
| Search | One box over wiki pages, events, watches, terminals, repositories, logs and settings. Pick which sources to include; click a result to open it where it lives. |
| Today | Opens today's daily note in the wiki, creating it if needed. The name comes from `wiki.dailyNoteFormat` (default `yyyy-MM-dd dddd`, e.g. `2026-11-12 Thursday.md`) and `wiki.dailyNoteFolder` (default `Daily`). |
| Settings | Every setting, which file it comes from, and which file to save changes to. Includes themes, the navigation bar style and the AI Workloads switches. |
| Watching | Periodic checks with a status, summary, clickable/editable link and item list. |
| Events | Timers, scheduled tasks and calendar events (multi-day, weekdays, weekly, monthly, yearly, fixed interval) that can run a script on start and on end. Events can have a location, file attachments, a colour and a markdown description. Double-click an event on the calendar to edit it, or an empty spot to add one. |
| Dashboard | Your own HTML/CSS/JS from the dashboard folder, with developer tools, an Edit button that opens the folder in your editor, togglable extra CSS/JS sources, and `window.avamux` to control the app. |
| Logs | App logs, crash reports, history of ended terminals, script output; retention by size (200 MB), age and count. |
| Wiki | Markdown editor and live preview with folders, links (`[[Page]]`), front matter, syntax-highlighted code blocks and pictures. Paste a picture straight into a page: it is saved next to the page and linked. Click a picture to zoom and pan; right-click it for Show in Folder, Copy and Copy Path. Also previews images, PDFs, audio, video and text files, and opens anything else in its default app. Entries can be opened in their own window. The last scroll position of recent files is remembered (`ui-state.json` in the data folder). |
| Repos | Changes, history, branches and pull requests for one configured git repository or all of them. Stage or unstage files, hunks (right-click the coloured strip) or single lines (right-click a line number); diff or whole-file view; switch branches; inspect a commit file by file; cherry-pick, revert, reset, tag. |
| AI Chat, Workloads | Placeholders, shown when switched on under Settings → AI Workloads. |

## Themes

Settings → Themes picks a theme, an accent colour and the window chrome (`system` title bar, or `custom`, drawn in
the theme's colours). "Simple" is the original look; "90's Desktop Computer OS Classic" is grey bevels and a navy
title bar; the others use palettes from the kitty themes in terminal-stuff. Terminals follow the theme unless
`terminal.background` / `terminal.foreground` are set.

Themes also have sounds, played when an event starts or ends, a timer goes off, a watched item or pull request
changes, or an AI agent in a terminal starts waiting for input. Each can be switched off, and files named
`event-start`, `event-end`, `timer`, `watch-changed`, `pull-request` or `agent-waiting` (.wav, .ogg, .flac, .mp3)
in the data folder's `sounds/custom/` replace the built-in ones. Playback uses `pw-play`, `paplay` or `aplay` on
Linux and `afplay` on macOS.

## Pull requests are watches

The Repositories page's Pull Requests tab shows the result of a watch of kind `repo-pr` for that repository, so the
list refreshes on the watch's interval, notifies on change and can be managed on the Watching page. By default it
runs `gh pr list`; set `repositories.pullRequestCommand` (or the watch's own `command`) to any command that prints
a JSON array of pull requests (`number`, `title`, `state`, `isDraft`, `author`, `url`, `headRefName`,
`baseRefName`, `updatedAt`, and optionally `mine` and `active`) or the usual watch result object.

## Configuration

Settings are JSON, merged in this order (later wins):

1. built-in defaults
2. `~/avamux.json`
3. `~/avamux.local.json`
4. `./avamux.json` (the folder avamux was started in)
5. `./avamux.local.json`

A file only needs the keys it changes:

```json
{ "terminal": { "scrollback": 10000, "fontSize": 14 }, "watching": { "defaultIntervalMinutes": 5 } }
```

`avamux settings list` shows every key, its value and its source. `avamux app paths` shows where data lives
(`~/.local/share/avamux` on Linux, `~/Library/Application Support/avamux` on macOS).

## CLI

```sh
avamux /?                      # also -?, --?, -h, --help, /help, help; works per group and per command
avamux terminal new --name build -- dotnet watch
avamux terminal send build "^C" --keys
avamux settings set terminal.scrollback 20000 --scope home-local
avamux event timer 25m Focus --run "notify-send done"
avamux event add Standup --start "tomorrow 9:30" --duration 15m --repeat weekly --days mon,tue,fri
avamux watch add "My PRs" --kind github-pr --set repo=owner/name --set author=me
avamux repo add . && avamux repo status
avamux repo prs avamux --mine
avamux search "release notes" --source wiki --source events
avamux wiki today
avamux theme set classic90s
```

Add `--json` to any command for machine-readable output. The same commands are available to dashboard
JavaScript: `await avamux.command('terminal new', { name: 'build' })`.

## Watches are pluggable

Built-in kinds: `github-pr`, `ado-pr`, `ado-pbi`, `ado-pipeline`, `repo-pr`, `command`, `plugin` (`avamux watch kinds`).
The GitHub and Azure DevOps kinds follow the public REST APIs but have not been run against live accounts yet.

- **Script** (`command`): run anything that prints this JSON; see `samples/watch-scripts/`.

  ```json
  {"status": "ok|info|warn|error", "summary": "…", "link": "https://…",
   "items": [{"title": "…", "status": "…", "detail": "…", "link": "https://…"}]}
  ```

  Plain text also works: the first line is the summary and the exit code the status (0 ok, 1 warn, other error).
  Watch settings arrive as `AVAMUX_WATCH_<KEY>` environment variables.

- **.NET plugin**: implement `Avamux.Plugins.IWatchProvider` (project `src/Avamux.Plugin.Abstractions`) and drop
  the DLL into the plugins folder, or point a `plugin` watch at it with `--set assembly=/path/to.dll`.
  `samples/watch-plugin/` is a complete example.
