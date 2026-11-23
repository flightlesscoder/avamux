using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core;
using Avamux.Core.Commands;
using Avamux.Core.Config;
using Avamux.Core.Logging;
using Avamux.Core.Nav;
using Avamux.Core.Terminal;
using Avamux.Plugins;

namespace Avamux.Views;

/// <summary>A page the user added to the navigation (Settings → Custom pages).</summary>
public interface ICustomPage : IPage, IDisposable
{
    /// <summary>Called when another page takes its place in the window.</summary>
    void OnHidden();
}

public static class CustomPages
{
    public static Control Create(CustomPage item) => item.Kind switch
    {
        CustomPage.Web => new CustomWebPage(item),
        CustomPage.Plugin => new CustomPluginPage(item),
        _ => new CustomTerminalPage(item),
    };

    /// <summary>The page's own icon, or one for its kind when it has none (or one that cannot be drawn).</summary>
    public static Geometry IconFor(CustomPage item) => Icons.Resolve(item.Icon) ?? DefaultIcon(item.Kind);

    public static Geometry DefaultIcon(string kind) => kind switch { CustomPage.Web => Icons.Web, CustomPage.Plugin => Icons.Puzzle, _ => Icons.Console };

    /// <summary>Marks a page as wanting the user, unless the user is looking at it already.</summary>
    public static void RequestAttention(Control page, CustomPage item, string? message) => Dispatcher.UIThread.Post(() =>
    {
        if (page.IsEffectivelyVisible && TopLevel.GetTopLevel(page) is Window { IsActive: true }) return;
        App.Services.Nav.RequestAttention(item.Id, message);
    });

    public static string ExpandHome(string path) =>
        path == "~" ? App.Services.Paths.HomeDir : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(App.Services.Paths.HomeDir, path[2..]) : path;

    /// <summary>A message in the middle of a page, with an optional button under it.</summary>
    public static Control Message(Geometry icon, string title, string detail, string? action = null, Action? onAction = null)
    {
        var column = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 640, Margin = new Thickness(24) };
        column.Children.Add(new PathIcon { Data = icon, Width = 36, Height = 36, Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Center });
        column.Children.Add(Ui.Apply(Ui.Text(title, "h2"), t => t.HorizontalAlignment = HorizontalAlignment.Center));
        column.Children.Add(new SelectableTextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Dim, FontSize = 12.5, TextAlignment = TextAlignment.Center });
        if (action is not null && onAction is not null) column.Children.Add(Ui.Apply(Ui.TextButton(action, onAction), b => b.HorizontalAlignment = HorizontalAlignment.Center));
        return column;
    }
}

/// <summary>One terminal of its own, running the page's command. Ending the program offers to start it again.</summary>
public sealed class CustomTerminalPage : UserControl, ICustomPage
{
    private readonly CustomPage _item;
    private readonly NativeWebView _web = new();
    private readonly Panel _termPanel = new();
    private readonly TextBlock _overlay = new() { Text = "Starting terminal engine…", Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _endedText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _ended;
    private readonly Action _config;
    private TerminalSession? _session;
    private string? _shownId;
    private bool _ready, _starting, _exited, _disposed;

    private static TerminalManager Manager => App.Services.Terminals;
    /// <summary>The session is kept out of the Terminals page's tabs by living in a group of its own.</summary>
    private string Group => "page:" + _item.Id;

    /// <summary>The custom page a terminal session belongs to, or null for an ordinary tab.</summary>
    public static string? PageKeyOf(string sessionId) =>
        TerminalLayout.GroupOf(sessionId) is { } g && g.StartsWith("page:", StringComparison.Ordinal) ? CustomPage.KeyPrefix + g[5..] : null;

    public CustomTerminalPage(CustomPage item)
    {
        _item = item;
        _ended = new Border
        {
            IsVisible = false, Background = Ui.Tk("panel"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 6),
            Child = new DockPanel { Children = { Ui.TextButton("Start again", Restart, "Run the command again").Dock(Dock.Right), _endedText } },
        };
        _termPanel.Children.Add(_web);
        _termPanel.Children.Add(_overlay);
        Content = new DockPanel { Children = { _ended.Dock(Dock.Top), _termPanel } };
        ApplyBackground();

        WebViews.FocusOnClick(_web);

        _web.EnvironmentRequested += (_, e) => WebViews.Configure(e);
        _web.WebMessageReceived += (_, e) => { if (e.Body?.Contains("\"ready\"", StringComparison.Ordinal) == true) _ = OnPageReadyAsync(); };
        _web.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) { _overlay.Text = "The terminal view failed to load. See Logs."; return; }
            _ = OnPageReadyAsync();
        };
        _web.Source = new Uri(App.Services.Web.TerminalUrl);

