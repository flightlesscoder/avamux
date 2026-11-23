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
/// How a split tab's panes are arranged: a leaf is one terminal; a split puts <see cref="A"/> left of (or above)
/// <see cref="B"/>, giving A <see cref="Ratio"/> of the room.
/// </summary>
public sealed class SplitNode
{
    public string? Id { get; set; }
    public bool Down { get; set; }
    public double Ratio { get; set; } = 0.5;
    public SplitNode? A { get; set; }
    public SplitNode? B { get; set; }

    public bool IsLeaf => Id is not null;
    public IEnumerable<string> Leaves() => IsLeaf ? [Id!] : A!.Leaves().Concat(B!.Leaves());

    /// <summary>What the terminal page lays out: <c>{id}</c>, or <c>{dir: right|down, ratio, a, b}</c>.</summary>
    public JsonObject ToJson() => IsLeaf
        ? new JsonObject { ["id"] = Id }
        : new JsonObject { ["dir"] = Down ? "down" : "right", ["ratio"] = Math.Round(Ratio, 4), ["a"] = A!.ToJson(), ["b"] = B!.ToJson() };
}

/// <summary>
/// Which window each terminal tab lives in, and which terminals share a tab as split panes. A tab with no entry is in
/// the main window; detaching a tab gives it the key of a <see cref="TerminalWindow"/>. Sessions themselves are owned
/// by the core and never move. A split tab is known by the id of one of its panes (the first, at first): that id is
/// the tab's, and its browser panes are docked under it. When that pane goes, the tab carries on under another
/// (<see cref="TabRenamed"/>).
/// </summary>
public static class TerminalLayout
{
    private static readonly Dictionary<string, string> Groups = new();
    private static readonly Dictionary<string, TerminalWindow> Windows = new();
    private static readonly Dictionary<string, SplitNode> Trees = new(); // split tab → its panes
    // Every pane of a split tab → the tab. Read from other threads too (the CLI asks which tab a terminal is in).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TabOfPane = new();
    private static readonly Dictionary<string, string> FocusOf = new(); // split tab → the pane last typed in
    private static int _next = 1;

    public static event Action? Changed;
    /// <summary>A split tab is now known by another id (old, new): the pane it was named after left it.</summary>
    public static event Action<string, string>? TabRenamed;

    public static string? GroupOf(string sessionId) => Groups.GetValueOrDefault(sessionId);
    public static IReadOnlyCollection<TerminalWindow> OpenWindows => Windows.Values;
    public static TerminalWindow? WindowOf(string sessionId) => GroupOf(sessionId) is { } g ? Windows.GetValueOrDefault(g) : null;

    // ---- split panes ----

    /// <summary>The tab a terminal is shown in: its own id, or the id of the split tab it is a pane of.</summary>
    public static string TabOf(string sessionId) => TabOfPane.GetValueOrDefault(sessionId, sessionId);
    /// <summary>Whether a terminal has a tab of its own in the strip (it is not a pane of another's split tab).</summary>
    public static bool IsTab(string sessionId) => TabOf(sessionId) == sessionId;
    public static bool IsSplit(string tab) => Trees.ContainsKey(tab);
    /// <summary>Every terminal shown in a tab, in layout order (just the tab's own when it is not split).</summary>
    public static IReadOnlyList<string> PanesOf(string tab) => Trees.TryGetValue(tab, out var t) ? t.Leaves().ToList() : [tab];
    /// <summary>The pane of a tab that has (or last had) the keyboard.</summary>
    public static string FocusedPane(string tab) => FocusOf.TryGetValue(tab, out var f) && TabOf(f) == tab ? f : tab;
    public static void SetFocusedPane(string pane)
    {
        var tab = TabOf(pane);
        if (Trees.ContainsKey(tab)) FocusOf[tab] = pane;
    }
    /// <summary>The tab's layout for the terminal page.</summary>
    public static JsonObject LayoutOf(string tab) => Trees.TryGetValue(tab, out var t) ? t.ToJson() : new JsonObject { ["id"] = tab };

