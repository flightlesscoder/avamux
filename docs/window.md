# The window

## One instance

avamux runs once per data folder. Starting it again brings the running window to the front (`avamux app activate`
does the same), and every `avamux` command talks to that one instance. See [CLI](cli.md).

## Navigation

The navigation bar down the side holds every page, then your [custom pages](custom-pages.md).

- `ui.navStyle`: `standard` (icons with labels) or `mini` (small icons with tooltips).
- `ui.startPage`: the page avamux opens on (`terminals`).
- The AI Chat and AI Workloads pages can be taken out of the navigation with `ai.chat` and `ai.workloads`.
- A page that wants you gets a dot on its entry, and some show a short badge.
- `avamux app nav <page>` switches page from the command line. Some pages take a part after a colon:
  `avamux app nav workloads:models`.

When something needs attention while avamux is in the background (an agent waiting, an event starting, a watch that
changed), the taskbar or dock icon flashes or gets a badge (`ui.taskbarAlert`). `avamux app notify <title> [message]`
raises a notification of your own.

## The command palette

The command palette opens from anywhere, terminals and the wiki preview included, with `ui.commandPaletteKey`: by
default **Shift pressed twice** quickly. A tap only counts when nothing else was pressed while the key was down, so
holding Shift for capitals never opens it. Other double taps (`Ctrl Ctrl`, `Alt Alt`, `Cmd Cmd`) or an ordinary
shortcut (`Ctrl+Shift+P`) work too; empty switches it off.

It lists:

- every page, to go to;
- New Terminal, New AI Chat, Open Today's Note, Boss Screen;
- the wiki's pages, to open.

Type to narrow the list: every word typed has to be found in the title, in order, either as one piece or letter by
letter (`nt` finds New Terminal). ↑ ↓ choose, Enter runs, Esc closes.

The palette is a preview; more is to come.

## The boss screen

Ctrl+Shift+B (`ui.bossKey`, empty to switch it off) swaps the window for something that looks like work, in the
spirit of the boss keys of old DOS games: a budget spreadsheet, or with `ui.bossScreen` set to `build`, a build log
that scrolls forever.

- The window title changes to match, and sounds stay quiet.
- avamux's other windows are minimized (`ui.bossHidesWindows`).
- The same key or Esc brings everything back. Terminals keep running underneath.
- It works while a terminal has the keyboard too, but not while typing in the dashboard or the wiki preview.

`avamux app boss` toggles it from the command line (`--on`, `--off`).

## Settings

| Setting | Default | What it does |
|---|---|---|
| `ui.startPage` | `terminals` | The page avamux opens on. |
| `ui.navStyle` | `standard` | `standard` or `mini`. |
| `ui.taskbarAlert` | true | Flash or badge the taskbar icon when something needs attention. |
| `ui.commandPaletteKey` | `Shift Shift` | Opens the command palette. Empty turns it off. |
| `ui.bossKey` | `Ctrl+Shift+B` | Shows and hides the boss screen. Empty turns it off. |
| `ui.bossScreen` | `spreadsheet` | `spreadsheet` or `build`. |
| `ui.bossHidesWindows` | true | Minimize avamux's other windows behind the boss screen. |

The window chrome (system or custom title bar) belongs to the theme: see [Themes](themes.md).

## Other commands

```sh
avamux app status                       # version, uptime, current page, counts
avamux app paths                        # where data and configuration live
avamux app screenshot window.png        # a PNG of the main window (or another avamux window)
avamux app ping                         # is avamux running?
avamux app quit
```
