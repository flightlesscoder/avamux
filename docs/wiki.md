# Wiki

A folder of markdown pages (and any other files) with an editor, a live preview, links, tags, daily notes and its
own history in git. It lives in `wiki/` in the [data folder](configuration.md#the-data-folder); `avamux app paths`
prints where that is.

## Pages

- **Editor**: coloured to match the theme, with undo and redo. With the editor and the preview side by side,
  scrolling one scrolls the other to the same place (`wiki.syncScroll`).
- **Saving**: edits are saved automatically after `wiki.autoSaveSeconds` (2) without a change (`wiki.autoSave`), and
  Ctrl+S saves at once.
- **Preview**: live, with `[[Page]]` links, front matter, syntax-highlighted code blocks with a copy button, and
  pictures.
- **Own windows**: any entry can be opened in a window of its own.
- **Back and Forward**: the buttons, Alt+Left and Alt+Right, and the mouse's side buttons walk through the last 50
  pages and files viewed.
- **Scroll positions**: the last position in each of the `wiki.rememberedPositions` (500) most recent files is
  remembered, in `ui-state.json` in the data folder.

## Pictures and files

Paste a picture or drop any file onto a page: it is saved next to the page and linked.

**In the editor**, a page's pictures are shown too, each under the line that embeds it. That covers
`![alt](file.png)`, reference pictures and `<img>` tags, from the wiki, the web or `data:` addresses (not inside
code blocks), drawn at most `wiki.editorPictureHeight` (220) pixels tall. The text stays exactly as written: the
arrow keys step over a picture, and clicking one selects its markdown to cut, copy or delete. `wiki.editorPictures`
switches this off.

**In the preview**, click a picture to zoom and pan (`+`, `-`, `0` to fit, `1` for actual size, the arrow keys to
move, Esc to close). Right-click it for Show in Folder, Copy and Copy Path.

**Other files** in the wiki are previewed too:

| Kind | Shown as |
|---|---|
| Pictures, PDFs | The file itself. |
| Audio, video | A player. Nothing is loaded until it is played. |
| Text files | Text. |
| CSV, TSV | A table. |
| Zip archives | A listing. |
| Anything else | Opened in its default app. |

Pictures, PDFs and recordings over `wiki.clickToPreviewMegabytes` (20) wait behind a "Load preview" button; 0 loads
everything at once.

## Tags

Write `#tag` anywhere in a page, or `tags: [a, b]` in its front matter, to tag it. `/` nests tags:
`#avamux/browser` is under `#avamux`.

The **Tags** switch above the folder tree lists every tag with its page count, nested ones under their parent. Click
one for its pages: filter them, sort them by name or newest, include nested tags or not, and click a page to open it
at the tag's line. Tags in the preview are links to that list, and the editor colours them.

What does not count as a tag: anything in code, links and URLs, and headings (`# Title`).

The index is kept in `wiki-tags.json` in the data folder and follows changes made outside avamux too, so a wiki of
tens of thousands of pages starts in a fraction of a second.

[AI Actions](ai-actions.md) can suggest tags for a page, and `avamux wiki tags add <page> <tags…>` adds them from the
command line: into the front matter's `tags:` when the page has one, else onto a `Tags:` line at the end.

## Daily notes

The **Today** page (and `avamux wiki today`) opens today's note, creating it if needed.

| Setting | Default | What it does |
|---|---|---|
| `wiki.dailyNoteFormat` | `yyyy-MM-dd dddd` | .NET date format of the file name: `2026-11-12 Thursday.md`. |
| `wiki.dailyNoteFolder` | `Daily` | Folder inside the wiki. Empty is the wiki's top. |
| `wiki.dailyNoteTemplate` | `# {title}\n\n` | What a new note starts with. `{date}` and `{title}` are replaced. |

## Big wikis

Folders of more than 300 entries start closed and list their entries 500 at a time ("Show more"), so wikis of tens of
thousands of files stay quick. The load tests check startup over a 20,000-file wiki (see
[Development](development.md#load-tests)).

## The wiki in git

The wiki folder can be a git repository of its own. The **Git** button in the wiki's toolbar shows the branch and how
many files changed since the last commit, and opens a menu:

- **Start Keeping History with Git** runs `git init` in the wiki folder (only offered until it is a repository).
- **Commit All Changes…** stages everything and opens the commit dialog, the same one the Repositories page uses.
- **Review Changes…**, **History…** and **Branches…** open the wiki in the Repositories screen, in a window of its
  own and showing the wiki alone. Everything that screen does works here: stage files, hunks or lines; view a commit
  file by file; revert, cherry-pick, reset (soft, mixed, hard), check out a commit, tag; create, merge, delete and
  switch branches; resolve conflicts in the [merge tool](merge-tool.md).
- **Switch Branch** lists the branches and makes new ones.
- **History of *page*…** lists the commits that changed the open page and shows the page as each left it.
  **Restore This Version** puts the page back that way, as a change to commit, so nothing is lost.
- **Discard Changes to *page*…** puts the open page back the way the last commit has it.

Unsaved edits are saved before git looks, and when git changes files (a branch switch, a revert, a restore) the tree,
the editor and the preview follow.

Before a commit, avamux points out files that are big or not text (pictures excepted), because git keeps them
forever: `wiki.commitWarning`, with "big" meaning `wiki.commitWarningMegabytes` (5) or more.

From the command line the wiki is the repository called `wiki`:

```sh
avamux repo status wiki
avamux repo log wiki
avamux repo switch wiki drafts --create
avamux repo commit wiki "Notes from today"
avamux repo select wiki                      # opens its window
```

## From the command line

```sh
avamux wiki list
avamux wiki read "Notes/Ideas.md"
avamux wiki write "Inbox.md" "- [ ] call back"     # create or replace a page
make report | avamux wiki write "Reports/today.md" # the content from standard input
avamux wiki render "Inbox.md"                      # as HTML
avamux wiki search release notes
avamux wiki open "Inbox.md"                        # show it in the window
avamux wiki import ~/Pictures/diagram.png          # copy a file into the wiki
avamux wiki mkdir Archive && avamux wiki move "Old.md" "Archive/Old.md"
avamux wiki tags && avamux wiki tag avamux --exact # tags with page counts; the pages carrying one
avamux wiki today
```

## Settings

| Setting | Default | What it does |
|---|---|---|
| `wiki.autoSave` | true | Save automatically. |
| `wiki.autoSaveSeconds` | 2 | Seconds without a change before saving. |
| `wiki.syncScroll` | true | Scroll the editor and preview together. |
| `wiki.editorPictures` | true | Show pictures in the editor. |
| `wiki.editorPictureHeight` | 220 | Tallest an editor picture is drawn, in pixels. |
| `wiki.clickToPreviewMegabytes` | 20 | Bigger files wait for a click before previewing. |
| `wiki.rememberedPositions` | 500 | Files whose scroll position is remembered. |
| `wiki.commitWarning` | true | Warn about big or binary files before a commit. |
| `wiki.commitWarningMegabytes` | 5 | What counts as big. |

The daily note settings are [above](#daily-notes).
