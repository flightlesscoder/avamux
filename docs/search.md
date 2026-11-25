# Search

One box over everything avamux knows about. Pick which sources to include, and click a result to open it where it
lives: a wiki page or a terminal at the matching line, an event in the calendar, a commit or file in its repository.

| Source | What is searched |
|---|---|
| Wiki | Page and file names, and the text of every page, with the matching line. |
| Tags | Wiki `#tags`. |
| Events | Titles, descriptions, locations and attachment names. |
| Watches | Names, kinds, summaries and links, and the items of the last check (title, detail, author). |
| Terminals | Tab titles, folders and commands, and the text in each terminal. |
| Repositories | Names and paths; with two letters or more, also commit messages (`git log --grep`), file names, and file contents (`git grep`). |
| Logs | File names and their lines. |
| Settings | Every setting's key and value. |

Matching ignores case. Sources are searched side by side, and each gives a limited number of results, so a broad
word does not bury the rest.

Enter searches, Esc clears the box, ↓ moves from the box to the results, and Enter opens the selected one.

From the command line:

```sh
avamux search "release notes"
avamux search "release notes" --source wiki --source events
avamux search standup --open          # show the results on the Search page
```

The Settings page has its own search box too, over every setting and the Hotkeys list. The
[command palette](window.md#the-command-palette) finds pages, actions and wiki pages by name from anywhere.
