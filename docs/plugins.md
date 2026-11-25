# Plugins

A plugin is a .NET class library that adds to avamux without necessarily adding a page: right-click menu items (with
submenus) in the wiki editor, the wiki preview, the wiki's folder tree and terminals; watch kinds; AI chat providers;
AI workload sources and leases; a settings panel of its own. [AI Actions](ai-actions.md) is one, and ships with avamux.

There are three interfaces, all in `src/Avamux.Plugin.Abstractions`:

| Interface | What it adds | More |
|---|---|---|
| `IAvamuxPlugin` | Anything below, registered when it is switched on. | This page. |
| `IPagePlugin` | A page in the navigation. | [Custom pages](custom-pages.md#plugin) |
| `IWatchProvider` | A kind of watch, on its own or from an `IAvamuxPlugin`. | [Watching](watches.md#plugin-watches) |

Plugins are fully trusted: they run inside avamux with everything it can do. Only load DLLs you trust.

## Writing one

Implement `Avamux.Plugins.IAvamuxPlugin`. The class needs a public parameterless constructor.

```csharp
public sealed class WordCount : IAvamuxPlugin, IMenuContributor
{
    private IPluginContext? _context;

    public string Id => "word-count";                       // stable, lower case with dashes
    public string Name => "Word count";
    public string Description => "Counts the words in the selection.";

    public void Activate(IPluginContext context)
    {
        _context = context;
        context.AddMenuItems(this);
    }

    public void Deactivate() => _context = null;            // registrations are taken back by avamux

    public IEnumerable<PluginMenuItem> Items(MenuContext menu)
    {
        if (_context is { } ctx && menu.SelectedText.Length > 0)
            yield return PluginMenuItem.Command("Count Words", () =>
                ctx.Toast("Words selected", menu.SelectedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length.ToString()));
    }
}
```

`samples/menu-plugin/` is a complete pageless plugin: a submenu in the wiki editor (change case, insert the date,
count words), an item in terminals' menus, and a watch kind. Its project file shows how to reference Avalonia and the
abstractions without shipping them (see [Custom pages](custom-pages.md#the-project)).

### Activate and Deactivate

`Activate(IPluginContext context)` is called when avamux starts or the plugin is switched on. Register what the plugin
adds there:

| Registration | Adds |
|---|---|
| `AddMenuItems(IMenuContributor)` | Right-click menu items. |
| `AddWatchProvider(IWatchProvider)` | A watch kind. |
| `AddAiProvider(IAiProvider)` | A kind of AI Chat provider. |
| `AddWorkloadSource(IWorkloadSource)` | Workloads from somewhere else (another machine's queue, its models and jobs). See [AI Workloads](ai-workloads.md#for-plugins). |
| `AddLeaseProvider(IWorkloadLeaseProvider)` | Leases a profile must get before it starts. |
| `AddSettingsView(Func<object>)` | A panel under Settings → Plugins. |

`Deactivate()` is called when the plugin is switched off, reloaded, or avamux quits. avamux takes every registration
back itself, so `Deactivate` only has to stop what the plugin started on its own: timers, processes.

### Menus

An `IMenuContributor` is asked for its items every time a menu opens, so items can follow the selection. It gets a
`MenuContext`:

| Member | What it is |
|---|---|
| `Menu` | Which menu: `Menus.WikiEditor`, `Menus.WikiPreview`, `Menus.WikiTree` (a page, file or folder in the tree or the Tags view) or `Menus.Terminal`. |
| `WikiPath`, `IsFolder` | The wiki page, file or folder the menu is about (wiki-relative, `/` separated), if any, and whether it is a folder. |
| `SelectedText` | The selected text in the editor, preview or terminal; empty when nothing is selected. |
| `TerminalId` | In terminal menus, the terminal (pane) the menu was opened in. |
| `Editor` | In wiki editor and preview menus, the open page as an `IWikiEditor`. |

`IWikiEditor` (call it from the UI thread) reads the page as it is, unsaved edits included (`Path`, `Text`,
`SelectedText`), and changes it, each change one undo step:

- `ReplaceSelection(text)`: replaces the selection, or inserts at the caret when nothing is selected;
- `ReplaceText(text)`: replaces the whole page, keeping the scroll position;
- `InsertAfterSelection(text)`: inserts on a new line after the selection or the caret's line;
- `Save()`: saves the page now.

`IsReadOnly` is true for files that cannot be edited (pictures, big files) and while the page is read only.

A `PluginMenuItem` is a command (`PluginMenuItem.Command(header, onClick, icon)`), a submenu
(`PluginMenuItem.Submenu(header, children, icon)`) or a separator. Icons are named as in Settings → Custom pages
(`robot`, `tag`, `sparkles`…). `OnClick` runs on the UI thread; an exception it throws is shown to the user and
logged.

### What the context offers

| Member | What it does |
|---|---|
| `CommandAsync(name, args)`, `RunAsync(argv…)` | Run any avamux command, as the CLI does. |
| `Notify(title, message)`, `Toast(title, message)` | A notification; a small passing message. |
| `Navigate(page)` | Switch page. |
| `GetSetting(path)`, `SetSetting(path, value)` | Read and change settings. |
| `ThemeColor(token)` | A colour of the theme (see [Themes](themes.md#colour-tokens)). |
| `Log(message)` | A line in the app log. |
| `Post(action)` | Run on the UI thread. |
| `PromptAsync`, `ConfirmAsync` | Ask for a line of text, or a yes or no. |
| `ShowWindow(title, content, width, height)` | Show an Avalonia control in a themed window of avamux's. |
| `SpeakAsync(text)` | Read text aloud with [Kokoro](ai-chat.md#speaking-replies-kokoro), asking to switch it on first if it is off. |
| `Ai` | avamux's AI providers: run a prompt to the end (`RunAsync`, with the reply streaming in), or open a conversation in AI Chat (`OpenChatAsync`). |
| `PluginId`, `Version`, `WikiDirectory` | Where it is running. |
| `DataDirectory` | A folder for the plugin's own files: `plugin-data/<id>/` in the data folder. |
| `Services` | avamux's own services (`Avamux.Core.AppServices`), for plugins that reference Avamux.Core. This is where [AI Workloads' hooks](ai-workloads.md#for-plugins) are. |

The window and question members are for the UI thread.

## Installing and managing

**Settings → Plugins** lists every plugin: the built-in ones (AI Actions), those in the plugins folder (each in a
folder of its own, or a single DLL as watch plugins were before), and page plugins from Custom pages. It shows what
each adds, and switches them on and off, reloads them after a rebuild, installs them (Install… copies the DLL and what
it needs into `plugins/<id>/`) and uninstalls them.

Switching one off takes what it added away at once. .NET cannot unload an assembly, so its code stays in memory until
avamux restarts; the list says so. Which plugins are off is kept in `plugins.json` in the data folder.

```sh
dotnet build samples/menu-plugin
avamux plugin install samples/menu-plugin/bin/Debug/net10.0/SampleMenuPlugin.dll
avamux plugin list
avamux plugin show sample-menu
avamux plugin disable sample-menu && avamux plugin enable sample-menu
avamux plugin reload sample-menu      # after copying a new build over plugins/sample-menu/
avamux plugin uninstall sample-menu
```

## Keeping your own setup out of avamux

Plugins are also the place for anything specific to your own machines: how your model servers' start scripts are
written, which containers count as AI workloads, what to do after a server starts. avamux has generic hooks for these
(see [AI Workloads](ai-workloads.md#for-plugins)), so a private plugin can carry the details while avamux stays
general.
