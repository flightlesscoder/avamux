# CLI

The `avamux` command does everything the window can. Run with no arguments it starts the app, or brings the running
one to the front. Run with a command, it sends that command to the running instance over a local socket and prints the
result.

The same commands are open to [dashboard](dashboard.md) JavaScript (`avamux.command(…)`), to
[plugins](plugins.md) (`CommandAsync`), to [C# Interactive](csharp-interactive.md) (`avamux.Command(…)`) and to
event and watch scripts, which can simply run `avamux`.

## Help

```sh
avamux /?                     # also -?, --?, -h, --help, /help, help
avamux terminal /?            # one group
avamux terminal new /?        # one command: its arguments and options
avamux --version
```

The option spellings (`/?`, `-?`, `--?`, `-h`, `--help`, `/help`) ask for help wherever they stand. The plain words
`help` and `?` ask for help only where a command word would go: alone (`avamux help`), after a group
(`avamux terminal help`), before a command (`avamux help terminal new`) or as a command's only argument
(`avamux terminal new help`). Anywhere else they are ordinary arguments, so `avamux wiki search need help` searches
for "need help". To search for the single word, put it after `--`: `avamux wiki search -- help`.

## Output and exit codes

Results print as readable tables and text. `--json` on any command prints the raw JSON result instead, for scripts:

```sh
avamux terminal list --json | jq -r '.[] | select(.running) | .title'
```

| Exit code | Meaning |
|---|---|
| 0 | Done. |
| 1 | The command failed; the reason is on standard error (`Error: …`). |
| 2 | The command line was wrong (an unknown command, a missing argument); the help follows the message. |
| 3 | avamux is not running, or could not be reached. |

## Conveniences

- Relative paths (`--cwd`, `--file`, `--out`, a repository's path, files to upload) are taken from where the command
  is run, not from where the app was started.
- `avamux terminal new` opens in the current folder unless `--cwd` says otherwise.
- `avamux wiki write <page>` with no content reads it from standard input: `make report | avamux wiki write
  Reports/today.md`.
- Inside an avamux terminal, `avamux browser …` acts on the pane beside that tab, and `avamux terminal split` splits
  that terminal (both through `AVAMUX_TERMINAL_ID`).
- Times and lengths are written as people write them: `17:30`, `"tomorrow 9:00"`, `+90m`, `1h30m`, `3d`.

## The groups

| Group | What it covers | More |
|---|---|---|
| `app` | The window: activate, nav, notify, status, paths, screenshot, timeline, boss, quit. | [The window](window.md) |
| `terminal` | Tabs and panes: new, split, send, read, list, select, rename, move, resize, stats, close. | [Terminals](terminals.md#from-the-command-line) |
| `browser` | Browser panes, for you and for agents. | [Browser panes](browser-panes.md#for-agents) |
| `wiki` | Pages: list, read, write, render, search, open, import, mkdir, move, delete, tags, today. | [Wiki](wiki.md#from-the-command-line) |
| `repo` | Repositories: status, changes, diff, stage, commit, log, branches, switch, prs, and more. | [Repositories](repositories.md#from-the-command-line) |
| `activity` | Git Activity: sources, scans, summaries and top lists. | [Git Activity](git-activity.md#from-the-command-line) |
| `event` | Timers, tasks and calendar events. | [Events](events.md#from-the-command-line) |
| `watch` | Watches and their kinds. | [Watching](watches.md#from-the-command-line) |
| `dashboard` | The dashboard page and its extra sources. | [Dashboard](dashboard.md) |
| `logs` | Log files and retention. | [Logs](logs.md#from-the-command-line) |
| `search` | One search over everything. | [Search](search.md) |
| `system`, `disk` | System readings; disk scans. | [System](system.md) |
| `caffeine` | Keeping the computer awake. | [Caffeine](caffeine.md) |
| `ai` | AI Chat's providers and chats, one-off prompts, Kokoro speech, and AI Workloads (`ai workloads …`). | [AI Chat](ai-chat.md#from-the-command-line), [AI Workloads](ai-workloads.md#from-the-command-line) |
| `page` | Custom pages. | [Custom pages](custom-pages.md#from-the-command-line) |
| `plugin` | Plugins. | [Plugins](plugins.md#installing-and-managing) |
| `theme` | Themes and theme files. | [Themes](themes.md) |
| `settings` | Every setting, and which file it comes from. | [Configuration](configuration.md) |

## A taste

```sh
avamux terminal new --name build -- dotnet watch
avamux terminal send build "^C" --keys
avamux settings set terminal.scrollback 20000 --scope home-local
avamux event timer 25m Focus --run "notify-send done"
avamux event add Standup --start "tomorrow 9:30" --duration 15m --repeat weekly --days mon,tue,fri
avamux watch add "My PRs" --kind github-pr --set repo=owner/name --set author=me
avamux repo add . && avamux repo status
avamux search "release notes" --source wiki --source events
avamux wiki today
avamux theme set classic90s
avamux caffeine on --for 1h30m
avamux browser open localhost:3000 && avamux browser snapshot -i
avamux ai run "Summarise the README in one line" --provider claude-code --model haiku
```

## How it reaches the app

The app listens on a Unix socket in its data folder (`avamux.sock`; a data folder with a very long path uses a short
name in `/tmp` instead). Only you can reach it. A CLI run with `AVAMUX_DATA_DIR` set talks to the instance using that
data folder, which is how a test instance runs beside your own (see
[Development](development.md#running-a-separate-instance)).
