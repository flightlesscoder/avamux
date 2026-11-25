# Dashboard

The Dashboard page shows your own HTML, CSS and JavaScript from the `dashboard/` folder in the data folder, with
`window.avamux` to run any avamux command from the page. The first start puts an example there (`index.html`,
`style.css`, `app.js`): cards for terminals, watches and the coming week's events, refreshed every five seconds. Edit
it, or replace it with anything.

## The page

- **Edit** opens the dashboard folder in `repositories.editor` (nvim to begin with), in a terminal tab.
- **Reload** loads it again after you change a file (`avamux dashboard reload`).
- **Developer tools** are on by default (`dashboard.developerTools`; `avamux dashboard devtools` opens them).
- A switch says whether page scripts may control avamux (`dashboard.allowAppControl`).
- `avamux dashboard browser` opens the same page in your usual web browser.

## Calling avamux from the page

avamux adds a script to every HTML page it serves from the folder, before `</head>`:

```js
// Run any command the CLI has; resolves to its result, as `--json` would print it.
const terminals = await avamux.command('terminal list');
await avamux.command('terminal new', { name: 'build', command: ['dotnet', 'watch'] });
await avamux.command('app nav', { page: 'terminals' });
const week = await avamux.command('event agenda', { days: 7 });

// Every command, with its parameters.
const all = await avamux.commands();

// Whether page scripts may control avamux (dashboard.allowAppControl).
if (!avamux.allowed) { /* show something read-only */ }

// The API is there from the start, but a script that loads early can wait for it:
window.addEventListener('avamux-ready', start);
```

Arguments are the command's options and parameters by name, as `avamux <command> /?` lists them; flags are `true`,
and a parameter that takes several words (a command and its arguments, say) is an array. A command that fails throws
an `Error` with avamux's message.

## Extra sources

Extra CSS and JS can be pulled into the dashboard and switched on and off without editing it: a URL, an absolute
file, or a path inside the dashboard folder.

```sh
avamux dashboard source add https://cdn.example.com/chart.js
avamux dashboard source add extra.css --disabled
avamux dashboard source list
avamux dashboard source enable <id>      # and disable, remove
avamux dashboard info                    # the folder, the address and the files
```

## Where it is served

The dashboard, like the terminals' and wiki preview's pages, is served by a small web server on
`http://127.0.0.1:<dashboard.port>` (47600; 0 picks a free port). It answers only on the loopback address and only
to loopback host names, and its command API needs a token that avamux puts into the pages it serves. A web page from
anywhere else cannot reach it.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `dashboard.port` | 47600 | Local port of avamux's web server. 0 picks a free one. |
| `dashboard.allowAppControl` | true | Let dashboard JavaScript run avamux commands. |
| `dashboard.developerTools` | true | The web developer tools for the page. |

The extra sources are kept in `dashboard.json` in the data folder.