    /// <summary>Puts <paramref name="newPane"/> beside (or below) <paramref name="pane"/>, in its tab, and gives it the keyboard.</summary>
    public static void Split(string pane, string newPane, bool down)
    {
        var tab = TabOf(pane);
        if (!Trees.TryGetValue(tab, out var tree)) Trees[tab] = tree = new SplitNode { Id = tab };
        var leaf = Find(tree, pane) ?? throw new InvalidOperationException($"Terminal {pane} is not in tab {tab}.");
        leaf.A = new SplitNode { Id = pane };
        leaf.B = new SplitNode { Id = newPane };
        leaf.Id = null; leaf.Down = down; leaf.Ratio = 0.5;
        foreach (var id in tree.Leaves()) TabOfPane[id] = tab;
        if (GroupOf(tab) is { } g) Groups[newPane] = g; else Groups.Remove(newPane);
        FocusOf[tab] = newPane;
        Changed?.Invoke();
    }

    /// <summary>Sets where a divider sits. <paramref name="path"/> leads from the tab's root split to it: "a"/"b" per step.</summary>
    public static void SetRatio(string tab, string path, double ratio)
    {
        if (!Trees.TryGetValue(tab, out var node)) return;
        foreach (var step in path) node = (step == 'a' ? node.A : node.B) ?? node;
        if (node.IsLeaf) return;
        node.Ratio = Math.Clamp(ratio, 0.05, 0.95);
    }

    /// <summary>Puts every divider of a tab back in the middle.</summary>
    public static void EvenOut(string tab)
    {
        if (!Trees.TryGetValue(tab, out var tree)) return;
        void Even(SplitNode n) { if (n.IsLeaf) return; n.Ratio = 0.5; Even(n.A!); Even(n.B!); }
        Even(tree);
        Changed?.Invoke();
    }

    /// <summary>Takes a pane out of its split tab into a tab of its own, in the same window.</summary>
    public static void Unsplit(string pane)
    {
        if (!TakeOut(pane)) return;
        Changed?.Invoke();
    }

    /// <summary>Removes a pane from its tab's tree; false when it was not in a split tab.</summary>
    private static bool TakeOut(string pane)
    {
        if (!TabOfPane.TryRemove(pane, out var tab) || !Trees.TryGetValue(tab, out var tree)) return false;
        var parent = ParentOf(tree, pane);
        if (parent is null) return false;
        var sibling = parent.A!.Id == pane ? parent.B! : parent.A!;
        parent.Id = sibling.Id; parent.Down = sibling.Down; parent.Ratio = sibling.Ratio; parent.A = sibling.A; parent.B = sibling.B;
        var focus = FocusOf.GetValueOrDefault(tab);
        if (focus == pane || focus is null) focus = sibling.Leaves().First();
        var name = tab == pane ? tree.Leaves().First() : tab; // the tab is named after a pane that is still in it
        Trees.Remove(tab); FocusOf.Remove(tab);
        if (tree.IsLeaf) TabOfPane.TryRemove(tree.Id!, out _); // one pane left: an ordinary tab again
        else
        {
            Trees[name] = tree;
            FocusOf[name] = focus;
            foreach (var id in tree.Leaves()) TabOfPane[id] = name;
        }
        if (name != tab) TabRenamed?.Invoke(tab, name);
        return true;
    }

    private static SplitNode? Find(SplitNode n, string id) => n.IsLeaf ? (n.Id == id ? n : null) : Find(n.A!, id) ?? Find(n.B!, id);

    private static SplitNode? ParentOf(SplitNode n, string id) =>
        n.IsLeaf ? null : n.A!.Id == id || n.B!.Id == id ? n : ParentOf(n.A!, id) ?? ParentOf(n.B!, id);

    // ---- windows ----

    /// <summary>Moves a tab (every pane of it) to a window; null is the main window.</summary>
    public static void Move(string sessionId, string? group)
    {
        foreach (var id in PanesOf(TabOf(sessionId)))
            if (group is null) Groups.Remove(id); else Groups[id] = group;
        Changed?.Invoke();
    }

