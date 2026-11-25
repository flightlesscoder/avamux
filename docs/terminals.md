# Terminals

Tabbed terminals: xterm.js on a real pty, one tab per shell or program, with split panes, tabs that move to their
own windows, and a browser that can sit beside each tab.

## Tabs

A tab shows what is running in it, or a name you give it (double-click the tab, Enter to keep it, Esc to cancel), and
a marker for what the terminal is doing:

| Marker | Meaning |
|---|---|
| idle | At the prompt. |
| running | A command is running. |
| agent working | An AI agent is running and printing. |
| agent waiting | An AI agent has gone quiet: it is probably waiting for you. |
| ended | The program has exited (seen when `terminal.closeTabOnExit` is off). |

An agent is any program named in `terminal.agentProcesses` (Claude Code, Codex, Gemini, aider, opencode, goose, pi
and others to begin with). One that has printed nothing for `terminal.agentIdleSeconds` (3) is taken to be waiting
for input. Unless that tab is the one in front of you, avamux then plays the theme's `agent-waiting` sound and shows
a notification (`terminal.notifyAgentWaiting`), so you can work elsewhere and come back when the agent needs you.

Tabs can be moved to their own window and back: right-click the tab, or use the button at the end of the tab strip.

When a program ends, its tab closes (`terminal.closeTabOnExit`), and its scrollback is saved to the
[Logs](logs.md) page (`terminal.preserveDeadSessions`), so nothing it printed is lost.

## Split panes

Right-click → **Split Right** / **Split Down** starts a terminal beside or below the one you are typing in, in the same
folder, as a pane of the same tab.

- Drag the dividers between panes to resize them; double-click one to even them out.
- Panes without the keyboard are dimmed. The tab shows the pane you last typed in and how many panes it has.
- The tab's menu can even the panes out, move one to a tab of its own, or close one.
- Closing the tab closes all its panes; Ctrl+Shift+W in a pane closes just that pane.

`avamux terminal split [id] [--down]` does the same from the command line. Run inside a terminal, it splits that one.

## Keys

| Keys | What they do |
|---|---|
| Ctrl+Shift+T | New tab |
| Ctrl+Shift+W | Close the tab (in a split tab: the pane you are typing in) |
| Ctrl+PageUp / Ctrl+PageDown | Previous / next tab |
| Ctrl+Shift+C, Ctrl+Insert | Copy |
| Ctrl+Shift+V, Shift+Insert | Paste |
| Ctrl+Shift+L | Open the browser beside this tab, or show and hide it |
| Ctrl+Click on a link | Open it in the tab's browser pane (or the other way round, see [Browser panes](browser-panes.md#links-from-terminals)) |

Every other key combination goes to the program, so terminal programs keep their own shortcuts. On macOS ⌘C and ⌘V
copy and paste too.

## Output and flow control

A program that writes faster than the view can show waits for it, as in any terminal. Ctrl+C therefore stops the
screen at once, instead of leaving it scrolling through a backlog for seconds after the program has gone.
`avamux terminal stats <id>` shows how far behind a view is and how long recent key presses took to reach the screen.

## The look

Terminals follow the theme unless `terminal.background` / `terminal.foreground` are set.

**CRT.** `terminal.crt` gives the view an old monitor's look: scanlines, glowing text and darkened corners, at
`terminal.crtIntensity` (0 to 100), with an optional `terminal.crtFlicker`. The glow is `terminal.crtGlow`:

| Value | Look | Cost |
|---|---|---|
| `fast` (default) | One soft halo over the text, in the foreground colour. | Cheap. |
| `colour` | Each character glows in its own colour. | Several times slower to draw: full-screen programs such as nvim lag. |
| `off` | No glow. | None. |

**Pulsing cursor.** `terminal.cursorPulse` replaces the blinking cursor with one that beats like a pulse and glows:
`terminal.cursorPulseShape` (`block`, `outline` or `bar`), `terminal.cursorPulseRate` (beats per minute, 30),
`terminal.cursorPulseDouble` (beat twice, like a heart), `terminal.cursorPulseGlow` (0 to 100) and
`terminal.cursorPulseColor` (empty follows the accent).

## Settings

| Setting | Default | What it does |
|---|---|---|
| `terminal.shell` | empty (`$SHELL`) | The shell new tabs start. |
| `terminal.term` | `xterm-256color` | `TERM` for programs in the terminal. |
| `terminal.scrollback` | 10000 | Lines kept per terminal. |
| `terminal.fontFamily` | JetBrainsMono Nerd Font, then fallbacks | Font list, first one installed wins. |
| `terminal.fontSize` | 13 | |
| `terminal.copyOnSelect` | false | Copy as soon as text is selected. |
| `terminal.cursorBlink` | true | |
| `terminal.closeTabOnExit` | true | Close a tab when its program ends. |
| `terminal.preserveDeadSessions` | true | Save an ended terminal's scrollback to Logs. |
| `terminal.agentProcesses` | claude, codex, gemini, aider, … | Programs treated as AI agents. |
| `terminal.agentIdleSeconds` | 3 | Quiet time before an agent counts as waiting. |
| `terminal.notifyAgentWaiting` | true | Notify when an agent starts waiting. |

The CRT and cursor settings are above. All of them are under Settings, and in [Configuration](configuration.md).

## From the command line

```sh
avamux terminal new --name build -- dotnet watch     # a tab running a command (--cwd, --background)
avamux terminal list
avamux terminal send build "^C" --keys               # type into it; ^X is Ctrl+X with --keys
avamux terminal send build 'make test\n' --keys      # with --keys, escapes too: \n \r \t \e \xHH
avamux terminal send build "$(cat notes.txt)" --paste  # as a bracketed paste
avamux terminal read build                           # its text, scrollback included, as the tab shows it
avamux terminal split build --down
avamux terminal rename build                         # an empty name goes back to showing what is running
avamux terminal select build && avamux terminal close build
```

Every terminal gets `AVAMUX_TERMINAL_ID` in its environment. Commands run inside a terminal use it to act on their
own tab: `avamux terminal split` with no id, and every `avamux browser …` command (see
[Browser panes](browser-panes.md#for-agents)).

`terminal send` and `terminal read` together let a script, or an agent in another tab, drive a program that only
has an interactive interface.
