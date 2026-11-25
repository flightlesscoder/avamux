# Development

## Projects

| Folder | What it is |
|---|---|
| `src/Avamux` | The app: windows, pages, the CLI entry point, browser panes. |
| `src/Avamux.Core` | Everything that is not UI: terminals, settings, the wiki, events, watches, git, AI, the command registry the CLI and the dashboard share. Also the web assets (`Web/term`, `Web/wiki`, `Web/dashboard`) the terminal, wiki preview and dashboard pages load. |
| `src/Avamux.Plugin.Abstractions` | The interfaces plugins implement and the host they are given. See [Plugins](plugins.md). |
| `src/Avamux.Plugins.AiActions` | The AI Actions plugin that ships with avamux. See [AI Actions](ai-actions.md). |
| `samples/` | A page plugin, a pageless menu plugin, a watch plugin and two watch scripts. |
| `tests/Avamux.Tests` | Core tests. |
| `tests/Avamux.UiTests` | UI tests that drive the real wiki editor and page headless. |

## Building and testing

```sh
dotnet build
dotnet run --project src/Avamux          # start the app (a second start just brings the first to the front)
dotnet test                              # core tests, and UI tests run headless (tests/Avamux.UiTests)
scripts/load-tests.sh                    # load and startup tests: big pages, thousands of files, start and quit times
```

The UI tests drive the real wiki editor and page in Avalonia's headless platform: no window opens, and they never
touch the system clipboard.

## Load tests

The load tests are skipped by `dotnet test`. `scripts/load-tests.sh` turns them on (`AVAMUX_LOAD_TESTS=1`) and
writes a report of time and memory per measurement to `TestResults/`, marking anything slower than the app can
afford there as **over budget**. Each report ends with a comparison against the previous one, listing every
measurement more than 25% slower.

| Option | What it does |
|---|---|
| `--scale 0.2` | A quick pass. |
| `--scale 5` | Pushes harder, to find where things break. |
| `--only core` / `--only ui` / `--only startup` | Runs one suite. |
| `--on-desktop` | Runs the startup suite on your display instead of a virtual session. |

### The startup suite

The startup suite starts the real app several times over a sandbox and measures the time

- to the first frame;
- to a first terminal command and its output;
- to the first wiki page, and an edit rendered;
- to load a watch plugin and a page plugin;
- of a CLI round trip, and of a second start handing over to the first;
- to start over a 20,000-file wiki;
- to quit, also with busy terminals, checking nothing is left running.

On Linux it runs in a virtual KWin session (`kwin_wayland --virtual`), so no window appears.

### The app's own timeline

The app keeps a timeline of these milestones as it runs. `avamux app timeline` lists them in milliseconds since it
started. With `AVAMUX_TIMELINE_FILE` set, it writes them, the steps of shutting down included, to that file as it
exits.

## Opt-in tests

One test pastes a picture through the desktop's own clipboard tools (`wl-paste`). It replaces and then clears the
clipboard, so it only runs when asked for:

```sh
AVAMUX_SYSTEM_CLIPBOARD_TESTS=1 dotnet test tests/Avamux.UiTests --filter Category=SystemClipboard
```

## Running a separate instance

Two environment variables move avamux away from your own data, which is how the tests run it and how to try
something without touching your wiki, events and settings:

| Variable | What it does |
|---|---|
| `AVAMUX_DATA_DIR` | The data folder (default `~/.local/share/avamux` on Linux, honouring `XDG_DATA_HOME`; `~/Library/Application Support/avamux` on macOS). |
| `AVAMUX_HOME` | The home folder avamux reads `avamux.json` and `avamux.local.json` from. |

Each data folder has its own control socket, so an instance with its own data folder runs beside your usual one,
and a CLI run with the same variables talks to it.

## Finding your way around the UI

`avamux app screenshot <file>` saves a PNG of the main window (or another avamux window) from the running app,
and `avamux app nav <page>` switches to any page, so a change can be checked from a script. `avamux terminal stats
<id>` shows what a terminal's view is doing: output received and shown, how far behind it is, and how long recent key
presses took to reach the screen.