        _config = () => Dispatcher.UIThread.Post(() => { if (_disposed) return; ApplyBackground(); _ = PushOptionsAsync(); });
        App.Services.Config.Changed += _config;
        Ui.ThemeChanged += _config;
        AttachedToVisualTree += (_, _) => { if (TopLevel.GetTopLevel(this) is Window w) { w.Activated -= OnWindowActivated; w.Activated += OnWindowActivated; } };
        DetachedFromVisualTree += (_, _) => { if (TopLevel.GetTopLevel(this) is Window w) w.Activated -= OnWindowActivated; };

        Start();
    }

    private void ApplyBackground() => _termPanel.Background = Ui.Raw(App.Services.Web.TerminalOptions()["background"]!.GetValue<string>());

    private void Start()
    {
        try
        {
            string? command = null;
            List<string>? args = null;
            if (_item.Command.Length > 0)
            {
                // Through the user's login shell, so the command line may use anything a shell line can (quotes,
                // pipes, variables, ~) and finds programs on the PATH their profile sets up.
                command = TerminalManager.DefaultShell(App.Services.Config.Settings.Terminal);
                args = Path.GetFileName(command) is "bash" or "zsh" or "fish" ? ["-l", "-c", _item.Command] : ["-c", _item.Command];
            }
            var session = Manager.Create(new TerminalStartOptions(command, args, _item.Cwd.Length > 0 ? CustomPages.ExpandHome(_item.Cwd) : null, _item.Name));
            TerminalLayout.Move(session.Id, Group);
            session.Exited += OnExited;
            _session = session;
            _exited = false;
            _ended.IsVisible = false;
        }
        catch (Exception ex)
        {
            Log.Error("page", $"Could not start '{_item.Name}'", ex);
            _exited = true;
            _endedText.Text = $"Could not start: {ex.GetBaseException().Message}";
            _ended.IsVisible = true;
        }
    }

    private void OnExited(TerminalSession session, int code) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || !ReferenceEquals(session, _session)) return;
        _exited = true;
        _endedText.Text = $"{(_item.Command.Length > 0 ? _item.Command : "The shell")} ended (exit code {code}).";
        _ended.IsVisible = true;
    });

    /// <summary>Lets go of a session: ends it if it is running, and takes it off avamux's list if it stayed there.</summary>
    private static void Release(TerminalSession session)
    {
        void Forget() { if (Manager.Sessions.Any(s => ReferenceEquals(s, session))) { try { Manager.Close(session.Id); } catch { } } }
        if (session.Running) { session.Exited += (_, _) => Task.Run(Forget); try { session.Kill(); } catch { } }
        else Forget();
    }

    private void Restart()
    {
        if (_disposed) return;
        if (_session is { } old)
        {
            old.Exited -= OnExited;
            if (_ready) _ = Js($"avamux.remove({Q(old.Id)})");
            Release(old);
        }
        _session = null; _shownId = null;
        Start();
        if (_ready) _ = ShowAsync();
        if (IsEffectivelyVisible) FocusTerminal();
    }

    private static string Q(string s) => JsonSerializer.Serialize(s);

    private async Task<string?> Js(string script)
    {
        try { return await _web.InvokeScript("(function(){ var r = (" + script + "); return r == null ? '' : String(r); })()"); }
        catch (Exception ex) { Log.Warn("page", $"Script failed: {ex.Message}"); return null; }
    }

    private async Task OnPageReadyAsync()
    {
        if (_ready || _starting || _disposed) return;
        _starting = true;
        try
        {
            if (!await WebViews.WaitForAsync(_web, "typeof window.avamux === 'object'")) { _overlay.Text = "The terminal view did not start. See Logs."; return; }
            _ready = true;
            _overlay.IsVisible = false;
            await PushOptionsAsync();
            await ShowAsync();
            if (IsEffectivelyVisible) FocusTerminal();
        }
        finally { _starting = false; }
    }

    private Task PushOptionsAsync() => _ready ? Js($"avamux.options({App.Services.Web.TerminalOptions().ToJsonString()})") : Task.CompletedTask;

    private async Task ShowAsync()
    {
        if (!_ready || _session is not { } s || _shownId == s.Id) return;
        _shownId = s.Id;
        await Js($"avamux.show({Q(s.Id)})");
    }

    private void FocusTerminal()
    {
        WebViews.FocusNative(_web);
        if (_ready) _ = Js("avamux.focus()");
    }

    private void OnWindowActivated(object? sender, EventArgs e) { if (IsEffectivelyVisible) OnShown(); }

    public void OnShown()
    {
        // Coming back to a page whose program has ended starts it again: the page is the program.
        if (_exited) Restart();
        Dispatcher.UIThread.Post(() => { if (!_disposed && IsEffectivelyVisible) FocusTerminal(); }, DispatcherPriority.Background);
    }

    public void OnHidden() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        App.Services.Config.Changed -= _config;
        Ui.ThemeChanged -= _config;
        if (_session is { } s) { s.Exited -= OnExited; Release(s); }
        _session = null;
    }
}

