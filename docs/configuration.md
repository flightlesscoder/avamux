# Configuration

## Settings files

Settings are JSON, merged in this order (later wins):

| | File | Scope name |
|---|---|---|
| 1 | built-in defaults | |
| 2 | `~/avamux.json` | `home` |
| 3 | `~/avamux.local.json` | `home-local` |
| 4 | `./avamux.json` (the folder avamux was started in) | `working` |
| 5 | `./avamux.local.json` | `working-local` |

Layers are merged key by key, so a file only needs the keys it changes:

```json
{ "terminal": { "scrollback": 10000, "fontSize": 14 }, "watching": { "defaultIntervalMinutes": 5 } }
```

Comments and trailing commas are allowed, but saving a setting from the app or with `avamux settings set` rewrites
that file without its comments. The files are watched, and a change on disk applies straight away.

A common split: shared preferences in `~/avamux.json`, and anything specific to one machine (paths to a Python, model
files, folders of scripts) in `~/avamux.local.json`, which you keep out of any dotfiles repository.

**Settings** in the app shows every setting, which file its value comes from, and which file to save changes to. Its
search box covers the settings and the Hotkeys list.

```sh
avamux settings list                          # every key, its value and its source
avamux settings get terminal.fontSize
avamux settings set terminal.scrollback 20000 --scope home-local
avamux settings unset terminal.scrollback --scope home-local   # the next layer (or the default) applies again
avamux settings files                         # the files, in order of precedence
avamux settings reload
```

`settings set` writes to `home` unless `--scope` says otherwise, and turns the value into the setting's type
(`true`, `14`, text).

## Every setting

Each feature's page has its settings with more on what they do. This is the whole list, with the defaults.

