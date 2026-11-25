# Custom pages

Settings → Custom pages (or `avamux page …`) adds entries of your own to the navigation, each with a name and an
icon: one of the built-in icons by name (Settings → Custom pages lists them), or SVG path data for a 24×24 box.
Adding, changing, reordering and switching them takes effect at once; nothing needs a restart. They are kept in
`nav.json` in the data folder.

There are three kinds.

## Terminal

One command in a terminal of its own that fills the page: `btm`, `lazygit`, `lazydocker`,
`ssh -p 2222 -L 8080:localhost:80 me@host`. The line is run through your login shell, so your `PATH` and aliases
apply; an empty command starts the shell. When the program ends, the page offers to start it again, and does so by
itself the next time you go to the page.

```sh
avamux page add Monitor --kind terminal --command "btm" --icon pulse
avamux page add Git --kind terminal --command "lazygit" --cwd ~/code/project --icon source-branch
```

## Web

A web page (a mail client, a chat app, a monitoring board) with back, forward, reload and the developer tools.

The page gets a deliberately small `window.avamux`, which lets it draw attention to itself and nothing else. It
cannot run commands or read anything from avamux:

| Function | What it does |
|---|---|
| `avamux.needsInput(message)` | A dot on the page's navigation entry and a notification (unless the page is in view). |
| `avamux.clearNeedsInput()` | Takes the dot away. |
| `avamux.notify(title, message)` | A notification. |
| `avamux.setBadge(text)` | Up to four characters on the navigation entry; empty removes them. |

A page's **script** (JavaScript run after each load) is where to use it for sites that are not yours. This one shows
a web mail's unread count, from its title, on the navigation entry:

```sh
avamux page add Mail --kind web --url https://mail.example.com --icon email \
  --script "setInterval(() => avamux.setBadge((document.title.match(/\((\d+)\)/) || [])[1]), 3000)"
```

Two more options: `--inject-api false` leaves `window.avamux` out altogether, and `--links-in-browser true` opens
links that ask for a new window in your usual browser.

## Plugin

A page built by a .NET DLL. Implement `Avamux.Plugins.IPagePlugin` (project `src/Avamux.Plugin.Abstractions`):
`CreateView(IAvamuxHost host)` returns an Avalonia control. The class needs a public parameterless constructor.

```sh
dotnet build samples/page-plugin
avamux page add Sample --kind plugin --assembly "$PWD/samples/page-plugin/bin/Debug/net10.0/SamplePagePlugin.dll"
```

`samples/page-plugin/` is a page of sample controls, several of them calling avamux. After rebuilding a plugin,
reload it with the page's reload button or `avamux page reload <page>`. A DLL with more than one page needs
`--type <class name>`; a relative `--assembly` path is looked up in the plugins folder.

### The project

Reference Avalonia (the same version avamux is built with) and the abstractions, but ship neither: avamux supplies
both at run time.

```xml
<PropertyGroup>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.1.2" ExcludeAssets="runtime" />
  <ProjectReference Include="..\..\src\Avamux.Plugin.Abstractions\Avamux.Plugin.Abstractions.csproj"
                    Private="false" ExcludeAssets="runtime" />
</ItemGroup>
```

### What the host offers

Every member of `IAvamuxHost` may be called from any thread.

| Member | What it does |
|---|---|
| `CommandAsync(name, args)` | Runs any avamux command (`"terminal new"`, `"watch list"`…) and returns its JSON result. Throws with avamux's message when it fails. |
| `RunAsync("terminal", "new", "htop", "--name", "top")` | The same, written as on the command line. |
| `Commands()` | Every command with its help and parameters. |
| `Notify(title, message)`, `Navigate(page)` | A notification; switch page. |
| `RequestAttention(message)`, `ClearAttention()`, `SetBadge(text)` | The navigation dot and badge, as for web pages. |
| `GetSetting(path)`, `SetSetting(path, value)`, `SettingsChanged` | Read and change settings; hear when they change. |
| `ThemeColor(token)`, `ThemeChanged` | The theme's colours (`accent`, `bg`, `panel`, `text`…; see [Themes](themes.md#colour-tokens)); hear when the theme changes. |
| `Log(message)` | A line in the app log. |
| `Post(action)` | Runs an action on the UI thread. |
| `PageId`, `PageName`, `Version`, `DataDirectory` | Where it is running. |
| `Services` | avamux's own services (`Avamux.Core.AppServices`), for plugins that reference Avamux.Core and want more than commands. |

Implement `IPageVisibility` beside `IPagePlugin` to hear when the page comes into view and leaves it (to stop
polling while hidden, say). If the plugin or its control is `IDisposable`, it is disposed when the page is switched
off, removed or reloaded.

A plugin runs inside avamux with everything avamux can do open to it, so only load DLLs you trust. Plugins that add
to avamux without a page of their own are described in [Plugins](plugins.md).

## Attention and badges

A page that wants you (`avamux.needsInput`, `host.RequestAttention`, `avamux page attention <page> <message>`) gets a
dot on its navigation entry and a notification, unless it is the page you are looking at; the dot goes when you open
the page. `avamux page badge <page> <text>` shows up to four characters on the entry.

## From the command line

```sh
avamux page list
avamux page add <name> --kind terminal|web|plugin …
avamux page edit Mail --url https://mail.example.org     # a changed command, address or DLL starts it afresh
avamux page move Mail 1                                  # one place down the navigation (-1 is one up)
avamux page disable Mail && avamux page enable Mail
avamux page open Mail
avamux page reload Mail
avamux page attention Mail "New message"                # --clear removes the mark
avamux page badge Mail 3
avamux page remove Mail
```
