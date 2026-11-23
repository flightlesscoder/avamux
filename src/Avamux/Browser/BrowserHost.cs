using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avamux.Core.Browser;
using Avamux.Core.Logging;
using Avamux.Views;

namespace Avamux.Browser;

/// <summary>
/// Every browser pane, where it belongs (a terminal tab's dock, or a window of its own), and where its web view is
/// right now. A web view must never leave the window without a place to go, or its page is lost: off screen it waits
/// in a window's parking layer, hidden — or, with keep alive, still "on screen" in the window's last pixel, so the page
/// keeps running and believes it is visible.
/// </summary>
public sealed class BrowserHost : IBrowserSurfaces
{
    public static BrowserHost Instance { get; } = new();

    private readonly List<BrowserSurface> _surfaces = [];
    private readonly Dictionary<string, string> _activeByTerminal = new();
    private readonly HashSet<string> _dockHidden = [];
    private readonly Dictionary<string, BrowserWindow> _windows = new();
    private int _next = 1;
    private DispatcherTimer? _timer;

    /// <summary>Panes were added, removed, moved, or changed title (docks rebuild their tabs).</summary>
    public event Action? Changed;

    public IReadOnlyList<BrowserSurface> Surfaces => _surfaces;

    public void Start()
    {
        App.Services.Browser.Surfaces = this;
        App.Services.Terminals.Closed += s => Dispatcher.UIThread.Post(() => OnTerminalClosed(s.Id));
        // Browser panes are docked under a terminal tab; a pane of a split tab uses its tab's dock.
        TerminalLayout.TabRenamed += OnTabRenamed;
        // A safety net for anything that changes visibility without telling us (window state, a page swap).
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => Sync());
        _timer.Start();
    }

    public BrowserSurface? Find(string id) => _surfaces.FirstOrDefault(s => s.Id == id);

    public IReadOnlyList<BrowserSurface> DockedFor(string? terminal) =>
        terminal is null ? [] : _surfaces.Where(s => s.Terminal == terminal && s.Placement == "dock").ToList();

    public BrowserSurface? ActiveFor(string? terminal)
    {
        var docked = DockedFor(terminal);
        if (docked.Count == 0) return null;
        return terminal is not null && _activeByTerminal.TryGetValue(terminal, out var id) && docked.FirstOrDefault(s => s.Id == id) is { } a ? a : docked[^1];
    }

    public bool IsDockHidden(string terminal) => _dockHidden.Contains(terminal);

    public void SetDockHidden(string terminal, bool hidden)
    {
        if (hidden ? !_dockHidden.Add(terminal) : !_dockHidden.Remove(terminal)) return;
        RaiseChanged();
    }

    public void SetActive(BrowserSurface s)
    {
        if (s.Terminal is null) return;
        _activeByTerminal[s.Terminal] = s.Id;
        _dockHidden.Remove(s.Terminal);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Changed?.Invoke();
        Sync();
    }

    // ---- creating and closing ----

    public BrowserSurface Create(string url, string? terminal, bool window, bool? keepAlive, string? profile, bool isPrivate)
    {
        if (terminal is not null) terminal = TerminalLayout.TabOf(terminal);
        var s = new BrowserSurface("b" + _next++, url, terminal, keepAlive ?? App.Services.Config.Settings.Browser.KeepAliveByDefault, profile, isPrivate);
        s.Changed += Ui.Debounced(() => Changed?.Invoke(), 50);
        _surfaces.Add(s);
        // The page starts in the parking layer straight away, so it loads even before anyone looks at it.
        Park(s, ParkingFor(null));
        if (window || terminal is null) OpenWindow(s);
        else
        {
            s.Placement = "dock";
            _activeByTerminal[terminal] = s.Id;
            _dockHidden.Remove(terminal);
        }
        Log.Info("browser", $"Opened browser pane {s.Id} ({(window || terminal is null ? "window" : "beside terminal " + terminal)}): {url}");
        RaiseChanged();
        return s;
    }

    /// <summary>A new tab next to <paramref name="from"/>, in the same dock or window group, same profile.</summary>
    public void OpenBeside(BrowserSurface from, string? url)
    {
        var target = url ?? App.Services.Browser.Normalize(App.Services.Config.Settings.Browser.HomePage);
        var s = Create(target, from.Terminal, from.Placement == "window" || from.Terminal is null, from.KeepAlive, from.Private ? null : from.Profile, from.Private);
        Show(s, focus: url is null);
        if (url is null) Dispatcher.UIThread.Post(s.FocusAddress, DispatcherPriority.Background);
    }

    public void Close(string id)
    {
        if (Find(id) is not { } s) return;
        // Out of every visual tree first, then let the native side go.
        if (_windows.Remove(id, out var w)) { w.Detach(); w.Close(); }
        if (s.Parent is ContentControl cc) cc.Content = null;
        else if (s.Parent is Panel p) p.Children.Remove(s);
        if (s.Web.Parent is Panel wp) wp.Children.Remove(s.Web);
        s.Shutdown();
        _surfaces.Remove(s);
        if (s.Terminal is not null && _activeByTerminal.TryGetValue(s.Terminal, out var a) && a == id) _activeByTerminal.Remove(s.Terminal);
        App.Services.Browser.Forget(id);
        Log.Info("browser", $"Closed browser pane {id}");
        RaiseChanged();
    }

    /// <summary>A split tab is known by another of its panes now: its docked panes go with it.</summary>
    private void OnTabRenamed(string from, string to)
    {
        foreach (var s in _surfaces.Where(s => s.Terminal == from)) s.Terminal = to;
        if (_activeByTerminal.Remove(from, out var active)) _activeByTerminal[to] = active;
        if (_dockHidden.Remove(from)) _dockHidden.Add(to);
        RaiseChanged();
    }

    private void OnTerminalClosed(string terminal)
    {
        // A pane leaving a split tab may hand the tab (and the panes docked under it) to another pane first.
        TerminalLayout.Forget(terminal);
        // A tab's docked panes go with it; a pane in its own window stays, but no longer belongs to a tab.
        foreach (var s in _surfaces.Where(s => s.Terminal == terminal).ToList())
        {
            if (s.Placement == "dock") Close(s.Id);
            else s.Terminal = null;
        }
        _activeByTerminal.Remove(terminal);
        _dockHidden.Remove(terminal);
    }

    // ---- windows ----

    private void OpenWindow(BrowserSurface s)
    {
        s.Placement = "window";
        if (_windows.ContainsKey(s.Id)) return;
        var w = new BrowserWindow(s);
        _windows[s.Id] = w;
        w.Closing += (_, _) =>
        {
            if (!_windows.Remove(s.Id)) return; // closed by us
            // Closing the window puts the pane back beside its terminal, like detached terminal tabs; without one it closes.
            w.Detach();
            if (s.Terminal is not null && App.Services.Terminals.Find(s.Terminal) is not null)
            {
                s.Placement = "dock";
                SetActive(s);
            }
            else Dispatcher.UIThread.Post(() => Close(s.Id));
        };
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            w.Width = Math.Max(640, main.Width * 0.6);
            w.Height = Math.Max(480, main.Height * 0.8);
        }
        w.Show();
    }

    public void Move(string id, string placement, string? terminal)
    {
        if (Find(id) is not { } s) throw new BrowserException("not_found", $"There is no browser pane {id}.");
        if (placement == "window")
        {
            if (s.Placement == "window") return;
            s.Placement = "window";
            if (s.Parent is ContentControl cc)
            {
                ParkBeforeDetach(s);
                cc.Content = null;
                // Into the new window once the old one has laid out without it (see MoveWeb).
                Dispatcher.UIThread.Post(() => { if (_surfaces.Contains(s) && s.Placement == "window") OpenWindow(s); }, DispatcherPriority.Loaded);
            }
            else OpenWindow(s);
        }
        else
        {
            var t = TerminalLayout.TabOf(terminal ?? s.Terminal ?? App.Services.Host.SelectedTerminal ?? throw new BrowserException("invalid_params", "Which terminal tab? Pass --terminal."));
            if (App.Services.Terminals.Find(t) is null) throw new BrowserException("not_found", $"There is no terminal {t}.");
            if (_windows.Remove(id, out var w)) { Park(s, ParkingFor(null)); w.Detach(); w.Close(); }
            s.Terminal = t;
            s.Placement = "dock";
            _activeByTerminal[t] = s.Id;
            _dockHidden.Remove(t);
        }
        RaiseChanged();
    }

    /// <summary>Brings a pane into view: its terminal tab and dock tab, or its window.</summary>
    public void Show(BrowserSurface s, bool focus)
    {
        if (s.Placement == "window" && _windows.TryGetValue(s.Id, out var w))
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            if (focus) w.Activate();
        }
        else if (s.Terminal is not null)
        {
            SetActive(s);
            if (focus)
            {
                App.Services.Host.Navigate("terminals");
                App.Services.Host.SelectTerminal(s.Terminal);
            }
        }
        Sync();
        if (focus) Dispatcher.UIThread.Post(s.FocusPage, DispatcherPriority.Background);
    }

    // ---- where each web view is ----

    /// <summary>Puts every web view where it should be. Cheap; called whenever anything might have changed.</summary>
    public void Sync()
    {
        foreach (var s in _surfaces)
        {
            try { Place(s); }
            catch (Exception ex) { Log.Warn("browser", $"Could not place browser pane {s.Id}: {ex.Message}"); }
        }
    }

    /// <summary>A window is closing: every web view in it goes to the main window first.</summary>
    public void Evacuate(AppWindow closing)
    {
        if (closing is MainWindow) return; // the app is ending
        foreach (var s in _surfaces.Where(s => !_moving.Contains(s) && TopLevel.GetTopLevel(s.Web) == closing).ToList())
            Park(s, ParkingFor(null));
    }

    private void Place(BrowserSurface s)
    {
        if (_moving.Contains(s)) return; // on its way to another window; placed again when it gets there
        if (TopLevel.GetTopLevel(s) is AppWindow { IsClosing: true }) { Park(s, ParkingFor(null)); return; }
        var attached = TopLevel.GetTopLevel(s) is Window;
        if (attached && (Shown(s) || !s.KeepAlive))
        {
            // On screen; or out of view but still in its pane, where Avalonia hides it and the page carries on as a
            // background tab.
            if (s.Web.Parent != s.Slot) MoveWeb(s, s.Slot);
            ApplySlotLayout(s);
            return;
        }
        Park(s, ParkingFor(attached ? TopLevel.GetTopLevel(s) as AppWindow : null));
    }

    /// <summary>
    /// Whether a control and every one of its parents is visible. Avalonia's own IsEffectivelyVisible can lag behind
    /// (seen when a dock is hidden and the tab selected again in one go), so this asks the parents themselves.
    /// </summary>
    public static bool Shown(Visual v)
    {
        for (Visual? x = v; x is not null; x = x.GetVisualParent())
            if (!x.IsVisible) return false;
        return true;
    }

    private static void ApplySlotLayout(BrowserSurface s)
    {
        if (s.Web.Parent != s.Slot) return;
        s.Web.IsVisible = true;
        Canvas.SetLeft(s.Web, double.NaN); Canvas.SetTop(s.Web, double.NaN);
        Canvas.SetRight(s.Web, double.NaN); Canvas.SetBottom(s.Web, double.NaN);
        if (s.Viewport is not null) return;
        s.Web.Width = double.NaN; s.Web.Height = double.NaN;
    }

    /// <summary>Moves a web view into a window's parking layer: hidden, or (keep alive) one pixel on screen.</summary>
    private void Park(BrowserSurface s, Canvas? lot)
    {
        if (lot is null || _moving.Contains(s)) return;
        var size = s.Web.Bounds.Size is { Width: > 1, Height: > 1 } b ? b : new Size(1024, 768);
        // Anchored to the bottom-right corner, outside the window however it is resized. WebKitGTK keeps a page that
        // is clipped away running as if it were on screen; macOS's WKWebView judges by what is visible, so there one
        // pixel (the page's top-left) stays inside the window's corner.
        var peek = OperatingSystem.IsLinux() ? 0 : 1;
        s.Web.Width = size.Width;
        s.Web.Height = size.Height;
        Canvas.SetLeft(s.Web, double.NaN); Canvas.SetTop(s.Web, double.NaN);
        Canvas.SetRight(s.Web, peek - size.Width);
        Canvas.SetBottom(s.Web, peek - size.Height);
        // A layer that has not been laid out yet (a window still opening) has no corner to hide behind.
        s.Web.IsVisible = s.KeepAlive && lot.Bounds.Width > 1 && lot.Bounds.Height > 1;
        if (s.Web.Parent != lot) MoveWeb(s, lot);
    }

    private readonly HashSet<BrowserSurface> _moving = [];

    /// <summary>
    /// Moves a web view to another parent without losing its page. Within one window that is immediate. Between
    /// windows it takes two steps, as Avalonia intends (a reparenting scope): out of the old window, one layout pass
    /// there without it, then into the new one. Moved in one go, the old window's layout manager still has it queued
    /// and throws ("wrong LayoutManager"), losing that frame's layout.
    /// </summary>
    private void MoveWeb(BrowserSurface s, Panel to)
    {
        var web = s.Web;
        if (web.Parent == to) return;
        var src = TopLevel.GetTopLevel(web);
        var dest = TopLevel.GetTopLevel(to);
        if (src is null || dest is null || src == dest)
        {
            using (web.BeginReparenting(false))
            {
                if (web.Parent is Panel from) from.Children.Remove(web);
                to.Children.Add(web);
            }
            return;
        }
        _ = MoveAcrossAsync(s, to);
    }

    private async Task MoveAcrossAsync(BrowserSurface s, Panel to)
    {
        _moving.Add(s);
        var web = s.Web;
        try
        {
            await using (web.BeginReparentingAsync())
            {
                if (web.Parent is Panel from) from.Children.Remove(web);
                // Lower than Render: the old window has laid out (without the web view) by the time this runs.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                if (!_surfaces.Contains(s)) return; // closed meanwhile: leaving the scope lets the native view go
                var target = TopLevel.GetTopLevel(to) is not null ? to : (Panel?)ParkingFor(null);
                target?.Children.Add(web);
            }
        }
        catch (Exception ex) { Log.Warn("browser", $"Could not move browser pane {s.Id} between windows: {ex.Message}"); }
        finally
        {
            _moving.Remove(s);
            if (_surfaces.Contains(s)) Place(s);
        }
    }

    private static Canvas? ParkingFor(AppWindow? preferred)
    {
        if (preferred is { IsVisible: true, IsClosing: false }) return preferred.ParkingLot;
        return App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: AppWindow main } ? main.ParkingLot : preferred?.ParkingLot;
    }

    /// <summary>
    /// Called before a pane's control leaves the visual tree. A dock switching tabs parks the page in its own window
    /// (immediate); a window that is closing must not keep it, or the page goes down with the window, so it goes to the
    /// main window's layer (out of the closing window at once, in two steps; see <see cref="MoveWeb"/>).
    /// </summary>
    public void ParkBeforeDetach(BrowserSurface s, bool windowClosing = false)
    {
        if (_moving.Contains(s)) return;
        Park(s, ParkingFor(windowClosing ? null : TopLevel.GetTopLevel(s) as AppWindow));
    }

    // ---- IBrowserSurfaces (called from the command threads) ----

    private static Task<T> OnUi<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();
    private static Task<T> OnUi<T>(Func<Task<T>> f) => Dispatcher.UIThread.InvokeAsync(f);
    private static Task OnUi(Action a) => Dispatcher.UIThread.InvokeAsync(a).GetTask();

    private BrowserSurface Get(string id) => Find(id) ?? throw new BrowserException("not_found", $"There is no browser pane {id}. See `avamux browser list`.");

    private BrowserSurfaceInfo InfoOf(BrowserSurface s) =>
        s.Info(TopLevel.GetTopLevel(s) is Window { IsVisible: true } w && w.WindowState != WindowState.Minimized && Shown(s),
            s.Placement == "window" || s.Terminal is not null && ActiveFor(s.Terminal) == s);

    IReadOnlyList<BrowserSurfaceInfo> IBrowserSurfaces.List() =>
        Dispatcher.UIThread.CheckAccess() ? _surfaces.Select(InfoOf).ToList() : Dispatcher.UIThread.InvokeAsync(() => _surfaces.Select(InfoOf).ToList()).GetTask().GetAwaiter().GetResult();

    Task<BrowserSurfaceInfo> IBrowserSurfaces.OpenAsync(BrowserOpenOptions o) => OnUi(() =>
    {
        var terminal = o.Window ? o.Terminal : o.Terminal ?? App.Services.Host.SelectedTerminal;
        if (terminal is not null && App.Services.Terminals.Find(terminal) is null) throw new BrowserException("not_found", $"There is no terminal {terminal}.");
        var s = Create(o.Url ?? "about:blank", terminal, o.Window, o.KeepAlive, o.Profile, o.Private);
        if (o.Focus) Show(s, true);
        return InfoOf(s);
    });

    Task IBrowserSurfaces.CloseAsync(string id) => OnUi(() => { Get(id); Close(id); });
    Task IBrowserSurfaces.ShowAsync(string id, bool focus) => OnUi(() => Show(Get(id), focus));
    Task IBrowserSurfaces.MoveAsync(string id, string placement, string? terminal) => OnUi(() => Move(id, placement, terminal));
    Task<string?> IBrowserSurfaces.EvalAsync(string id, string script) => OnUi(() => Get(id).EvalAsync(script));
    Task IBrowserSurfaces.NavigateAsync(string id, string url) => OnUi(() => Get(id).NavigateTo(url));
    Task IBrowserSurfaces.GoAsync(string id, string action) => OnUi(async () => { await Get(id).GoAsync(action); return true; });

    async Task<bool> IBrowserSurfaces.WaitForLoadAsync(string id, int loads, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var done = await OnUi(() => Find(id) is not { } s || s.Loads > loads && !s.Loading);
            if (done) return true;
            await Task.Delay(50);
        }
        return false;
    }

    Task IBrowserSurfaces.ScreenshotAsync(string id, string file, bool fullPage) => OnUi(async () => { await Get(id).ScreenshotAsync(file, fullPage); return true; });
    Task IBrowserSurfaces.PdfAsync(string id, string file) => OnUi(async () => { await Get(id).PdfAsync(file); return true; });
    Task<string> IBrowserSurfaces.DevToolsAsync(string id, string action) => OnUi(() => Get(id).DevToolsAsync(action));
    Task IBrowserSurfaces.SetInitScriptsAsync(string id, IReadOnlyList<BrowserInitScript> scripts) => OnUi(async () => { await Get(id).SetInitScriptsAsync(scripts); return true; });
    Task IBrowserSurfaces.SetKeepAliveAsync(string id, bool on) => OnUi(async () => { await Get(id).SetKeepAliveAsync(on); return true; });
    Task<double> IBrowserSurfaces.ZoomAsync(string id, string action) => OnUi(() => Get(id).ZoomAsync(action));
    Task<string?> IBrowserSurfaces.SetViewportAsync(string id, int? width, int? height) => OnUi(() => Get(id).SetViewport(width, height));
    Task<bool> IBrowserSurfaces.DesignModeAsync(string id, bool? on) => OnUi(async () => on is { } v ? await Get(id).SetDesignModeAsync(v) : Get(id).DesignMode);
    Task<bool> IBrowserSurfaces.AnswerDialogAsync(string id, bool accept, string? text) => OnUi(() => Get(id).AnswerDialogAsync(accept, text));
}
