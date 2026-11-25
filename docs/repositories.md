# Repositories

The Repositories page shows changes, history, branches and pull requests for one configured git repository, or all
of them at once. Repositories are added on the page or with `avamux repo add <path>`, and kept in
`repositories.json` in the data folder; removing one from the list leaves its files alone.

## Changes

- Stage or unstage whole files, hunks (right-click the coloured strip beside the diff) or single lines (right-click a
  line number).
- See the diff, or the whole file with the changes marked.
- Commit what is staged.
- Open a file in `repositories.editor` (nvim to begin with) in a new terminal tab: `avamux repo edit <repo> <file>`.

When a merge, rebase, cherry-pick or revert stops for conflicts, the conflicted files are listed first and open in the
[merge tool](merge-tool.md).

## History and branches

- Inspect a commit file by file.
- Cherry-pick, revert, reset (soft, mixed, hard), check out a commit, tag.
- Create, merge, delete and switch branches.
- `repositories.logCount` (50) commits are listed to begin with.

## Pull requests

The Pull Requests tab shows the result of a [watch](watches.md) of kind `repo-pr` for that repository. So the list
refreshes on the watch's interval, notifies you when it changes, plays the theme's `pull-request` sound, and can be
managed on the Watching page like any other watch. `avamux repo prs <repo>` makes that watch on first use.

By default it runs `gh pr list`. Set `repositories.pullRequestCommand` (or the watch's own `command`) to any command
that prints a JSON array of pull requests, to use another forge or your own filtering:

| Field | |
|---|---|
| `number`, `title`, `state`, `isDraft`, `author`, `url`, `headRefName`, `baseRefName`, `updatedAt` | The pull request, as `gh pr list --json` gives it. |
| `mine` (optional) | Marks it as yours, for **Mine** and `--mine`. |
| `active` (optional) | Whether it is still open for review. Left out, a pull request is active when its `state` is open (`opened` and `active` count as open). Inactive ones are hidden unless `--all`. |

The command can also print the usual [watch result object](watches.md#script-watches). It runs in the repository's
folder with `AVAMUX_REPO_PATH` and `AVAMUX_REPO_NAME` set.

The watch's other settings: `limit` (how many pull requests `gh` fetches, 50) and `me` (your user name, to mark yours
when the command does not say; empty asks `gh`).

## The wiki

The wiki can keep its history in git, and opens in this same screen, in a window of its own. See
[The wiki in git](wiki.md#the-wiki-in-git).

## From the command line

```sh
avamux repo add . --name avamux
avamux repo list
avamux repo status                     # every repository; or name one
avamux repo changes avamux
avamux repo diff avamux src/App.cs --staged
avamux repo stage avamux src/App.cs && avamux repo commit avamux "Fix the thing"
avamux repo log avamux --count 20
avamux repo show avamux 52ea117 && avamux repo files avamux 52ea117
avamux repo branches avamux --local
avamux repo switch avamux feature/x --create
avamux repo prs avamux --mine          # --all for merged and closed, --refresh to check now
avamux repo remote avamux --open       # the remote's web page
avamux repo select avamux              # show it on the page ("all" for every repository)
```

## Settings

| Setting | Default | What it does |
|---|---|---|
| `repositories.editor` | `nvim` | The editor files open in, in a terminal tab. Also used by the merge tool's **Editor** button. |
| `repositories.logCount` | 50 | Commits listed in the history. |
| `repositories.pullRequestCommand` | empty (`gh pr list`) | Command that lists a repository's pull requests. |
