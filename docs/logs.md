# Logs

The Logs page lists what avamux writes down, by category:

| Category | What it holds | Folder in the data folder |
|---|---|---|
| `app` | avamux's own log: what it did, warnings, errors. | `logs/app` |
| `crashes` | Crash reports. | `logs/crashes` |
| `terminals` | The scrollback of terminals that ended (`terminal.preserveDeadSessions`). | `logs/terminals` |
| `scripts` | Output of event scripts. | `logs/scripts` |

Open one to read it; delete one, a category, or everything.

## Retention

Logs are pruned by age, then by count, then by total size:

| Setting | Default | What it does |
|---|---|---|
| `logs.maxAgeDays` | 30 | Files older than this are deleted. |
| `logs.maxFileCount` | 500 | At most this many files are kept. |
| `logs.maxTotalSizeMb` | 200 | All logs together stay under this. |
| `logs.maxFileSizeMb` | 10 | One app log file grows to this before a new one starts. |
| `logs.level` | `info` | How much the app log records: `debug`, `info`, `warn` or `error`. |

## From the command line

```sh
avamux logs list --category terminals
avamux logs show <name>
avamux logs tail --lines 100           # the most recent app log lines
avamux logs write "Deployed 1.4"       # add a line of your own to the app log
avamux logs usage                      # space used against the limits
avamux logs prune                      # apply the limits now
avamux logs delete <name>
avamux logs clear --category scripts
```

Other things keep logs of their own: model servers and start scripts show theirs on the
[AI Workloads](ai-workloads.md) page.
