using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core.Logging;
using Avamux.Browser;
using Avamux.Core.Terminal;

namespace Avamux.Views;

/// <summary>
/// Which window each terminal tab lives in. A tab with no entry is in the main window; detaching a tab gives it
/// the key of a <see cref="TerminalWindow"/>. Sessions themselves are owned by the core and never move.
/// </summary>
public static class TerminalLayout
{
    private static readonly Dictionary<string, string> Groups = new();
    private static readonly Dictionary<string, TerminalWindow> Windows = new();
    private static int _next = 1;

    public static event Action? Changed;

    public static string? GroupOf(string sessionId) => Groups.GetValueOrDefault(sessionId);
    public static IReadOnlyCollection<TerminalWindow> OpenWindows => Windows.Values;
    public static TerminalWindow? WindowOf(string sessionId) => GroupOf(sessionId) is { } g ? Windows.GetValueOrDefault(g) : null;

    public static void Move(string sessionId, string? group)
    {
        if (group is null) Groups.Remove(sessionId); else Groups[sessionId] = group;
        Changed?.Invoke();
    }

    /// <summary>Opens a new terminal window and moves a tab into it (or starts a new terminal there).</summary>
    public static TerminalWindow OpenWindow(string? sessionId, Window? near = null)
    {
        var key = "w" + _next++;
        if (sessionId is not null) Groups[sessionId] = key;
        var window = new TerminalWindow(key);
        Windows[key] = window;
        window.Closed += (_, _) =>
        {
            // Closing the window puts its tabs back in the main window; it does not end them.
            Windows.Remove(key);
            foreach (var id in Groups.Where(g => g.Value == key).Select(g => g.Key).ToList()) Groups.Remove(id);
            Changed?.Invoke();
        };
        if (near is not null) { window.Width = Math.Max(640, near.Width * 0.7); window.Height = Math.Max(420, near.Height * 0.7); }
        window.Show();
        Changed?.Invoke();
        if (sessionId is null) window.Page.NewTerminal(); else window.Page.Select(sessionId);
        return window;
    }

    public static void Forget(string sessionId) => Groups.Remove(sessionId);
}

/// <summary>A separate window holding detached terminal tabs.</summary>
public sealed class TerminalWindow : AppWindow
{
    public TerminalWindow(string group)
    {
        Group = group;
        Title = "avamux terminals";
        Icon = AppIcon.Window;
        Width = 980; Height = 640; MinWidth = 420; MinHeight = 260;
        Page = new TerminalsPage(group);
        Body = Page;
        Page.Emptied += Close;
        // Browser panes beside its tabs go back with them; their pages must not go with the window.
        Closing += (_, _) => Page.ReleaseBrowser();
    }

    public string Group { get; }
    public TerminalsPage Page { get; }
}