    /// <summary>Opens a new terminal window and moves a tab into it (or starts a new terminal there).</summary>
    public static TerminalWindow OpenWindow(string? sessionId, Window? near = null)
    {
        var key = "w" + _next++;
        if (sessionId is not null) foreach (var id in PanesOf(TabOf(sessionId))) Groups[id] = key;
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

    /// <summary>
    /// A terminal has closed: it leaves its window and its split tab. Safe to call more than once (every terminal view
    /// and the browser host hear of the closing, in no set order).
    /// </summary>
    public static void Forget(string sessionId)
    {
        var split = TakeOut(sessionId);
        Groups.Remove(sessionId);
        if (split) Changed?.Invoke();
    }
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
    private readonly GridSplitter _splitter = new() { Classes = { "divider" }, ResizeDirection = GridResizeDirection.Columns, IsVisible = false };
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
        TerminalLayout.TabRenamed += OnTabRenamed;
        var browsers = Ui.Debounced(UpdateDock, 20);
        BrowserHost.Instance.Changed += browsers;
        App.Services.Config.Changed += config;
        Ui.ThemeChanged += config;
        // A detached window goes away; the main page lives as long as the app.
        DetachedFromVisualTree += (_, _) =>
        {
            if (_group is null) return;
            Manager.Changed -= refresh; Manager.Closed -= closed; TerminalLayout.Changed -= refresh; TerminalLayout.TabRenamed -= OnTabRenamed; App.Services.Config.Changed -= config; Ui.ThemeChanged -= config;
            BrowserHost.Instance.Changed -= browsers;
        };

        // Coming back to the window (Alt+Tab, closing a dialog) hands the keyboard to the window, not to the
        // terminal inside it, so pass it on whenever the terminals are what is showing.
        AttachedToVisualTree += (_, _) => { if (TopLevel.GetTopLevel(this) is Window w) { w.Activated -= OnWindowActivated; w.Activated += OnWindowActivated; } };
        DetachedFromVisualTree += (_, _) => { if (TopLevel.GetTopLevel(this) is Window w) w.Activated -= OnWindowActivated; };

        if (_group is null && Manager.Sessions.Count == 0) NewTerminal();
        RebuildTabs();
    }

    /// <summary>This window's tabs: its terminals, less those shown as panes of another's split tab.</summary>
    private IReadOnlyList<TerminalSession> Mine => Manager.Sessions.Where(s => TerminalLayout.GroupOf(s.Id) == _group && TerminalLayout.IsTab(s.Id)).ToList();
    /// <summary>Every terminal of this window, split panes included: what its web view holds.</summary>
    private IReadOnlyList<TerminalSession> MinePanes => Manager.Sessions.Where(s => TerminalLayout.GroupOf(s.Id) == _group).ToList();
    /// <summary>The pane of the selected tab that has the keyboard (the tab itself when it is not split).</summary>
    private string? SelectedPane => SelectedId is { } id ? TerminalLayout.FocusedPane(id) : null;

