# Git Activity

Where the work is going across hundreds of repositories: whole GitHub organisations, Azure DevOps organisations or
projects, folders of clones, single URLs. Git Activity reads every repository of its sources and shows, for any range
of days (the last two months to begin with), who worked on what, when, in which languages and frameworks, and with
how much AI help.

## The views

- **Heatmap**: rows by one thing and columns by another: repositories × days, contributors × repositories,
  frameworks × weeks, weekday × hour, and so on. Pivot by repositories, contributors, branches, languages, file types,
  frameworks, AI tools and models, days, weeks, weekdays or hours. Click a row or a cell to look at just that;
  right-click a row for more.
- **Leaderboards**: top contributors, repositories, branches (mainline ones marked), languages, frameworks and file
  types, each with its weekly line, whether it is picking up or slowing down, and how much of it had AI help.
- **Rhythm**: when the work is done (in each author's own time zone), per person, and week-by-week momentum per
  repository.
- **AI**: AI help week by week, by tool (Claude Code, GitHub Copilot, Cursor, Codex, Aider, Devin, Gemini, Amazon Q…),
  by model, and who uses it.
- **Frameworks & Languages**: what the work was written in and with.
- **Commits**: the commits behind the figures. Double-click one, or right-click any repository, branch or commit, to
  open it on the [Repositories](repositories.md) page (a repository that is not on this computer is cloned first) or
  in the browser.

## Filters and measures

- **Range**: presets such as Last 30 days, This quarter, Last year and All time, or any From and To. An empty From is
  the beginning, an empty To is the present.
- **Still active**: contributors and repositories with a commit in the last N days of the range, to hide people who
  left and projects gone quiet. The defaults are `activePeopleDays` and `activeReposDays`.
- **Branches**: all, mainline only, or everything but mainline. Mainline names are the `mainlineBranches` list
  (`main`, `master`, `develop`, `release/**`, `prod` and the like).
- **With or without** AI, bots, merges, words.
- **Contributors…** (or right-click one) chooses which contributors count.
- **Measure**: commits, lines changed, lines added, lines removed, files, or contributors.

## How AI help is recognised

From `Co-authored-by` trailers, "Generated with" lines and agent accounts. The commit is credited to the person, not
the tool: a commit authored by the Copilot agent counts for the human it worked with.

Extra rules go in `aiRules`. Each is a case-insensitive regular expression over the author, the committer, the
message, or any of them, and the tool to credit. `agent: true` says the matching identity is the AI itself (it
authored the commit) rather than an assistant:

```json
"aiRules": [
  { "tool": "Our review bot", "pattern": "^review-bot@", "field": "author", "agent": true },
  { "tool": "Claude Code", "pattern": "via claude", "field": "message" }
]
```

## How languages and frameworks are recognised

Over 500 file extensions and names (C#, VB.NET, F#, Razor, XAML, MSBuild, web, data, SQL, scripts,
infrastructure…) and over 150 frameworks (Next.js, React, React Native, Angular, Vue, ASP.NET Core, Blazor, MediatR,
EF Core, MAUI, WPF, Spring, Django, Terraform…).

Frameworks are recognised from each project's manifests *as they were when each commit was made*. A repository that
moved from Angular to React shows Angular for the work before the move and React after it, including moves that arrive
through a merge. A file counts toward the frameworks of the projects it is in; test frameworks count only for test
files.

Add your own with `languages` (`".ext"` or a file name → language) and `frameworks`:

```jsonc
"frameworks": [
  {
    "name": "Our UI kit", "category": "Front end",
    "packages": ["@example/ui-*"],          // npm, NuGet, PyPI, Maven, Go, Cargo, gem, Composer or pub
    "sdks": [],                              // .NET SDKs or MSBuild switches such as UseMaui
    "markers": ["ui-kit.config.*"],          // files that mean a project uses it
    "files": []                              // files that belong to it wherever they are (*.tf, Dockerfile)
  }
]
```

## Sources

| Kind | What it reads | Credentials |
|---|---|---|
| `github` | An organisation's or user's repositories (`apiUrl` for GitHub Enterprise). | `GITHUB_TOKEN`, `GH_TOKEN`, or `gh auth login`; `tokenEnv` names another variable. |
| `azure-devops` | An organisation's repositories, or one project's. `baseUrl` for Azure DevOps Server. | `az login`, or a PAT in `AZURE_DEVOPS_EXT_PAT` (or `ADO_PAT`); `patEnv` names another variable. |
| `local` | A repository, or a folder with repositories up to `depth` (3) folders below it. | None. |
| `git` | Clone URLs. | git's own credentials. |
| `repositories` | The repositories on the Repositories page. | None. |

Every source can narrow what it lists: `repositories` (only these), `include` and `exclude` (name globs),
`includeArchived` and `includeForks`. `local` and `repositories` sources can `fetch` before reading, so remote
branches are current.

## How reading works

Remote repositories are kept as shallow bare mirrors holding only what is read (`git-activity/mirrors` in the data
folder, or `mirrorFolder`): the period, or from `readFrom` (a date, or `all` for the whole history). Choosing an
earlier start on the page reads further back by itself.

Later reads only read the commits that are new since the last one. A repository whose branch tips have not moved is
not fetched or read again, and one GitHub says was not pushed to in the period is not fetched at all. `parallel` (4)
repositories are read at a time, and `refreshMinutes` (0, only when asked) refreshes on a timer.

For very large organisations, `"detail": "files"` downloads file names only, so files are counted but lines are not.
Repositories bigger than `maxLinesRepoMb` (2000) are read that way regardless.

## The settings file

Git Activity keeps its own settings in `git-activity.json` in the data folder, apart from the app's settings, so it
can be shared or generated on its own. **Git Activity Settings** edits it, and **Edit File** opens it.
`avamux activity config` prints where it is, and `--show` prints it.

| Key | Default | What it does |
|---|---|---|
| `days` | 61 | Days back to read, and to show at first. |
| `readFrom` | empty | Read from this date (yyyy-MM-dd) instead, or `all`. |
| `topCount` | 15 | Entries in the leaderboards and heatmap rows. |
| `parallel` | 4 | Repositories read at the same time. |
| `refreshMinutes` | 0 | Minutes between automatic refreshes; 0 only when asked. |
| `detail` | `lines` | `lines` counts lines; `files` downloads names only. |
| `maxLinesRepoMb` | 2000 | Bigger repositories are read in `files` detail. 0 is no limit. |
| `mirrorFolder` | empty | Where remote repositories are mirrored. |
| `mainlineBranches` | `main`, `master`, `develop`, `release/**`, … | Branch globs that count as mainline (`*` within one level, `**` across levels). |
| `activePeopleDays` / `activeReposDays` | 0 | The "still active" filters to begin with. |
| `hideBots` | true | Hide bot accounts (dependabot, renovate, build services). AI agents are not bots for this. |
| `mergeByName` | true | Treat identities that share a name as one person. |
| `aliases` | `{}` | One person under several names or addresses: shown name → the names and addresses that are them. |
| `excludedContributors` | `[]` | Names or emails left out of everything. |
| `excludePaths` | lock files, minified, generated, `node_modules`, `bin`, `obj`, … | Path globs whose changes are not counted. |
| `languages`, `frameworks`, `aiRules` | empty | Your own additions, as above. |
| `disabledRepositories` | `[]` | Repositories not read. |
| `sources` | `[]` | Where repositories come from, as above. |

## From the command line

```sh
avamux activity add github my-org
avamux activity add azure-devops my-org MyProject
avamux activity add local ~/code
avamux activity sources && avamux activity repos
avamux activity scan --wait                    # read everything now; status and stop also work
avamux activity summary --days 30
avamux activity top contributor --no-mainline --ai with
avamux activity top framework --from 2024-01-01 --to 2024-12-31 --repos-active 90
avamux activity top aitool --measure lines
```

`top` ranks by `contributor`, `repository`, `branch`, `language`, `filetype`, `framework`, `aitool`, `aimodel` or
`source`, and takes `--measure`, `--count`, `--days`, `--from` / `--to`, `--people-active`, `--repos-active`,
`--no-mainline` and `--ai all|with|without|agent`.