/// <summary>
/// A web page in the window, with the developer tools available. The page gets a small window.avamux object that can
/// only draw attention to itself (see <see cref="ApiScript"/>); it cannot run avamux commands.
/// </summary>
public sealed class CustomWebPage : UserControl, ICustomPage
{
    private readonly CustomPage _item;
    private readonly NativeWebView _web = new();
    private readonly TextBlock _address = new() { Foreground = Ui.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0) };
    private DateTime _lastNotify;
    private bool _disposed;

    public CustomWebPage(CustomPage item)
    {
        _item = item;
        var home = new Uri(item.Url);
        var bar = new Border
        {
            BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8, 3),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Row(0,
                        Ui.IconButton(Icons.ArrowLeft, "Back", () => _web.GoBack()),
                        Ui.IconButton(Icons.ArrowRight, "Forward", () => _web.GoForward()),
                        Ui.IconButton(Icons.Refresh, "Reload the page", () => _web.Refresh()),
                        Ui.IconButton(Icons.Home, $"Go to {item.Url}", () => Ui.Try(this, () => _web.Navigate(home)))).Dock(Dock.Left),
                    Ui.Row(0,
                        Ui.IconButton(Icons.Bug, "Open the web developer tools", OpenDevTools),
                        Ui.IconButton(Icons.OpenInNew, "Open this address in the default browser", () => Shell.Open(_address.Text is { Length: > 0 } now ? now : item.Url))).Dock(Dock.Right),
                    _address,
                },
            },
        };
        Content = new DockPanel { Children = { bar.Dock(Dock.Top), _web } };
        _address.Text = item.Url;

        WebViews.FocusOnClick(_web);

        _web.EnvironmentRequested += (_, e) => e.EnableDevTools = true;
        _web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Request is not { } uri || uri.Scheme is not ("http" or "https")) return;
            // Following the link in place keeps sign-in pop-ups working; the Back button returns.
            if (_item.LinksInBrowser) Shell.Open(uri.ToString()); else _web.Navigate(uri);
        };
        _web.NavigationCompleted += (_, e) =>
        {
            if (e.Request is { } at) _address.Text = at.ToString();
            if (e.IsSuccess) _ = InjectAsync();
        };
        _web.WebMessageReceived += OnMessage;
        _web.Source = home;
    }

    private void OpenDevTools()
    {
        if (!WebViews.OpenDevTools(_web)) Ui.Toast(this, "Developer tools", "Right-click the page and choose \"Inspect Element\".");
    }

    /// <summary>
    /// The object a web page is given. It only sends four kinds of message, and <see cref="OnMessage"/> acts on
    /// nothing else, so a page (or anything it loads) can at most mark itself and show a notification.
    /// </summary>
    private string ApiScript() => """
        (function () {
          if (window.avamux) return;
          var send = function (o) { try { invokeCSharpAction(JSON.stringify(o)); } catch (e) { } };
          var text = function (v) { return v == null ? '' : String(v); };
          window.avamux = Object.freeze({
            limited: true,
            version: __VERSION__,
            page: Object.freeze({ id: __ID__, name: __NAME__ }),
            needsInput: function (message) { send({ avamux: 'attention', message: text(message) }); },
            clearNeedsInput: function () { send({ avamux: 'clear' }); },
            notify: function (title, message) { send({ avamux: 'notify', title: text(title), message: text(message) }); },
            setBadge: function (value) { send({ avamux: 'badge', text: text(value) }); }
          });
        })();
        """.Replace("__VERSION__", JsonSerializer.Serialize(typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? ""))
           .Replace("__ID__", JsonSerializer.Serialize(_item.Id)).Replace("__NAME__", JsonSerializer.Serialize(_item.Name));

    private async Task InjectAsync()
    {
        try
        {
            if (_item.InjectApi) await _web.InvokeScript(ApiScript() + " ''");
            if (_item.Script.Trim().Length > 0)
                await _web.InvokeScript("(function () { try {\n" + _item.Script + "\n} catch (e) { console.error('avamux page script failed', e); } })(); ''");
        }
        catch (Exception ex) { Log.Warn("page", $"Could not prepare '{_item.Name}': {ex.Message}"); }
    }

    private void OnMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_disposed || !_item.InjectApi) return;
        try
        {
            // Whatever the page sends is untrusted: only these verbs, and only short text.
            if (e.Body is not { Length: > 0 and < 4000 } body || JsonNode.Parse(body) is not JsonObject m) return;
            string Text(string key, int max) => m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Length > max ? s[..max] : s : "";
            switch (Text("avamux", 20))
            {
                case "attention": CustomPages.RequestAttention(this, _item, Text("message", 200)); break;
                case "clear": App.Services.Nav.ClearAttention(_item.Id); break;
                case "badge": App.Services.Nav.SetBadge(_item.Id, Text("text", 4)); break;
                case "notify":
                    if (DateTime.UtcNow - _lastNotify < TimeSpan.FromSeconds(3)) break;
                    _lastNotify = DateTime.UtcNow;
                    var title = Text("title", 80);
                    App.Services.Host.Notify(title.Length > 0 ? $"{_item.Name}: {title}" : _item.Name, Text("message", 300));
                    break;
            }
        }
        catch { /* not a message for us */ }
    }

    public void OnShown() => Dispatcher.UIThread.Post(() => { if (!_disposed && IsEffectivelyVisible) WebViews.FocusNative(_web); }, DispatcherPriority.Background);
    public void OnHidden() { }
    public void Dispose() => _disposed = true;
}

