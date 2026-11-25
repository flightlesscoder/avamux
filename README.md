# avamux

Terminals, watches, events, a dashboard, logs, a wiki and git repositories in one Avalonia window, with a CLI
that can do everything the window can. Runs on Linux and macOS.

![The three-way merge tool](docs/screenshots/merge/02-three-way-view.png)

## Build and run

```sh
dotnet build
dotnet run --project src/Avamux          # start the app (a second start just brings the first to the front)
dotnet test                              # core tests, and UI tests run headless
```

Linux needs WebKitGTK 4.1 (`libwebkit2gtk-4.1`). macOS uses the system WKWebView.

The built executable is `src/Avamux/bin/Debug/net10.0/avamux`. Put it (or a `dotnet publish` output) on your `PATH`
to use the CLI as `avamux`. The load tests, the startup suite and the other opt-in tests are described in
[Development](docs/development.md).

## What is in it

| Page | What it does |
|---|---|
| [Terminals](docs/terminals.md) | Tabbed terminals on a real pty, with split panes, tabs in their own windows, and a marker on each tab for what it is doing, down to an AI agent waiting for input. |
| [Browser panes](docs/browser-panes.md) | A real web view beside any terminal tab, with an inspector, for you and for the agent in that terminal, which drives it with `avamux browser …`. |
| [Wiki](docs/wiki.md) | Markdown pages with a live preview, `[[links]]`, `#tags`, pictures, daily notes, previews of almost any file, and the wiki's own history in git. |
| [Repositories](docs/repositories.md) | Changes, history, branches and pull requests for your git repositories. Stage files, hunks or single lines. |
| [Merge tool](docs/merge-tool.md) | Resolve merge, rebase, cherry-pick and revert conflicts in a three-way view, or in your own editor. |
| [Git Activity](docs/git-activity.md) | Heatmaps, leaderboards and AI use across hundreds of repositories: GitHub and Azure DevOps organisations, folders of clones. |
| [Events](docs/events.md) | Timers, scheduled scripts and a calendar of repeating events, each able to run a script when it starts and ends. |
| [Watching](docs/watches.md) | Periodic checks (pull requests, work items, pipelines, any script) with a status, a summary and a list of items. |
| [Dashboard](docs/dashboard.md) | Your own HTML, CSS and JavaScript page, which can run any avamux command. |
| [Search](docs/search.md) | One box over wiki pages, tags, events, watches, terminals, repositories, logs and settings. |
| [Logs](docs/logs.md) | App logs, crash reports, the scrollback of ended terminals and script output, kept within size and age limits. |
| [System](docs/system.md) | CPU, memory, GPUs, network and volumes, and a treemap of what takes the space on any disk. |
| [Caffeine](docs/caffeine.md) | Keeps the computer awake: until you stop it, for a while, or while terminals are busy. |
| [C# Interactive](docs/csharp-interactive.md) | A C# prompt inside the running app, beside a screen of live controls built from your code and XAML. |
| [AI Chat](docs/ai-chat.md) | Conversations with coding agents (Claude Code, pi and others): thinking, tool calls, permission requests, pictures, and replies read aloud. |
| [AI Workloads](docs/ai-workloads.md) | Every model server, coding agent and AI job on the machine: start, stop and swap model servers and see why one failed. |
| [AI Actions](docs/ai-actions.md) | Right-click actions in the wiki and terminals: add tags, summarise, rewrite, ask about a page or folder. |
| [Custom pages](docs/custom-pages.md) | Your own entries in the navigation: a terminal running one command, a web page, or a .NET plugin. |
| [Themes](docs/themes.md) | Built-in themes, theme files in YAML or JSON, a live theme editor, a neon glow and sounds. |

Also: the [window itself](docs/window.md) (navigation, the command palette, the boss screen) and every
[keyboard shortcut](docs/keyboard-shortcuts.md).

## The CLI

Everything the window does can be done from a shell, a script, the dashboard or an agent:

```sh
avamux /?                                         # help; also per group (avamux terminal /?) and per command
avamux terminal new --name build -- dotnet watch
avamux event timer 25m Focus --run "notify-send done"
avamux wiki today
avamux browser open localhost:3000 && avamux browser snapshot -i
avamux settings list --json                       # --json on any command for machine-readable output
```

See [CLI](docs/cli.md) for how it reaches the running app and what each group of commands covers.

## Configuration

Settings are JSON files layered from `~/avamux.json` down to `./avamux.local.json` in the folder avamux was started
in; a file only needs the keys it changes. Settings in the app shows every key and where its value comes from.
[Configuration](docs/configuration.md) has the layering, every setting with its default, and what lives in the data
folder.

## Extending avamux

- [Plugins](docs/plugins.md): .NET class libraries that add menu items, watch kinds, AI providers, workload sources
  and settings panels.
- [Custom pages](docs/custom-pages.md): pages built by a plugin, a web page or a terminal command.
- [Watches](docs/watches.md#script-watches): any script that prints a little JSON.
- [Dashboard](docs/dashboard.md): HTML and JavaScript that calls `avamux.command(…)`.