/// <summary>Tabbed terminals. Tabs are native controls; the terminals themselves are xterm.js in one web view.</summary>
public sealed class TerminalsPage : UserControl, IPage
{
    private readonly string? _group;
    private readonly NativeWebView _web = new();
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Panel _termPanel = new();
    private readonly TextBlock _overlay = new()
    {
        Text = "Starting terminal engine…", Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly BrowserDock _dock = new() { IsVisible = false };
    private readonly GridSplitter _splitter = new() { Width = 5, ResizeDirection = GridResizeDirection.Columns, IsVisible = false, Background = Brushes.Transparent };
    private readonly Grid _split = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
    private readonly Button _globe;
    private readonly HashSet<string> _opened = [];
    private bool _ready, _hadTabs;
    private string? _editingId;
    private TerminalManager Manager => App.Services.Terminals;

    public string? SelectedId { get; private set; }
    /// <summary>Raised when the last tab of this view has gone (used by detached windows to close themselves).</summary>
    public event Action? Emptied;

    /// <param name="group">Null for the main window; otherwise the key of the detached window this view belongs to.</param>
    public TerminalsPage(string? group = null)
    {
        _group = group;
        var add = Ui.IconButton(Icons.Plus, "New terminal (Ctrl+Shift+T)", () => NewTerminal());
        var detach = Ui.IconButton(Icons.OpenInNew, group is null ? "Move this tab to a new window" : "Move this tab back to the main window", () =>
        {
            if (SelectedId is not { } id) return;
            if (_group is null) TerminalLayout.OpenWindow(id, TopLevel.GetTopLevel(this) as Window); else TerminalLayout.Move(id, null);
        });
        _globe = Ui.IconButton(Icons.Web, "Browser beside this tab (Ctrl+Shift+L)", ToggleBrowser);
        var strip = new Border
        {
            Background = Ui.Tk("bgDeep"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 4, 6, 0),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Row(0, _globe, add, detach).Dock(Dock.Right),
                    new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _tabs },
                },
            },
        };
        _termPanel.Children.Add(_web);
        _termPanel.Children.Add(_overlay);
        _split.ColumnDefinitions[0].MinWidth = 200;
        _split.Children.Add(_termPanel.At(0, 0));
        _split.Children.Add(_splitter.At(0, 1));
        _split.Children.Add(_dock.At(0, 2));
        _splitter.DragCompleted += (_, _) =>
        {
            App.Services.Ui.Data.BrowserDockWidth = Math.Round(_split.ColumnDefinitions[2].ActualWidth);
            App.Services.Ui.SaveSoon();
        };
        Content = new DockPanel { Children = { strip.Dock(Dock.Top), _split } };
        ApplyBackground();

        WebViews.FocusOnClick(_web);

        _web.EnvironmentRequested += (_, e) => WebViews.Configure(e);
        _web.WebMessageReceived += OnWebMessage;
        _web.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) { _overlay.Text = "The terminal view failed to load. See Logs."; Log.Error("terminal", "Terminal page failed to load"); return; }
            _ = OnPageReadyAsync();
        };
        _web.Source = new Uri(App.Services.Web.TerminalUrl);

        var refresh = Ui.Debounced(RebuildTabs, 30);
        Action<TerminalSession> closed = s => Dispatcher.UIThread.Post(() => OnClosed(s));
        Action config = () => Dispatcher.UIThread.Post(() => { ApplyBackground(); _ = PushOptionsAsync(); });
        Manager.Changed += refresh;
        Manager.Closed += closed;
        TerminalLayout.Changed += refresh;
        var browsers = Ui.Debounced(UpdateDock, 20);
        BrowserHost.Instance.Changed += browsers;
        App.Services.Config.Changed += config;
        Ui.ThemeChanged += config;
        // A detached window goes away; the main page lives as long as the app.
        DetachedFromVisualTree += (_, _) =>
        {
            if (_group is null) return;
            Manager.Changed -= refresh; Manager.Closed -= closed; TerminalLayout.Changed -= refresh; App.Services.Config.Changed -= config; Ui.ThemeChanged -= config;
            BrowserHost.Instance.Changed -= browsers;
        };

        // Coming back to the window (Alt+Tab, closing a dialog) hands the keyboard to the window, not to the
        // terminal inside it, so pass it on whenever the terminals are what is showing.
        AttachedToVisualTree += (_, _) => { if (TopLevel.GetTopLevel(this) is Window w) { w.Activated -= OnWindowActivated; w.Activated += OnWindowActivated; } };
        DetachedFromVisualTree += (_, _) => { if (TopLevel.GetTopLevel(this) is Window w) w.Activated -= OnWindowActivated; };

        if (_group is null && Manager.Sessions.Count == 0) NewTerminal();
        RebuildTabs();
    }

    private IReadOnlyList<TerminalSession> Mine => Manager.Sessions.Where(s => TerminalLayout.GroupOf(s.Id) == _group).ToList();

    private void ApplyBackground() => _termPanel.Background = Ui.Raw(App.Services.Web.TerminalOptions()["background"]!.GetValue<string>());

    public void OnShown() => QueueFocus();

    private bool _focusQueued;
    private long _focusedAt = long.MinValue / 2;

    /// <summary>Hands the keyboard to the terminal once the current work is done; repeated requests share one hand-over.</summary>
    private void QueueFocus()
    {
        if (_focusQueued) return;
        _focusQueued = true;
        Dispatcher.UIThread.Post(() => { _focusQueued = false; FocusTerminal(); }, DispatcherPriority.Background);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (!IsEffectivelyVisible || _editingId is not null) return;
        // Giving the web view the keyboard can itself make a window report "activated" again (on macOS the web view
        // becomes the window's first responder). Acting on that would focus again, and again, keeping the UI thread and
        // the page busy. An activation that follows our own hand-over this closely is that echo, not the user.
        if (Environment.TickCount64 - _focusedAt < FocusEchoMs) return;
        QueueFocus();
    }

    private const long FocusEchoMs = 300;

    private void FocusTerminal()
    {
        _focusedAt = Environment.TickCount64;
        WebViews.FocusNative(_web);
        if (_ready) _ = Js("avamux.focus()");
    }

    /// <summary>True while this view's window is the one the user is working in.</summary>
    private bool InActiveWindow => IsEffectivelyVisible && TopLevel.GetTopLevel(this) is Window { IsActive: true };

    private async Task<string?> Js(string script)
    {
        // Always hand back a string: the web view cannot marshal undefined or objects.
        try { return await _web.InvokeScript("(function(){ var r = (" + script + "); return r == null ? '' : String(r); })()"); }
        catch (Exception ex) { Log.Warn("terminal", $"Script failed: {ex.Message}"); return null; }
    }

    private static string Q(string s) => JsonSerializer.Serialize(s);

    private bool _starting;

    private async Task OnPageReadyAsync()
    {
        if (_ready || _starting) return;
        _starting = true;
        try
        {
            // Navigation can complete before the page's script has run; wait until its API exists.
            var up = false;
            for (var i = 0; i < 100 && !up; i++)
            {
                try { up = await _web.InvokeScript("typeof window.avamux === 'object' ? '1' : '0'") is { } r && r.Contains('1'); }
                catch { /* not there yet */ }
                if (!up) await Task.Delay(100);
            }
            if (!up)
            {
                _overlay.Text = "The terminal view did not start. See Logs.";
                Log.Error("terminal", "Terminal page script did not start within 10s");
                return;
            }
            _ready = true;
            _overlay.IsVisible = false;
            await PushOptionsAsync();
            await SyncViewAsync();
            if (SelectedId is not null) await Js($"avamux.show({Q(SelectedId)})");
            Log.Info("terminal", _group is null ? "Terminal view ready" : $"Terminal window {_group} ready");
            // The page was asked for focus before it existed (app start, a new window); give it now.
            if (IsEffectivelyVisible && _editingId is null) FocusTerminal();
        }
        finally { _starting = false; }
    }

    /// <summary>Makes the web view hold exactly the terminals that belong to this window.</summary>
    private async Task SyncViewAsync()
    {
        if (!_ready) return;
        var mine = Mine.Select(s => s.Id).ToHashSet();
        foreach (var id in _opened.Where(id => !mine.Contains(id)).ToList()) { _opened.Remove(id); await Js($"avamux.remove({Q(id)})"); }
        foreach (var id in mine.Where(id => _opened.Add(id))) await Js($"avamux.open({Q(id)})");
    }

    private Task PushOptionsAsync() =>
        _ready ? Js($"avamux.options({App.Services.Web.TerminalOptions().ToJsonString()})") : Task.CompletedTask;

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Body is not { Length: > 0 } body || JsonNode.Parse(body) is not JsonObject m) return;
            switch (m["t"]?.GetValue<string>())
            {
                case "ready": _ = OnPageReadyAsync(); break;
                case "new": NewTerminal(); break;
                case "close": if (SelectedId is not null) Manager.Close(SelectedId); break;
                case "next": Step(1); break;
                case "prev": Step(-1); break;
                case "boss": Ui.Main?.ToggleBoss(null); break;
                case "browser": ToggleBrowser(); break;
            }
        }
        catch (Exception ex) { Log.Warn("terminal", "Bad message from terminal view: " + ex.Message); }
    }

    /// <summary>The globe button and Ctrl+Shift+L: open a browser beside this tab, or show / hide the one it has.</summary>
    private void ToggleBrowser()
    {
        if (SelectedId is not { } id) return;
        var host = BrowserHost.Instance;
        if (host.DockedFor(id).Count == 0) { BrowserDock.NewTab(id); return; }
        var hide = !host.IsDockHidden(id);
        host.SetDockHidden(id, hide);
        if (hide) QueueFocus();
        else if (host.ActiveFor(id) is { } s) Dispatcher.UIThread.Post(s.FocusPage, DispatcherPriority.Background);
    }

    /// <summary>Shows the browser panes of the selected tab beside it (or nothing, when it has none).</summary>
    private void UpdateDock()
    {
        var host = BrowserHost.Instance;
        var has = _dock.ShowFor(SelectedId);
        var show = has && SelectedId is not null && !host.IsDockHidden(SelectedId);
        if (show != _dock.IsVisible)
        {
            _dock.IsVisible = show;
            _splitter.IsVisible = show;
            var width = Math.Clamp(App.Services.Ui.Data.BrowserDockWidth, 240, 4000);
            _split.ColumnDefinitions[2] = new ColumnDefinition(show ? new GridLength(width) : new GridLength(0)) { MinWidth = show ? 240 : 0 };
        }
        ((PathIcon)((StackPanel)_globe.Content!).Children[0]).Foreground = has ? Ui.Tk("accent") : null;
        ToolTip.SetTip(_globe, !has ? "Open a browser beside this tab (Ctrl+Shift+L)" : show ? "Hide the browser beside this tab (Ctrl+Shift+L)" : "Show the browser beside this tab (Ctrl+Shift+L)");
        host.Sync();
    }

    /// <summary>Gives back the browser pane shown in this page's dock (its window is closing).</summary>
    public void ReleaseBrowser() => _dock.Release();

    private void Step(int delta)
    {
        var list = Mine;
        if (list.Count == 0) return;
        var i = Math.Max(0, list.ToList().FindIndex(s => s.Id == SelectedId));
        Select(list[(i + delta + list.Count) % list.Count].Id);
    }

    public void NewTerminal(string? cwd = null)
    {
        try
        {
            var session = Manager.Create(new TerminalStartOptions(Cwd: cwd));
            if (_group is not null) TerminalLayout.Move(session.Id, _group);
            Select(session.Id);
        }
        catch (Exception ex)
        {
            Log.Error("terminal", "Could not start a terminal", ex);
            Ui.Toast(this, "Could not start a terminal", ex.Message);
        }
    }

    public void Select(string id)
    {
        if (Manager.Find(id) is not { } s || TerminalLayout.GroupOf(s.Id) != _group) return;
        SelectedId = s.Id;
        RebuildTabs();
        UpdateDock();
        if (_ready) _ = ShowAsync(s.Id);
        // A window in the background must not take the keyboard (it happens here when a tab moves out of it and
        // the next tab is selected); it gets the terminal focused when it is activated again.
        if (InActiveWindow) FocusTerminal();
    }

    private async Task ShowAsync(string id)
    {
        await SyncViewAsync();
        await Js($"avamux.show({Q(id)})");
    }

    private void OnClosed(TerminalSession s)
    {
        TerminalLayout.Forget(s.Id);
        if (_opened.Remove(s.Id) && _ready) _ = Js($"avamux.remove({Q(s.Id)})");
        if (SelectedId == s.Id)
        {
            SelectedId = null;
            if (Mine.LastOrDefault() is { } next) Select(next.Id);
        }
        RebuildTabs();
    }

    private void RebuildTabs()
    {
        if (_editingId is not null) return; // do not yank the rename box away
        var sessions = Mine;
        if (sessions.Count > 0) _hadTabs = true;
        else if (_hadTabs && _group is not null) { _hadTabs = false; Emptied?.Invoke(); return; }
        if (SelectedId is not null && sessions.All(s => s.Id != SelectedId)) SelectedId = null; // moved to another window
        if (SelectedId is null && sessions.Count > 0) { Select(sessions[^1].Id); return; }
        _ = SyncViewAsync();
        _tabs.Children.Clear();
        foreach (var s in sessions) _tabs.Children.Add(BuildTab(s));
        UpdateDock();
        if (TopLevel.GetTopLevel(this) is TerminalWindow w) w.Title = sessions.FirstOrDefault(s => s.Id == SelectedId) is { } sel ? $"{sel.Title} — avamux" : "avamux terminals";
    }

    /// <summary>The marker in front of a tab's title: what the terminal is doing right now.</summary>
    private static (Geometry Icon, IBrush Brush, string Text) StateMark(TerminalSession s) => s.State switch
    {
        TerminalState.Exited => (Icons.MinusCircle, Ui.Tk("error"), $"ended (exit code {s.ExitCode})"),
        TerminalState.AgentWaiting => (Icons.Bell, Ui.Tk("warn"), $"{s.Agent ?? "agent"} is waiting for input"),
        TerminalState.AgentWorking => (Icons.Robot, Ui.Tk("purple"), $"{s.Agent ?? "agent"} is working"),
        TerminalState.Running => (Icons.Circle, Ui.Tk("accent"), $"running {s.Process}"),
        _ => (Icons.CircleOutline, Ui.Tk("faint"), "idle"),
    };

    private Control BuildTab(TerminalSession s)
    {
        var selected = s.Id == SelectedId;
        var waiting = s.State == TerminalState.AgentWaiting;
        var (icon, brush, stateText) = StateMark(s);
        var big = s.State is TerminalState.AgentWaiting or TerminalState.AgentWorking or TerminalState.Exited;
        var mark = new PathIcon { Data = icon, Foreground = brush, Width = big ? 13 : 8, Height = big ? 13 : 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = s.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = !s.Running ? Ui.Dim : waiting ? Ui.Tk("warn") : selected ? Ui.Strong : Ui.Tk("textMute"),
            FontWeight = waiting ? FontWeight.SemiBold : FontWeight.Normal,
            FontStyle = s.Running ? FontStyle.Normal : FontStyle.Italic,
            TextDecorations = s.Running ? null : TextDecorations.Strikethrough,
        };
        var close = new Button
        {
            Content = new PathIcon { Data = Icons.Close, Width = 10, Height = 10 }, Classes = { "flat" }, Padding = new Thickness(4), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Opacity = selected ? 0.9 : 0.5,
        };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => Manager.Close(s.Id);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, title, close } };
        var tab = new Border
        {
            Tag = s.Id, Child = row, Padding = new Thickness(10, 5, 6, 6), CornerRadius = Ui.R(7, 7, 0, 0), Cursor = new Cursor(StandardCursorType.Hand),
            Background = selected ? Ui.Tk("bgEditor") : Brushes.Transparent,
            BorderBrush = waiting ? Ui.Tk("warn") : selected ? Ui.Tk("accent") : Brushes.Transparent, BorderThickness = new Thickness(0, 2, 0, 0),
        };
        if (selected) tab.Classes.Add("glow");
        ToolTip.SetTip(tab, $"{s.Process} — {s.Cwd}\n{stateText}" + (s.OscTitle is { Length: > 0 } t ? $"\n{t}" : "") + "\nDouble-click to rename");
        tab.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(tab).Properties;
            if (p.IsMiddleButtonPressed) { Manager.Close(s.Id); return; }
            if (!p.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2) BeginRename(s, row, title);
            else if (SelectedId != s.Id) Select(s.Id);
            // The tab already in front: the click still means "type here", wherever the keyboard was.
            else FocusTerminal();
        };

        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Rename…", () => BeginRename(s, row, title)));
        menu.Items.Add(Ui.Item("Show what is running (clear name)", () => Manager.Rename(s.Id, null), !string.IsNullOrWhiteSpace(s.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Move to New Window", () => { TerminalLayout.OpenWindow(s.Id, TopLevel.GetTopLevel(this) as Window); }, icon: Icons.OpenInNew));
        if (_group is not null) menu.Items.Add(Ui.Item("Move to Main Window", () => TerminalLayout.Move(s.Id, null)));
        foreach (var w in TerminalLayout.OpenWindows.Where(w => w.Group != _group))
            menu.Items.Add(Ui.Item($"Move to \"{w.Title}\"", () => { TerminalLayout.Move(s.Id, w.Group); w.Page.Select(s.Id); w.Activate(); }));
        menu.Items.Add(Ui.Item("New Terminal in New Window", () => { TerminalLayout.OpenWindow(null, TopLevel.GetTopLevel(this) as Window); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Close", () => Manager.Close(s.Id)));
        tab.ContextMenu = menu;
        return tab;
    }

    private void BeginRename(TerminalSession s, StackPanel row, TextBlock title)
    {
        if (_editingId is not null) return;
        _editingId = s.Id;
        var box = new TextBox { Text = s.Name ?? s.Title, MinWidth = 120, Padding = new Thickness(4, 1), MinHeight = 0, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var index = row.Children.IndexOf(title);
        row.Children[index] = box;
        var done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            _editingId = null;
            if (commit) Manager.Rename(s.Id, box.Text); // empty text clears the name
            RebuildTabs();
            FocusTerminal();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostFocus += (_, _) => Finish(true);
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Background);
    }
}