| Setting | Default | See |
|---|---|---|
| **terminal** | | [Terminals](terminals.md#settings) |
| `terminal.shell` | empty (`$SHELL`) | |
| `terminal.term` | `xterm-256color` | |
| `terminal.scrollback` | 10000 | |
| `terminal.fontFamily` | `JetBrainsMono Nerd Font Mono, …, monospace` | |
| `terminal.fontSize` | 13 | |
| `terminal.copyOnSelect` | false | |
| `terminal.cursorBlink` | true | |
| `terminal.cursorPulse`, `…Shape`, `…Color`, `…Double`, `…Rate`, `…Glow` | false, `block`, empty, false, 30, 60 | |
| `terminal.crt`, `terminal.crtIntensity`, `terminal.crtFlicker`, `terminal.crtGlow` | false, 50, false, `fast` | |
| `terminal.closeTabOnExit` | true | |
| `terminal.preserveDeadSessions` | true | |
| `terminal.background`, `terminal.foreground` | empty (the theme) | |
| `terminal.agentProcesses` | `claude, codex, gemini, aider, opencode, goose, amp, cursor-agent, copilot, qwen, crush, droid, kiro-cli, pi` | |
| `terminal.agentIdleSeconds` | 3 | |
| `terminal.notifyAgentWaiting` | true | |
| **browser** | | [Browser panes](browser-panes.md#settings) |
| `browser.agentControl` | true | |
| `browser.homePage` | `about:blank` | |
| `browser.searchUrl` | `https://duckduckgo.com/?q={query}` | |
| `browser.keepAliveByDefault` | false | |
| `browser.terminalLinksInPane` | false | |
| `browser.externalSites` | empty | |
| `browser.developerTools` | true | |
| `browser.setBrowserVariable` | false | |
| **wiki** | | [Wiki](wiki.md#settings) |
| `wiki.dailyNoteFormat` | `yyyy-MM-dd dddd` | |
| `wiki.dailyNoteFolder` | `Daily` | |
| `wiki.dailyNoteTemplate` | `# {title}\n\n` | |
| `wiki.rememberedPositions` | 500 | |
| `wiki.autoSave`, `wiki.autoSaveSeconds` | true, 2 | |
| `wiki.clickToPreviewMegabytes` | 20 | |
| `wiki.editorPictures`, `wiki.editorPictureHeight` | true, 220 | |
| `wiki.syncScroll` | true | |
| `wiki.commitWarning`, `wiki.commitWarningMegabytes` | true, 5 | |
| **repositories** | | [Repositories](repositories.md#settings) |
| `repositories.editor` | `nvim` | |
| `repositories.logCount` | 50 | |
| `repositories.pullRequestCommand` | empty (`gh pr list`) | |
| **watching** | | [Watching](watches.md#settings) |
| `watching.enabled` | true | |
| `watching.defaultIntervalMinutes` | 5 | |
| `watching.timeoutSeconds` | 60 | |
| `watching.notifyOnChange` | true | |
| `watching.checkOnStartup` | true | |
| **events** | | [Events](events.md#settings) |
| `events.enabled` | true | |
| `events.scriptTimeoutSeconds` | 300 | |
| `events.desktopNotifications` | true | |
| `events.missedGraceSeconds` | 120 | |
| `events.calendarColor`, `events.taskColor`, `events.timerColor` | empty | |
| **logs** | | [Logs](logs.md#retention) |
| `logs.level` | `info` | |
| `logs.maxTotalSizeMb` | 200 | |
| `logs.maxAgeDays` | 30 | |
| `logs.maxFileCount` | 500 | |
| `logs.maxFileSizeMb` | 10 | |
| **dashboard** | | [Dashboard](dashboard.md#settings) |
| `dashboard.port` | 47600 | |
| `dashboard.allowAppControl` | true | |
| `dashboard.developerTools` | true | |
| **ui** | | [The window](window.md#settings) |
| `ui.startPage` | `terminals` | |
| `ui.navStyle` | `standard` | |
| `ui.taskbarAlert` | true | |
| `ui.bossKey` | `Ctrl+Shift+B` | |
| `ui.commandPaletteKey` | `Shift Shift` | |
| `ui.bossScreen` | `spreadsheet` | |
| `ui.bossHidesWindows` | true | |
| **theme** | | [Themes](themes.md#settings) |
| `theme.name` | `simple` | |
| `theme.accent` | empty | |
| `theme.windowChrome` | `auto` | |
| `theme.glow`, `theme.glowIntensity`, `theme.glowColor` | false, 60, empty | |
| `theme.sounds`, `theme.soundVolume`, `theme.soundSet` | true, 70, `theme` | |
| `theme.soundOnEvent`, `…OnTimer`, `…OnWatch`, `…OnPullRequest`, `…OnAgentWaiting` | true | |
| **caffeine** | | [Caffeine](caffeine.md) |
| `caffeine.graceMinutes` | 5 | |
| `caffeine.keepDisplayOn` | true | |
| **ai** | | [AI Chat](ai-chat.md#settings), [AI Workloads](ai-workloads.md#settings), [AI Actions](ai-actions.md#settings) |
| `ai.chat`, `ai.workloads` | true, true | |
| `ai.workloadScripts`, `ai.workloadModels` | empty | |
| `ai.chatProvider`, `ai.chatFolder` | empty | |
| `ai.expandThinking` | false | |
| `ai.actionsProvider`, `ai.actionsModel`, `ai.actionsEffort` | `claude-code`, `haiku`, `low` | |
| `ai.tts.enabled` | false | [Speaking replies](ai-chat.md#speaking-replies-kokoro) |
| `ai.tts.python`, `ai.tts.model`, `ai.tts.voices` | empty | |
| `ai.tts.voice`, `ai.tts.speed`, `ai.tts.volume` | `af_nova`, 1.05, 0.85 | |
| `ai.tts.autoSpeak`, `ai.tts.idleMinutes` | false, 10 | |

Git Activity keeps its own settings in `git-activity.json`: see [Git Activity](git-activity.md#the-settings-file).

## The data folder

Everything avamux keeps that is not a setting lives in one folder: `~/.local/share/avamux` on Linux (or under
`$XDG_DATA_HOME`), `~/Library/Application Support/avamux` on macOS. `avamux app paths` prints it, and
`AVAMUX_DATA_DIR` moves it (see [Development](development.md#running-a-separate-instance)).

| Path | What it holds |
|---|---|
| `wiki/` | The wiki. |
| `dashboard/` | The dashboard's HTML, CSS and JavaScript. |
| `themes/` | Theme files. |
| `sounds/custom/` | Sounds that replace the theme's. |
| `plugins/` | Installed plugins, one folder each. |
| `plugin-data/<id>/` | Each plugin's own files (AI Actions' `actions.json`, for one). |
| `logs/` | `app/`, `crashes/`, `terminals/`, `scripts/`: see [Logs](logs.md). |
| `attachments/` | Files attached to events. |
| `ai/` | AI Chat's chats (`ai/chats/`), AI Workloads' state (`ai/workloads/`) and the Kokoro helper. |
| `browser/` | Browser panes' cookies, storage and cache, and their screenshots. |
| `disk-scans/` | Saved disk scans. |
| `git-activity/` | Git Activity's mirrors and what it found. |
| `events.json` | Timers, tasks and calendar events. |
| `watches.json` | Watches and their last results. |
| `repositories.json` | The Repositories page's list. |
| `nav.json` | Custom pages. |
| `dashboard.json` | The dashboard's extra sources. |
| `plugins.json` | Which plugins are switched off. |
| `ai-providers.json` | AI Chat's providers. |
| `ai-workloads.json` | AI Workloads' profiles. |
| `git-activity.json` | Git Activity's settings. |
| `wiki-tags.json` | The wiki's tag index. |
| `ui-state.json` | Scroll positions, widths and other things the window remembers. |
| `avamux.sock` | The control socket the CLI talks to (see [CLI](cli.md)). |

Backing up this folder and your `~/avamux*.json` files keeps everything.
