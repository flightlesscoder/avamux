# Themes

Settings → Themes picks a theme, an accent colour and the window chrome: `system` (the desktop's title bar) or
`custom` (drawn in the theme's colours). Terminals, the wiki preview and the merge tool
follow the theme too. Terminals use the theme's colours unless `terminal.background` / `terminal.foreground` are
set.

## Built-in themes

| Id | Name | Look |
|---|---|---|
| `simple` | Simple | The original avamux look: quiet dark blue-greys with a blue accent. |
| `classic90s` | 90's Desktop Computer OS Classic | Grey raised panels, a navy title bar, square corners and beeps. Light. |
| `synthwave-kat` | Synthwave Kat | Deep violet with fuchsia and cyan neon. |
| `hot-pink-neon` | Hot Pink Neon | Near-black purple with hot pink and acid green. |
| `miami-retro` | Miami Retro | Indigo evening with pink, aqua and sunset orange. |
| `rose-gold-soft` | Rose Gold Soft | Warm plum with muted rose and gold. |

`avamux theme list` lists these and your theme files; `avamux theme set <id>` switches.

## Glow

`theme.glow` adds a neon glow around whatever is selected or current: list and tree selections, the active
navigation button and tab, the current terminal tab, today and the selected day in the calendar.
`theme.glowIntensity` (0 to 100) sets its strength and `theme.glowColor` its colour; empty follows the accent.

## Theme files

Themes can also be files: JSON or YAML, one theme per file, in the data folder's `themes/` folder (`avamux theme
folder` prints it). The file name is the theme's id, and a file named after a built-in theme (`simple.yaml`)
replaces it. Every key is optional; what a file leaves out comes from its `base` theme (Simple unless it names one):

```yaml
name: Synth Sunset
base: synthwave-kat     # start from another theme (built-in or file)
accent: "#ffaa00"       # recolours accent, links and selection from one colour
radius: 4               # corners; 0 is square
colors:                 # any colour token; colours must be in quotes in YAML
  bg: "#2a0f3d"
  cellSel: "#4a2060"
ansi:                   # 16 terminal colours as a list, or by name
  red: "#ff5577"
```

| Key | What it sets |
|---|---|
| `name`, `description` | What the theme is called, and a line about it. |
| `base` | Id of a theme to start from (default `simple`). Themes can be based on other files. |
| `light` | `true` for light controls. |
| `radius` | Corner radius, 0 to 30; 0 gives square corners. |
| `chrome` | `system` or `custom` title bars. |
| `sounds` | `soft`, `retro` or `synth`. |
| `bevel` | `true` for raised buttons and sunken fields, as in the 90's theme. |
| `font` | Comma-separated font families for the interface. |
| `derive` | `{ background, foreground, accent, link?, ansi? }`: build every colour from these. Handy for a new light or dark theme. |
| `accent` | One colour that recolours accent, links and selection. |
| `colors` | Any of the colour tokens below. |
| `ansi` | The 16 terminal colours as a list (black, red, green, yellow, blue, magenta, cyan, white, then the bright ones), or a map such as `red: "#e06c75"`. |

They apply in this order: `base`, then `derive`, then `accent`, then `colors` and `ansi`. `avamux theme help` prints
this list.

### Colour tokens

| Group | Tokens |
|---|---|
| Surfaces | `bg` (page), `bgDeep` (navigation bar), `bgEditor` (editors, lists), `bgSide` (side panels), `panel` (cards, toolbars, dialogs), `panelHi` (hovered panels, tooltips), `cell` (rows and cells), `cellSel` (selected row or cell) |
| Lines | `line` (borders), `lineSoft` (faint separators), `lineStrong` (emphasised borders, field outlines), `scroll` (scroll bar thumbs) |
| Text | `text`, `textStrong` (headings), `textSoft` (secondary), `textMute` (muted labels), `textCode`, `dim` (hints), `faint` (timestamps), `ghost` (placeholders, disabled) |
| Accent | `accent`, `link`, `linkHi`, `accentDeep` (filled areas), `onAccent` (text on the accent) |
| Status | `ok`, `okHi`, `warn`, `error`, `errorHi`, `errorText`, `purple` (tags, kinds), `cyan`, `orange` |
| Diffs | `addBg`, `delBg`, `hunkBg` |
| Terminal | `termBg`, `termFg`, `termSel` |
| Title bar | `titleBg`, `titleFg` (avamux-drawn window frames) |

The quickest start is a copy of an existing theme: `avamux theme show synthwave-kat --file mine.yaml` writes it in
full, every colour with a comment saying where it is used.

### Loading and checking

Files are re-read when they change on disk. One that cannot be read is listed under Settings → Themes with everything
that is wrong with it, and in the app log.

```sh
avamux theme validate mine.yaml        # every problem in a file, before copying it in
avamux theme import mine.yaml --use    # check it, copy it into the themes folder, and switch to it
avamux theme reload                    # re-read the folder and report problems
avamux theme delete mine               # a file that replaced a built-in brings the built-in back
```

## The theme editor

**Theme Editor…** (Settings → Themes, right-click a theme tile, or `avamux theme edit [id]`) edits a theme while
showing it: every change applies to the whole app straight away, terminals and the wiki preview included, and nothing
is written until Save (Ctrl+S). It has

- the colours, grouped and explained, each with a colour picker, a hex box and a button back to the saved colour;
- "Start from three colours", to rebuild everything from a background, text and accent colour;
- the theme file itself (YAML or JSON) on the **Theme File** tab, applied as you type, with problems listed under it;
- a preview screen that uses every colour: click any part of it to jump to its colour;
- a readability check that flags text whose contrast is too low.

Saving a built-in theme makes a copy (New Copy…). Import… and Export… move theme files in and out; Use This Theme
switches to it. Closing the editor with unsaved changes asks first and puts the chosen theme back.

## Sounds

Themes have sounds, played when

| Sound | Plays when | Switch |
|---|---|---|
| `event-start` | An event starts. | `theme.soundOnEvent` |
| `event-end` | An event ends. | `theme.soundOnEvent` |
| `timer` | A timer goes off. | `theme.soundOnTimer` |
| `watch-changed` | A watched item changes. | `theme.soundOnWatch` |
| `pull-request` | A pull request watch changes. | `theme.soundOnPullRequest` |
| `agent-waiting` | An AI agent in a terminal starts waiting for input, or a page asks for attention. | `theme.soundOnAgentWaiting` |

`theme.sounds` switches them all off, `theme.soundVolume` (0 to 100, 70) sets the volume, and `theme.soundSet` picks
the set: `theme` (the theme's own), `soft`, `retro` or `synth`. `avamux theme sound <sound>` plays one.

Files with those names (`.wav`, `.ogg`, `.flac`, `.mp3`) in the data folder's `sounds/custom/` replace the built-in
ones. Playback uses `pw-play`, `paplay` or `aplay` on Linux and `afplay` on macOS.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `theme.name` | `simple` | The theme. |
| `theme.accent` | empty | Accent colour as `#rrggbb`; empty uses the theme's own. |
| `theme.windowChrome` | `auto` | `auto` (the theme decides), `system` or `custom`. |
| `theme.glow`, `theme.glowIntensity`, `theme.glowColor` | off, 60, empty | The neon glow. |
| `theme.sounds`, `theme.soundVolume`, `theme.soundSet` | on, 70, `theme` | Sounds. |
| `theme.soundOn…` | all on | Each sound on its own. |