/// <summary>A page built by a .NET DLL that implements <see cref="IPagePlugin"/>.</summary>
public sealed class CustomPluginPage : UserControl, ICustomPage
{
    private readonly CustomPage _item;
    private PluginHost? _host;
    private IPagePlugin? _plugin;
    private object? _view;

    public CustomPluginPage(CustomPage item)
    {
        _item = item;
        var path = CustomPages.ExpandHome(item.Assembly);
        if (!Path.IsPathRooted(path)) path = Path.Combine(App.Services.Paths.PluginsDir, path);
        try
        {
            _plugin = PagePluginLoader.Load(path, item.Type);
            _host = new PluginHost(item, this);
            _view = _plugin.CreateView(_host);
            Content = _view as Control ?? throw new InvalidOperationException(
                $"{_plugin.GetType().FullName}.CreateView returned {(_view is null ? "nothing" : _view.GetType().FullName)}; it has to return an Avalonia Control. " +
                "If it did return one, the plugin brought its own copy of Avalonia: reference Avalonia without copying it next to the plugin.");
            Log.Info("page", $"Loaded plugin page '{item.Name}' ({_plugin.GetType().FullName} from {path})");
            Avamux.Core.Diagnostics.Timeline.Note("plugin page loaded", item.Name);
        }
        catch (Exception ex)
        {
            var problem = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            Log.Error("page", $"Plugin page '{item.Name}' could not be loaded from {path}", problem);
            Release();
            Content = CustomPages.Message(Icons.Puzzle, $"{item.Name} could not be loaded", $"{problem.Message}\n\n{path}", "Try again", () => App.Services.Nav.Reload(item.Id));
        }
    }

    private void Release()
    {
        foreach (var o in new[] { _view, _plugin, _host })
            if (o is IDisposable d) { try { d.Dispose(); } catch (Exception ex) { Log.Warn("page", $"Plugin page '{_item.Name}' failed while closing: {ex.Message}"); } }
        _view = null; _plugin = null; _host = null;
    }

    private void Tell(Action<IPageVisibility> what)
    {
        try { if (_plugin is IPageVisibility p) what(p); else if (_view is IPageVisibility v) what(v); }
        catch (Exception ex) { Log.Error("page", $"Plugin page '{_item.Name}' failed", ex); }
    }

    public void OnShown() => Tell(p => p.OnShown());
    public void OnHidden() => Tell(p => p.OnHidden());
    public void Dispose() { Content = null; Release(); }
}

