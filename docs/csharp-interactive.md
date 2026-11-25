# C# Interactive

A C# prompt inside the running app, for trying things against the live avamux. On the left is a terminal: type C# and
press Enter. On the right is a screen of live controls that the code (and a XAML document) builds, and that you can
click.

```csharp
> new [] { "a", "b", "c" }.ToList().Select(x => x)
IEnumerable<string> { "a", "b", "c" }
> var b = Add(new Button { Content = "Hello" });
> b.Click += (_, _) => Print("clicked");                 // clicking the button writes to the terminal
> avamux.Terminals.Sessions.Select(t => t.Title)
> await avamux.Command("terminal new", new { name = "build" })
> await Task.Delay(1000); DateTime.Now                   // await works anywhere
```

It is the quickest way to find out what a plugin could do, to prototype a control before putting it in a page
plugin, or to poke at avamux's state while working on it.

## The prompt

- **Values**: an expression without a semicolon shows its value, laid out as a REPL does: lists, dictionaries,
  tuples, records and anonymous objects spelled out, JSON indented.
- **State**: variables, `using`s, methods and classes stay for later lines. `Vars()` lists the variables, `Help()`
  everything else. **Reset** forgets every variable and builds the examples again.
- **Several lines**: Enter continues a statement that is not finished (an open brace, a missing semicolon),
  Shift+Enter always starts a new line, Ctrl+Enter always runs. Pasted code keeps its lines.
- **History**: Up and Down walk it; Esc empties the prompt; Ctrl+L empties the terminal.
- **Async**: `await` anything. The window stays responsive while awaited code waits. A task left un-awaited is shown
  as running, and its result is printed when it finishes. **Stop** gives the prompt back and cancels the `Cancel`
  token: `await Task.Delay(60000, Cancel)`.
- **Output**: `Print(value)` and `Console.WriteLine` write to the terminal, also from event handlers and other
  threads. An exception thrown later by a handler is shown there too.

## The `avamux` variable

`avamux` is the running app:

| Member | What it is |
|---|---|
| `Terminals`, `Wiki`, `Events`, `Watches`, `Repos`, `Search`, `Logs`, `Caffeine`, `Nav`, `Themes` | The services behind each page. |
| `Settings`, `Config` | The current settings, and the service that reads and writes them. |
| `Paths` | Where data and configuration live. |
| `Host` | The window: pages, notifications, the selected terminal. |
| `Services` | Everything, for what the rest does not reach. |
| `Version` | avamux's version. |
| `Command(name, new { option = value })` | Runs any CLI command and returns its JSON result. |
| `Run("terminal", "list")` | The same, with the arguments as on the command line. |
| `Commands()` | Every command's name. |
| `Notify(title, text)`, `Navigate(page)` | Raise a notification; switch page. |

## The screen

- `Add(control)` puts a control on the screen and returns it.
- `Xaml("<Button Content='Hi'/>")` builds one from XAML text.
- `Find<Button>("name")` gets one by its `Name`.
- `Screen` is the panel itself.

The **XAML** tab holds a document the top of the screen is built from (Apply, or Ctrl+Enter). In it,
`i:On.Click='Print("clicked")'` runs C# from the session when the control is clicked.

The page starts with a few working examples, shown in the terminal as the code that made them: a button that prints a
message, one that raises an avamux notification, one with an async handler, a slider bound to a label, and the same
from XAML.

## What to keep in mind

Code runs on the UI thread, so it can touch controls, and so a loop that never awaits freezes the window until it
ends: await, or use `Task.Run`, for anything long.

It runs with everything avamux can do. It is your own machine and your own code, like a [plugin](plugins.md).
