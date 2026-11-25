# Events

Timers, scheduled tasks and calendar events, on one calendar. Any of them can run a script when it starts and when
it ends, so the calendar doubles as a scheduler.

## Kinds

| Kind | What it is |
|---|---|
| Timer | A countdown: `25m Focus`. Goes off once, with a notification, the theme's `timer` sound, and an optional script. |
| Task | A script on a schedule: once at a time, or repeatedly. |
| Calendar event | Something with a start and an end, possibly days apart, possibly repeating. Can run a script at each. |

Events can have a location (an address, a link, any text), file attachments (copied into `attachments/` in the data
folder), a colour, and a markdown description.

## Repeating

Daily, on chosen weekdays, weekly, monthly, yearly, or at a fixed interval (every 15 minutes, every 3 days), until a
date or for a number of times.

To skip a single occurrence (a holiday, a cancelled session), right-click it on the calendar. Skipped dates are listed
in the event's editor, where they can be brought back. A skipped occurrence has no start, end, notification or
script.

An occurrence missed by more than `events.missedGraceSeconds` (120) because avamux was closed or the machine was
asleep is skipped rather than run late.

## The calendar

- **Month**, **Week** and **Day** views. In Week and Day, events sit at their hours and overlapping ones share the
  column.
- **Running now**, above the day's list, shows events in progress and event scripts still running, each with a
  **Stop** button.
- Double-click an event to edit it, or an empty spot to add one.
- Each kind has a colour (`events.calendarColor`, `events.taskColor`, `events.timerColor`; empty follows the theme).
  An event's own colour wins.

## Scripts

An event's start and end scripts run through the shell in your home folder, for at most
`events.scriptTimeoutSeconds` (300). Their output goes to the [Logs](logs.md) page. They get:

| Variable | Value |
|---|---|
| `AVAMUX_EVENT_ID` | The event's id. |
| `AVAMUX_EVENT_TITLE` | Its title. |
| `AVAMUX_EVENT_KIND` | `calendar`, `task` or `timer`. |
| `AVAMUX_EVENT_PHASE` | `start` or `end`. |
| `AVAMUX_EVENT_START` | When this occurrence starts (ISO 8601). |
| `AVAMUX_EVENT_END` | When it ends, or empty. |

A script can call `avamux` itself, so an event can open a terminal running a build, switch the theme at night, or
start the Caffeine page for a meeting:

```sh
avamux event add "Deep work" --start 09:00 --duration 2h --repeat weekly --days weekdays \
  --on-start "avamux caffeine on --for 2h; avamux app nav terminals" \
  --on-end   "avamux app notify 'Deep work' 'Time for a break'"
```

## From the command line

Times are flexible: `2026-12-24T18:00`, `"tomorrow 9:00"`, `17:30`, `+90m`. Lengths are `90s`, `45m`, `1h30m`,
`3d`.

```sh
avamux event timer 25m Focus --run "notify-send done"
avamux event task Backup --run "~/bin/backup.sh" --at 02:00 --repeat daily
avamux event task Ping --run "curl -fsS https://example.com/health" --repeat interval --every 15m
avamux event add Standup --start "tomorrow 9:30" --duration 15m --repeat weekly --days mon,tue,fri
avamux event add Conference --start 2026-06-10 --end 2026-06-12 --all-day --location "Hall B" --attach ticket.pdf
avamux event skip Standup 2026-12-25 2026-12-28   # no start, end, notification or script on those days
avamux event unskip Standup 2026-12-25
avamux event agenda                               # what happens in the coming days
avamux event running                              # events in progress and event scripts still running
avamux event run Backup && avamux event stop Backup
avamux event list && avamux event clear           # clear deletes completed timers, tasks and events
```

`event add` also takes `--every`, `--until`, `--count`, `--notes` (markdown) and `--color`; `event edit` changes any
of them.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `events.enabled` | true | Run events at all. |
| `events.desktopNotifications` | true | Notify when events start and end and timers go off. |
| `events.scriptTimeoutSeconds` | 300 | Longest a script may run. |
| `events.missedGraceSeconds` | 120 | How late an occurrence may still run. |
| `events.calendarColor`, `events.taskColor`, `events.timerColor` | empty | Calendar colours per kind. |

Events are kept in `events.json` in the data folder. The sounds for start, end and timers come from the
[theme](themes.md#sounds).