/// <summary>Loads page plugins. Each load gets a context of its own, so a rebuilt DLL can be loaded again.</summary>
internal static class PagePluginLoader
{
    public static IPagePlugin Load(string path, string type)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"Plugin DLL not found: {full}");
        var context = new Context(full);
        // From memory rather than from the file, so the file is not held open and can be rebuilt while avamux runs.
        Assembly assembly;
        using (var dll = new MemoryStream(File.ReadAllBytes(full)))
        {
            var symbols = Path.ChangeExtension(full, ".pdb");
            if (File.Exists(symbols)) { using var pdb = new MemoryStream(File.ReadAllBytes(symbols)); assembly = context.LoadFromStream(dll, pdb); }
            else assembly = context.LoadFromStream(dll);
        }
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { throw new InvalidOperationException($"{Path.GetFileName(full)} could not be read: {ex.LoaderExceptions.FirstOrDefault(e => e is not null)?.Message ?? ex.Message}"); }
        var pages = types.Where(t => typeof(IPagePlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToList();
        if (pages.Count == 0) throw new InvalidOperationException($"{Path.GetFileName(full)} has no class that implements Avamux.Plugins.IPagePlugin.");
        var chosen = type.Length == 0 ? pages[0]
            : pages.FirstOrDefault(t => t.Name == type || t.FullName == type)
              ?? throw new InvalidOperationException($"No page '{type}' in {Path.GetFileName(full)}. It has: {string.Join(", ", pages.Select(t => t.FullName))}.");
        return (IPagePlugin)Activator.CreateInstance(chosen)!;
    }

    private sealed class Context(string path) : AssemblyLoadContext("page plugin " + Path.GetFileName(path), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        private readonly string _folder = Path.GetDirectoryName(path)!;

        protected override Assembly? Load(AssemblyName name)
        {
            // Anything avamux has itself (the plugin contract, Avalonia, Avamux.Core, the framework) comes from
            // avamux, so the types are the same ones on both sides. Returning null hands the request to it.
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"))) return null;
            var found = _resolver.ResolveAssemblyToPath(name);
            if (found is null && Path.Combine(_folder, name.Name + ".dll") is var beside && File.Exists(beside)) found = beside;
            return found is null ? null : LoadFromAssemblyPath(found);
        }
    }
}

/// <summary>What a page plugin sees of avamux.</summary>
internal sealed class PluginHost : IAvamuxHost, IDisposable
{
    private readonly CustomPage _item;
    private readonly Control _page;
    private readonly Action _settingsChanged, _themeChanged;

    public PluginHost(CustomPage item, Control page)
    {
        _item = item; _page = page;
        _settingsChanged = () => Dispatcher.UIThread.Post(() => SettingsChanged?.Invoke());
        _themeChanged = () => ThemeChanged?.Invoke();
        App.Services.Config.Changed += _settingsChanged;
        Ui.ThemeChanged += _themeChanged;
    }

    public string PageId => _item.Id;
    public string PageName => _item.Name;
    public string Version => typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "";
    public string DataDirectory => App.Services.Paths.DataDir;
    public object Services => App.Services;

    public event Action? SettingsChanged;
    public event Action? ThemeChanged;

    public async Task<JsonNode?> CommandAsync(string name, JsonObject? args = null)
    {
        var response = await App.Services.Commands.ExecuteAsync(name, args ?? new JsonObject());
        if (response["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? $"'{name}' failed.");
        return response["result"]?.DeepClone();
    }

    public Task<JsonNode?> RunAsync(params string[] argv)
    {
        var parsed = CliParser.Parse(App.Services.Commands, argv);
        if (parsed.Error is not null) throw new InvalidOperationException(parsed.Error);
        if (parsed.Command is null) throw new InvalidOperationException("No command given.");
        return CommandAsync(parsed.Command.Name, parsed.Args);
    }

    public JsonArray Commands() => App.Services.Commands.Describe();
    public void Notify(string title, string message = "") => App.Services.Host.Notify(title, message);
    public void Navigate(string page) => App.Services.Host.Navigate(page);
    public void RequestAttention(string? message = null) => CustomPages.RequestAttention(_page, _item, message);
    public void ClearAttention() => App.Services.Nav.ClearAttention(_item.Id);
    public void SetBadge(string? text) => App.Services.Nav.SetBadge(_item.Id, text);

    public string? GetSetting(string path)
    {
        if (ConfigService.Canonical(path) is not { } key) return null;
        var value = App.Services.Config.Get(key);
        return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString();
    }

    public void SetSetting(string path, string value)
    {
        var key = ConfigService.Canonical(path) ?? throw new ArgumentException($"Unknown setting '{path}'.");
        App.Services.Config.Set(key, ConfigService.Coerce(key, value));
    }

    public string ThemeColor(string token)
    {
        try { return Ui.Theme[token]; } catch (KeyNotFoundException) { return Ui.Theme["text"]; }
    }

    public void Log(string message) => Avamux.Core.Logging.Log.Info("plugin", $"{_item.Name}: {message}");
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        App.Services.Config.Changed -= _settingsChanged;
        Ui.ThemeChanged -= _themeChanged;
        SettingsChanged = null; ThemeChanged = null;
    }
}
