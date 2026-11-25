# Watching

A watch is a check that runs every few minutes and reports a status (ok, info, warn, error), a one-line summary, a
link and a list of items: your open pull requests, a build pipeline's last runs, a work item query, how full a disk
is. When the result changes, avamux notifies you and plays the theme's `watch-changed` sound (`pull-request` for pull
request watches).

The Watching page lists them with their status and items. Each has a clickable link you can edit, and can be checked
now, switched off, or changed. The [Repositories](repositories.md#pull-requests) page's Pull Requests tab is a watch
too.

## Kinds

`avamux watch kinds` lists every kind, plugins' included, and `avamux watch kind <kind>` describes one's settings.
Settings are given with `--set key=value`.

| Kind | Watches | Settings |
|---|---|---|
| `github-pr` | One pull request, or a repository's list. | `repo` (owner/name, required), `number`, `state` (open, closed, all), `author`, `tokenEnv`, `apiUrl` |
| `ado-pr` | Azure DevOps pull requests. | `repository` (empty: every one in the project), `pullRequestId`, `status` (active, completed, abandoned, all) |
| `ado-pbi` | Azure DevOps work items. | `ids` (comma separated), or `wiql`: a query to run instead |
| `ado-pipeline` | A pipeline's recent runs. | `definitionId` (required), `branch`, `top` (5) |
| `repo-pr` | A local repository's pull requests, through `gh` or your own command. | `path` (required), `command`, `limit`, `me` |
| `command` | Anything a script can find out. | `command` (required), `cwd`, and any settings of your own |
| `plugin` | A watch kind from a DLL you name. | `assembly` |

The Azure DevOps kinds all need `organization` and `project`, and take `patEnv` (default `AZURE_DEVOPS_EXT_PAT`,
then `ADO_PAT`) and `baseUrl` (for Azure DevOps Server). GitHub reads `GITHUB_TOKEN`, then `GH_TOKEN`; a token is
optional for public repositories.

The GitHub and Azure DevOps kinds follow the public REST APIs but have not been run against live accounts yet.

```sh
avamux watch add "My PRs" --kind github-pr --set repo=owner/name --set author=me
avamux watch add "My work" --kind ado-pbi --set organization=my-org --set project=MyProject \
  --set "wiql=SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me AND [System.State] <> 'Done'"
avamux watch add "Main build" --kind ado-pipeline --set organization=my-org --set project=MyProject \
  --set definitionId=42 --set branch=refs/heads/main --interval 10
```

## Script watches

A `command` watch runs any command line through `/bin/sh` and reads what it prints:

```json
{"status": "ok|info|warn|error", "summary": "…", "link": "https://…",
 "items": [{"id": "…", "title": "…", "status": "…", "detail": "…", "link": "https://…"}]}
```

Plain text also works: the first line is the summary and the exit code the status (0 ok, 1 warn, anything else
error).

The watch's settings arrive as `AVAMUX_WATCH_<KEY>` environment variables (the key upper-cased, anything not a
letter or digit made `_`), with `AVAMUX_WATCH_NAME` and `AVAMUX_WATCH_LINK` besides, so one script can serve several
watches. `samples/watch-scripts/` has two examples; this one warns when a disk is getting full:

```sh
avamux watch add "Disk" --kind command --interval 10 \
  --set "command=$PWD/samples/watch-scripts/disk-space.sh" --set mount=/home --set limit=85
```

A check that takes longer than `watching.timeoutSeconds` (60) is stopped and reported as an error.

## Plugin watches

Implement `Avamux.Plugins.IWatchProvider` (project `src/Avamux.Plugin.Abstractions`): a `Kind`, a `Description`,
the `Settings` it understands with a line of help each, and `CheckAsync`, which returns a `WatchResult`. The class
needs a public parameterless constructor.

Then either drop the DLL into the plugins folder (it is then listed under Settings → Plugins and its kind is
available to every watch), or point a `plugin` watch at it with `--set assembly=/path/to.dll`. An
[`IAvamuxPlugin`](plugins.md) can add watch kinds too, with `AddWatchProvider`.

`samples/watch-plugin/` is a complete example, a kind that checks web addresses answer:

```sh
dotnet build samples/watch-plugin
avamux plugin install samples/watch-plugin/bin/Debug/net10.0/SampleWatchPlugin.dll
avamux watch add "Sites" --kind http-status --set urls=https://example.com,https://example.org
```

## From the command line

```sh
avamux watch list
avamux watch show "My PRs"              # in full, with the items of its last check
avamux watch check                      # every watch now; or name one
avamux watch edit "My PRs" --interval 15
avamux watch disable "My PRs" && avamux watch enable "My PRs"
avamux watch open "My PRs"              # its link, in the browser
avamux watch remove "My PRs"
```

`watch add` also takes `--link`, `--disabled`, and `--no-check` (do not check it straight away).

## Settings

| Setting | Default | What it does |
|---|---|---|
| `watching.enabled` | true | Run watches at all. |
| `watching.defaultIntervalMinutes` | 5 | Minutes between checks, for watches that do not set their own. |
| `watching.timeoutSeconds` | 60 | Longest a check may take. |
| `watching.notifyOnChange` | true | Notify when a result changes. |
| `watching.checkOnStartup` | true | Check every watch when avamux starts. |

Watches are kept in `watches.json` in the data folder.