    private void OnTabRenamed(string from, string to)
    {
        if (SelectedId == from) SelectedId = to;
    }

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
            await PushLayoutAsync(force: true);
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
        var mine = MinePanes.Select(s => s.Id).ToHashSet();
        foreach (var id in _opened.Where(id => !mine.Contains(id)).ToList()) { _opened.Remove(id); await Js($"avamux.remove({Q(id)})"); }
        foreach (var id in mine.Where(id => _opened.Add(id))) await Js($"avamux.open({Q(id)})");
    }

    private string? _pushedLayout;

    /// <summary>Shows the selected tab's panes in the web view (laid out as split), when that has changed.</summary>
    private async Task PushLayoutAsync(bool force = false)
    {
        if (!_ready || SelectedId is not { } tab) return;
        var layout = TerminalLayout.LayoutOf(tab).ToJsonString();
        var focus = TerminalLayout.FocusedPane(tab);
        var key = layout + "|" + focus;
        if (!force && key == _pushedLayout) return;
        _pushedLayout = key;
        await Js($"avamux.layout({layout}, {Q(focus)})");
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
                case "close": if (SelectedPane is { } focus) Manager.Close(focus); break;
                case "focus" when m["id"]?.GetValue<string>() is { } pane && SelectedId is { } tab && TerminalLayout.TabOf(pane) == tab:
                    if (TerminalLayout.FocusedPane(tab) != pane) { TerminalLayout.SetFocusedPane(pane); RebuildTabs(); }
                    break;
                case "ratio" when SelectedId is { } tab && m["path"]?.GetValue<string>() is { } path && m["ratio"] is { } ratio:
                    TerminalLayout.SetRatio(tab, path, ratio.GetValue<double>());
                    _pushedLayout = TerminalLayout.LayoutOf(tab).ToJsonString() + "|" + TerminalLayout.FocusedPane(tab); // the page has it already
                    break;
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

    /// <summary>Selects a tab; given a pane of a split tab, selects that tab and gives the pane the keyboard.</summary>
    public void Select(string id)
    {
        if (Manager.Find(id) is not { } s || TerminalLayout.GroupOf(s.Id) != _group) return;
        if (!TerminalLayout.IsTab(s.Id)) TerminalLayout.SetFocusedPane(s.Id);
        SelectedId = TerminalLayout.TabOf(s.Id);
        RebuildTabs();
        UpdateDock();
        if (_ready) _ = SyncAsync();
        // A window in the background must not take the keyboard (it happens here when a tab moves out of it and
        // the next tab is selected); it gets the terminal focused when it is activated again.
        if (InActiveWindow) FocusTerminal();
    }

    /// <summary>The web view holds this window's terminals and shows the selected tab's panes.</summary>
    private async Task SyncAsync()
    {
        await SyncViewAsync();
        await PushLayoutAsync();
    }

    /// <summary>Starts a terminal beside (or below) a pane, in the same folder, as a new pane of its tab.</summary>
    private void SplitPane(string pane, bool down)
    {
        try
        {
            var session = Manager.Create(new TerminalStartOptions(Cwd: Manager.Find(pane)?.Cwd));
            AddSplit(pane, session.Id, down);
        }
        catch (Exception ex)
        {
            Log.Error("terminal", "Could not split the terminal", ex);
            Ui.Toast(this, "Could not split the terminal", ex.Message);
        }
    }

    /// <summary>Shows a terminal that has just started beside (or below) a pane of this window, and gives it the keyboard.</summary>
    public void AddSplit(string pane, string newPane, bool down)
    {
        if (Manager.Find(pane) is null || Manager.Find(newPane) is null) return;
        TerminalLayout.Split(pane, newPane, down);
        Select(newPane);
    }

    /// <summary>Closes a tab: every pane of it.</summary>
    private void CloseTab(string tab)
    {
        foreach (var id in TerminalLayout.PanesOf(tab).ToList()) Manager.Close(id);
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
        _ = SyncAsync();
        _tabs.Children.Clear();
        foreach (var s in sessions) _tabs.Children.Add(BuildTab(s));
        UpdateDock();
        if (TopLevel.GetTopLevel(this) is TerminalWindow w) w.Title = SelectedPane is { } pane && Manager.Find(pane) is { } sel ? $"{sel.Title} — avamux" : "avamux terminals";
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

    /// <param name="owner">The terminal the tab is known by. A split tab shows the pane that has the keyboard, and the
    /// mark of a pane whose agent is waiting, whichever pane that is.</param>
    private Control BuildTab(TerminalSession owner)
    {
        var tabId = owner.Id;
        var panes = TerminalLayout.PanesOf(tabId).Select(Manager.Find).OfType<TerminalSession>().ToList();
        var split = panes.Count > 1;
        var s = Manager.Find(TerminalLayout.FocusedPane(tabId)) ?? owner;
        var selected = tabId == SelectedId;
        var waiting = panes.Any(p => p.State == TerminalState.AgentWaiting);
        var marked = panes.FirstOrDefault(p => p.State == TerminalState.AgentWaiting) ?? s;
        var (icon, brush, stateText) = StateMark(marked);
        var big = marked.State is TerminalState.AgentWaiting or TerminalState.AgentWorking or TerminalState.Exited;
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
        ToolTip.SetTip(close, split ? $"Close (all {panes.Count} panes)" : "Close");
        close.Click += (_, _) => CloseTab(tabId);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, title, close } };
        if (split)
        {
            // How many panes the tab is split into, before the close button.
            row.Children.Insert(2, new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75,
                Children =
                {
                    new PathIcon { Data = Icons.SplitRight, Width = 11, Height = 11, Foreground = Ui.Tk("textMute") },
                    new TextBlock { Text = panes.Count.ToString(), FontSize = 11, Foreground = Ui.Tk("textMute"), VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
        var tab = new Border
        {
            Tag = tabId, Child = row, Padding = new Thickness(10, 5, 6, 6), CornerRadius = Ui.R(7, 7, 0, 0), Cursor = new Cursor(StandardCursorType.Hand),
            Background = selected ? Ui.Tk("bgEditor") : Brushes.Transparent,
            BorderBrush = waiting ? Ui.Tk("warn") : selected ? Ui.Tk("accent") : Brushes.Transparent, BorderThickness = new Thickness(0, 2, 0, 0),
        };
        if (selected) tab.Classes.Add("glow");
        ToolTip.SetTip(tab, $"{s.Process} — {s.Cwd}\n{stateText}" + (s.OscTitle is { Length: > 0 } t ? $"\n{t}" : "")
            + (split ? $"\nSplit into {panes.Count} panes; this is the one you last typed in" : "") + "\nDouble-click to rename");
        tab.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(tab).Properties;
            if (p.IsMiddleButtonPressed) { CloseTab(tabId); return; }
            if (!p.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2) BeginRename(s, row, title);
            else if (SelectedId != tabId) Select(tabId);
            // The tab already in front: the click still means "type here", wherever the keyboard was.
            else FocusTerminal();
        };

        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Rename…", () => BeginRename(s, row, title)));
        menu.Items.Add(Ui.Item("Show what is running (clear name)", () => Manager.Rename(s.Id, null), !string.IsNullOrWhiteSpace(s.Name)));
        menu.Items.Add(new Separator());
        // Splits act on the pane that has the keyboard; the new pane gets it, and starts in the same folder.
        menu.Items.Add(Ui.Item("Split Right", () => SplitPane(s.Id, down: false), icon: Icons.SplitRight));
        menu.Items.Add(Ui.Item("Split Down", () => SplitPane(s.Id, down: true), icon: Icons.SplitDown));
        if (split)
        {
            menu.Items.Add(Ui.Item("Even Out Panes", () => TerminalLayout.EvenOut(tabId)));
            menu.Items.Add(Ui.Item("Move Pane to Its Own Tab", () => { TerminalLayout.Unsplit(s.Id); Select(s.Id); }));
            menu.Items.Add(Ui.Item("Close Pane", () => Manager.Close(s.Id)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Move to New Window", () => { TerminalLayout.OpenWindow(s.Id, TopLevel.GetTopLevel(this) as Window); }, icon: Icons.OpenInNew));
        if (_group is not null) menu.Items.Add(Ui.Item("Move to Main Window", () => TerminalLayout.Move(s.Id, null)));
        foreach (var w in TerminalLayout.OpenWindows.Where(w => w.Group != _group))
            menu.Items.Add(Ui.Item($"Move to \"{w.Title}\"", () => { TerminalLayout.Move(s.Id, w.Group); w.Page.Select(s.Id); w.Activate(); }));
        menu.Items.Add(Ui.Item("New Terminal in New Window", () => { TerminalLayout.OpenWindow(null, TopLevel.GetTopLevel(this) as Window); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item(split ? $"Close Tab ({panes.Count} panes)" : "Close", () => CloseTab(tabId)));
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
