# Keyboard shortcuts

Every key avamux itself handles, by where it works. Settings → Hotkeys shows the same list, filled in from your
settings, and its search box finds a shortcut by its keys or by what it does. On macOS ⌘ works wherever Ctrl is shown
for copy, paste, cut, select all and save, and in browser panes.

## Everywhere

| Keys | What they do |
|---|---|
| Ctrl+Shift+B | The boss screen, and back (`ui.bossKey`; see [The window](window.md#the-boss-screen)) |
| Esc | Leave the boss screen |
| Shift Shift (pressed twice) | The command palette (`ui.commandPaletteKey`; see [The window](window.md#the-command-palette)) |
| ↑ ↓, Enter, Esc | Command palette: choose, run, close |

## Terminals

Every other key goes to the program running in the tab.

| Keys | What they do |
|---|---|
| Ctrl+Shift+T | New terminal tab |
| Ctrl+Shift+W | Close the current tab (in a split tab: the pane you are typing in) |
| Ctrl+PageUp | Previous tab |
| Ctrl+PageDown | Next tab |
| Ctrl+Shift+C, Ctrl+Insert | Copy the selection |
| Ctrl+Shift+V, Shift+Insert | Paste |
| Ctrl+Shift+L | Open the browser beside this tab, or show and hide it |
| Ctrl+Click | Open a link in the browser beside the tab; a plain click opens the system browser (`browser.terminalLinksInPane` swaps them) |
| Enter / Esc | Renaming a tab (double-click it): keep the name / cancel |

## Browser panes

While the page has the keyboard.

| Keys | What they do |
|---|---|
| Ctrl+L | Go to the address bar |
| Enter | Address bar: go there |
| Esc | Address bar: put the address back and return to the page |
| F5, Ctrl+R | Reload |
| Shift+F5, Ctrl+Shift+R, Ctrl+F5 | Reload, ignoring the cache |
| Alt+Left, mouse back button | Back |
| Alt+Right, mouse forward button | Forward |
| Ctrl+=, Ctrl++ | Zoom in |
| Ctrl+- | Zoom out |
| Ctrl+0 | Actual size |
| Ctrl+Shift+C | Design mode: pick an element on the page |
| Esc | Stop picking |
| F12, Ctrl+Shift+I | Web Inspector (when `browser.developerTools` is on) |
| Ctrl+Shift+L | Hide the browser and go back to its terminal tab |
| Enter | A page's prompt dialog: OK |

## Wiki

| Keys | What they do |
|---|---|
| Ctrl+S | Save |
| Ctrl+V, Shift+Insert | Paste; a picture or a copied file is saved next to the page and linked |
| Ctrl+X / Ctrl+C / Ctrl+A | Cut / copy / select all |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Right-click | Undo, redo, cut, copy, paste, delete, select all (editor menu) |
| Alt+Left, mouse back button | Back through visited pages |
| Alt+Right, mouse forward button | Forward through visited pages |

### Wiki pictures

In the preview, while a picture is shown in its viewer or enlarged.

| Keys | What they do |
|---|---|
| + or = | Zoom in |
| - | Zoom out |
| 0 | Fit |
| 1 | Actual size |
| ← → ↑ ↓ | Move around |
| Esc | Close the enlarged picture or the menu |

## Search

| Keys | What they do |
|---|---|
| Enter | Search |
| Esc | Clear the search |
| ↓ | From the search box to the results |
| Enter | Open the selected result |

## C# Interactive

| Keys | What they do |
|---|---|
| Enter | Run (or a new line, while the statement is unfinished) |
| Ctrl+Enter | Always run |
| Shift+Enter | Always a new line |
| ↑ | Earlier line from the history (on the prompt's first line) |
| ↓ | Later line from the history (on the prompt's last line) |
| Esc | Empty the prompt |
| Ctrl+L | Empty the terminal |
| Ctrl+Enter | XAML tab: apply |

## AI Chat

| Keys | What they do |
|---|---|
| Enter | Send the message |
| Shift+Enter | New line in the message |
| Ctrl+V, Shift+Insert | Paste; copied pictures and files become attachments |
| Esc | Stop the reply being written |
| Ctrl+N | New chat |
| Enter | Model box: use the model name typed |
| Ctrl+Enter | Editing a message or reply: send or save it |
| Esc | Editing a message or reply: cancel |
| Esc | Close an enlarged picture |

## Repositories

| Keys | What they do |
|---|---|
| Esc | Merge tool: stop editing the result |

## Disk map

The window opened from [System](system.md).

| Keys | What they do |
|---|---|
| Enter | Open the selected folder or file |
| Backspace | Up one folder |
| Alt+Left, mouse back button | Back |
| Alt+Right, mouse forward button | Forward |
| F5 | Scan again |

## Theme editor

| Keys | What they do |
|---|---|
| Ctrl+S | Save the theme |
| Enter | Name, font or colour field: apply |
| Esc | Colour field: put it back |

## Settings

| Keys | What they do |
|---|---|
| Esc | Clear the settings search |
| Enter | Text setting: save it |
| Esc | Text setting: put it back |

## Other pages

| Keys | What they do |
|---|---|
| Enter | Caffeine: start the custom time |
| Enter | Dashboard: add the source |
| Enter | Watching: save the link |
