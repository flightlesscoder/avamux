using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avamux.Core.Browser;
using Avamux.Core.Logging;
using Avamux.Views;

namespace Avamux.Browser;

/// <summary>
/// One browser pane: a toolbar over a native web view. The web view itself is moved between the pane's slot and the
/// window's parking layer by <see cref="BrowserHost"/>, so the page survives the pane going out of view.
/// </summary>
public sealed class BrowserSurface : UserControl, IWebKitEvents
{
    private readonly TextBox _address = new() { FontSize = 12.5, Padding = new Thickness(8, 3), MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _back, _forward, _reload, _keep, _pick, _devtools, _audio;
    private readonly PathIcon _reloadIcon = new() { Data = Icons.Refresh, Width = 14, Height = 14 };
    private readonly Border _errorDot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Ui.Tk("error"), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly ProgressBar _progress = new() { Height = 2, MinHeight = 2, Minimum = 0, Maximum = 1, IsVisible = false, Background = Brushes.Transparent };
    private readonly Border _dialogBar = new() { IsVisible = false, Padding = new Thickness(10, 8) };
    private readonly Border _crashBar = new() { IsVisible = false, Padding = new Thickness(10, 8) };
    private readonly TextBlock _blank = new() { Text = "", Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private string _initialUrl;
    private IntPtr _view, _userScript, _dialog;
    private IntPtr _wkWebView; // macOS: the WKWebView, while Avalonia's adapter has it (not retained by us)
    private readonly List<IntPtr> _initScriptHandles = [];
    private IReadOnlyList<BrowserInitScript> _initScripts = [];
    private bool _adapterReady, _addressEditing;
    private int _errorsOnPage;
    private bool _audible, _muted, _canBack, _canForward;

    public BrowserSurface(string id, string url, string? terminal, bool keepAlive, string? profile, bool isPrivate)
    {
        Id = id;
        Terminal = terminal;
        KeepAlive = keepAlive;
        Profile = profile;
        Private = isPrivate;
        _initialUrl = url;
        Url = url;

        Web = new NativeWebView { Focusable = true };
        Slot = new Panel { ClipToBounds = true, Background = Brushes.White, Children = { _blank } };

        _back = Tool(Icons.ArrowLeft, "Back (Alt+Left)", () => _ = GoAsync("back"));
        _forward = Tool(Icons.ArrowRight, "Forward (Alt+Right)", () => _ = GoAsync("forward"));
        _reload = Tool(Icons.Refresh, "Reload (Ctrl+R; Shift for a hard reload)", () => _ = GoAsync(Loading ? "stop" : "reload"));
        _reload.Content = _reloadIcon;
        _keep = Tool(Icons.PinOutline, "", () => _ = SetKeepAliveAsync(!KeepAlive));
        _pick = Tool(Icons.HandPointing, "Design mode: click an element in the page to copy its details for your agent (Ctrl+Shift+C)", () => _ = SetDesignModeAsync(!DesignMode));
        _devtools = Tool(Icons.Bug, "Developer tools (F12)", () => _ = DevToolsAsync("toggle"));
        _devtools.Content = new Panel { Children = { new PathIcon { Data = Icons.Bug, Width = 14, Height = 14 }, _errorDot } };
        _audio = Tool(Icons.VolumeHigh, "Mute this page", () => { if (_view != IntPtr.Zero) _ = WebKitGtk.SetMutedAsync(_view, !_muted); });
        _audio.IsVisible = false;
        var menu = Tool(Icons.DotsVertical, "More", () => { });
        menu.Click += (_, _) => BuildMenu().Open(menu);
        var close = Tool(Icons.Close, "Close this browser pane", () => BrowserHost.Instance.Close(Id));

        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _addressEditing = false; Navigate(_address.Text ?? ""); FocusPage(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _addressEditing = false; _address.Text = Url; FocusPage(); }
            else _addressEditing = true;
        };
        _address.GotFocus += (_, _) => Dispatcher.UIThread.Post(() => _address.SelectAll(), DispatcherPriority.Background);
        _address.LostFocus += (_, _) => { if (!_addressEditing) _address.Text = Url; };

        var toolbar = new Border
        {
            Background = Ui.Tk("bgDeep"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(4, 3),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { _back, _forward, _reload } }.Dock(Dock.Left),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(4, 0, 0, 0), Children = { _audio, _keep, _pick, _devtools, menu, close } }.Dock(Dock.Right),
                    _address,
                },
            },
        };
        Content = new DockPanel
        {
            Children =
            {
                toolbar.Dock(Dock.Top),
                _progress.Dock(Dock.Top),
                _dialogBar.Dock(Dock.Top),
                _crashBar.Dock(Dock.Top),
                Slot,
            },
        };
        UpdateToolbar();

        Web.EnvironmentRequested += (_, e) =>
        {
            e.EnableDevTools = App.Services.Config.Settings.Browser.DeveloperTools;
            if (e is GtkWebViewEnvironmentRequestedEventArgs gtk)
            {
                if (Private) gtk.EphemeralDataManager = true;
                else if (Profile is { Length: > 0 } p)
                {
                    var safe = string.Concat(p.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
                    gtk.BaseDataDirectory = Path.Combine(App.Services.Paths.DataDir, "browser", "profiles", safe, "data");
                    gtk.BaseCacheDirectory = Path.Combine(App.Services.Paths.DataDir, "browser", "profiles", safe, "cache");
                }
            }
        };
        WebViews.FocusOnClick(Web);
        Web.AdapterCreated += (_, _) => _ = OnAdapterCreatedAsync();
        Web.AdapterDestroyed += (_, _) =>
        {
            // The native view went (it was out of every window for too long). Should it come back, it reopens this page.
            Log.Warn("browser", $"Browser pane {Id}: the native web view was destroyed ({Url})");
            if (_view != IntPtr.Zero) { WebKitGtk.Disconnect(_view); _view = IntPtr.Zero; _userScript = IntPtr.Zero; }
            if (_wkWebView != IntPtr.Zero) { WebKitMac.Forget(_wkWebView); _wkWebView = IntPtr.Zero; }
            _dialog = IntPtr.Zero; Dialog = null;
            _adapterReady = false;
            _initialUrl = Url;
        };
        // Called while the GLib thread waits for it (Avalonia's policy decision): nothing here may wait on that thread.
        Web.NavigationStarted += (_, e) =>
        {
            // Frames inside the page navigate too and arrive here as well; with WebKitGTK the main frame's own
            // address and loading state come from its notifications instead.
            if (_view != IntPtr.Zero) return;
            if (e.Request is { } u) SetUrl(u.ToString());
            Loading = true;
            NewDocument();
            UpdateToolbar();
        };
        Web.NavigationCompleted += (_, e) =>
        {
            // Avalonia reports this when the new document is committed. On WebKitGTK the end of the load comes from
            // is-loading instead (images and scripts included); elsewhere this is the best there is.
            if (e.Request is { } u) SetUrl(u.ToString());
            if (_view == IntPtr.Zero) { Loading = false; _canBack = Web.CanGoBack; _canForward = Web.CanGoForward; FinishLoad(); _ = RefreshTitleAsync(); }
            UpdateToolbar();
            if (Viewport is not null) ApplyViewport();
        };
        Web.WebMessageReceived += (_, e) => OnMessage(e.Body);
        Web.NewWindowRequested += (_, e) =>
        {
            // window.open and target=_blank: a new tab in the same dock, same profile. Later, not here: the GLib thread
            // waits for this handler, and making a web view needs that thread.
            e.Handled = true;
            if (e.Request is { } u) Dispatcher.UIThread.Post(() => BrowserHost.Instance.OpenBeside(this, u.ToString()));
        };
        Slot.SizeChanged += (_, _) => { if (Viewport is not null) ApplyViewport(); };
        _address.AttachedToVisualTree += (_, _) => { if (_wantsAddressFocus) Dispatcher.UIThread.Post(FocusAddress, DispatcherPriority.Background); };
    }

    public string Id { get; }
    /// <summary>The terminal tab whose dock the pane belongs to.</summary>
    public string? Terminal { get; set; }
    public string Placement { get; set; } = "dock";
    public NativeWebView Web { get; }
    /// <summary>Where the web view sits while the pane is on screen.</summary>
    public Panel Slot { get; }
    public string Url { get; private set; }
    public string Title { get; private set; } = "";
    public bool Loading { get; private set; } = true;
    public int Loads { get; private set; }
    public bool KeepAlive { get; private set; }
    public string? Profile { get; }
    public bool Private { get; }
    public double Zoom { get; private set; } = 1;
    public BrowserDialogInfo? Dialog { get; private set; }
    public bool DevToolsOpen { get; private set; }
    public bool DesignMode { get; private set; }
    public (int Width, int Height)? Viewport { get; private set; }
    public DateTime Created { get; } = DateTime.Now;

    /// <summary>Title, address, loading or state changed (the dock's tabs follow it).</summary>
    public event Action? Changed;
    public event Action? LoadFinished;

    public string Label => Title is { Length: > 0 } t ? t : Url is "about:blank" ? "New tab" : Url;

    private static Button Tool(Geometry icon, string tip, Action onClick)
    {
        var b = new Button { Content = new PathIcon { Data = icon, Width = 14, Height = 14 }, Classes = { "flat", "tool" }, Padding = new Thickness(6, 5), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void UpdateToolbar()
    {
        // Never ask the web view itself here: on Linux that waits on the GLib thread, which may be waiting on this one.
        _back.IsEnabled = _canBack;
        _forward.IsEnabled = _canForward;
        _reloadIcon.Data = Loading ? Icons.Close : Icons.Refresh;
        ToolTip.SetTip(_reload, Loading ? "Stop loading" : "Reload (Ctrl+R; Ctrl+Shift+R ignores the cache)");
        _progress.IsVisible = Loading;
        if (!_addressEditing && !_address.IsFocused) _address.Text = Url is "about:blank" ? "" : Url;
        _address.PlaceholderText = "Search or type an address";
        ((PathIcon)_keep.Content!).Data = KeepAlive ? Icons.Pin : Icons.PinOutline;
        ((PathIcon)_keep.Content!).Foreground = KeepAlive ? Ui.Tk("accent") : null;
        ToolTip.SetTip(_keep, KeepAlive
            ? "Keep alive is on: the page keeps running, and believes it is visible, while another tab or page is in front. Click to turn it off."
            : "Keep alive is off: the page may slow down while it is out of view. Click to keep it running.");
        ((PathIcon)_pick.Content!).Foreground = DesignMode ? Ui.Tk("accent") : null;
        _errorDot.IsVisible = _errorsOnPage > 0;
        ToolTip.SetTip(_devtools, _errorsOnPage > 0 ? $"Developer tools (F12) — {_errorsOnPage} error{(_errorsOnPage == 1 ? "" : "s")} on this page" : "Developer tools (F12)");
        _audio.IsVisible = _audible || _muted;
        ((PathIcon)_audio.Content!).Data = _muted ? Icons.VolumeOff : Icons.VolumeHigh;
        ToolTip.SetTip(_audio, _muted ? "Unmute this page" : "Mute this page");
        _blank.Text = Loading && Loads == 0 ? "Loading…" : "";
        Changed?.Invoke();
    }

    /// <summary>A new page is on its way: what belonged to the old one goes.</summary>
    private void NewDocument()
    {
        _errorsOnPage = 0;
        _crashBar.IsVisible = false;
        DesignMode = false;
    }

    private void FinishLoad()
    {
        Loads++;
        LoadFinished?.Invoke();
    }

    private void SetUrl(string url)
    {
        if (url == Url) return;
        Url = url;
        UpdateToolbar();
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(Ui.Item("Reload, ignoring the cache", () => _ = GoAsync("hardreload"), icon: Icons.Refresh));
        m.Items.Add(Ui.Item("Zoom in", () => _ = ZoomAsync("in")));
        m.Items.Add(Ui.Item("Zoom out", () => _ = ZoomAsync("out")));
        m.Items.Add(Ui.Item($"Actual size ({Math.Round(Zoom * 100)}%)", () => _ = ZoomAsync("reset"), Math.Abs(Zoom - 1) > 0.001));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Copy address", () => _ = App.Services.Host.SetClipboardAsync(Url), icon: Icons.ContentCopy));
        m.Items.Add(Ui.Item("Open in the system browser", () => Avamux.Core.Shell.Open(Url), Url.StartsWith("http"), Icons.OpenInNew));
        m.Items.Add(Ui.Item("Save a screenshot", async () =>
        {
            var file = Path.Combine(App.Services.Paths.DataDir, "browser", "captures", $"{Id}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            try { await ScreenshotAsync(file, false); Ui.Toast(this, "Screenshot saved", file); } catch (Exception ex) { Ui.Toast(this, "No screenshot", ex.Message); }
        }, WebKitGtk.Supported, Icons.Camera));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item(DevToolsOpen ? "Close developer tools" : "Developer tools", () => _ = DevToolsAsync("toggle"), icon: Icons.Bug));
        if (WebKitGtk.Supported)
        {
            m.Items.Add(Ui.Item(CanDockInspector ? "Developer tools inside the pane" : $"Developer tools inside the pane (needs {InspectorDockWidth} px)", () => _ = DevToolsAsync("attach"), CanDockInspector));
            m.Items.Add(Ui.Item("Developer tools in a window", () => _ = DevToolsAsync("detach")));
        }
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item(KeepAlive ? "Stop keeping alive" : "Keep alive while out of view", () => _ = SetKeepAliveAsync(!KeepAlive), icon: Icons.Pin));
        if (Placement == "dock") m.Items.Add(Ui.Item("Move to a window", () => BrowserHost.Instance.Move(Id, "window", null), icon: Icons.OpenInNew));
        else if (Terminal is not null) m.Items.Add(Ui.Item("Move back beside the terminal", () => BrowserHost.Instance.Move(Id, "dock", null)));
        m.Items.Add(Ui.Item("New tab", () => BrowserHost.Instance.OpenBeside(this, null), icon: Icons.Plus));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Close", () => BrowserHost.Instance.Close(Id), icon: Icons.Close));
        return m;
    }

    // ---- page lifecycle ----

    private async Task OnAdapterCreatedAsync()
    {
        try
        {
            if (WebKitGtk.Supported && Web.TryGetPlatformHandle() is IGtkWebViewPlatformHandle { WebKitWebView: var view } && view != IntPtr.Zero)
            {
                _view = view;
                await WebKitGtk.ConnectAsync(view, this);
                _userScript = await WebKitGtk.AddUserScriptAsync(view, BrowserService.AgentScript(KeepAlive));
                await InstallInitScriptsAsync();
                if (Math.Abs(Zoom - 1) > 0.001) await WebKitGtk.SetZoomAsync(view, Zoom);
            }
            else if (WebKitMac.Supported && Web.TryGetPlatformHandle() is IAppleWKWebViewPlatformHandle { WKWebView: var wk } && wk != IntPtr.Zero)
            {
                // The agent and the init scripts as real user scripts: at document start, in every frame. Without
                // this the agent only arrives with the first command on a page, and never in cross-origin frames.
                _wkWebView = wk;
                InstallMacScripts();
            }
        }
        catch (Exception ex) { Log.Warn("browser", $"Browser pane {Id}: WebKit set-up failed: {ex.Message}"); }
        _adapterReady = true;
        Web.Navigate(new Uri(_initialUrl));
    }

    public void Navigate(string text)
    {
        string url;
        try { url = App.Services.Browser.Normalize(text); }
        catch (Exception ex) { Ui.Toast(this, "Not opened", ex.Message); return; }
        NavigateTo(url);
    }

    public void NavigateTo(string url)
    {
        SetUrl(url);
        if (!_adapterReady) { _initialUrl = url; return; }
        Loading = true;
        UpdateToolbar();
        Web.Navigate(new Uri(url));
    }

    public async Task GoAsync(string action)
    {
        switch (action)
        {
            case "back" or "forward":
            {
                var back = action == "back";
                var went = _view != IntPtr.Zero ? await WebKitGtk.GoAsync(_view, back) : back ? Web.GoBack() : Web.GoForward();
                if (!went) throw new BrowserException("invalid_state", $"There is no page to go {action} to.");
                Loading = true;
                break;
            }
            case "reload": Loading = true; Web.Refresh(); break;
            case "hardreload":
                Loading = true;
                if (_view != IntPtr.Zero) await WebKitGtk.HardReloadAsync(_view); else Web.Refresh();
                break;
            case "stop": Web.Stop(); Loading = false; break;
        }
        UpdateToolbar();
    }

    public async Task<string?> EvalAsync(string script)
    {
        if (!_adapterReady) throw new BrowserException("invalid_state", "The page has not started yet.");
        return await Web.InvokeScript(script);
    }

    private async Task RefreshTitleAsync()
    {
        try
        {
            var t = await Web.InvokeScript("document.title");
            if (t is not null && t.Length >= 2 && t[0] == '"') t = JsonNode.Parse(t)?.GetValue<string>();
            if (t is not null && t != Title) { Title = t; UpdateToolbar(); }
        }
        catch { /* not there yet */ }
    }

    public void FocusPage()
    {
        if (Web.Parent == Slot) WebViews.FocusNative(Web);
    }

    private bool _wantsAddressFocus;

    /// <summary>Puts the keyboard in the address bar, now or as soon as the pane is on screen (a new pane is not yet).</summary>
    public void FocusAddress()
    {
        if (!_address.IsEffectivelyVisible || TopLevel.GetTopLevel(_address) is null) { _wantsAddressFocus = true; return; }
        _wantsAddressFocus = false;
        _address.Focus(); // the window takes the keyboard back from the page (see AppWindow)
        _address.SelectAll();
    }

    // ---- features ----

    public async Task SetKeepAliveAsync(bool on)
    {
        if (KeepAlive == on) return;
        KeepAlive = on;
        UpdateToolbar();
        if (_view != IntPtr.Zero)
        {
            // The document-start script carries the setting into later pages; the current page is told directly.
            await WebKitGtk.RemoveUserScriptAsync(_view, _userScript);
            _userScript = await WebKitGtk.AddUserScriptAsync(_view, BrowserService.AgentScript(on));
            await InstallInitScriptsAsync(); // after the agent again, so it still sees what they log
        }
        else if (_wkWebView != IntPtr.Zero)
        {
            try { InstallMacScripts(); } catch (Exception ex) { Log.Warn("browser", $"Browser pane {Id}: could not reinstall the page scripts: {ex.Message}"); }
        }
        try { await Web.InvokeScript(BrowserService.Wrap($"window.__avx.keepAlive({(on ? "true" : "false")})", on)); } catch { }
        BrowserHost.Instance.Sync();
    }

    /// <summary>Replaces the scripts that run at the start of every page (after the agent, before the page's own).</summary>
    public async Task SetInitScriptsAsync(IReadOnlyList<BrowserInitScript> scripts)
    {
        if (!WebKitGtk.Supported && !WebKitMac.Supported)
            throw new BrowserException("not_supported", "Scripts at the start of every page need WebKitGTK or WKWebView; on this system run them after the load with `avamux browser eval`.");
        _initScripts = scripts.ToList();
        // Otherwise when the view is set up.
        if (_view != IntPtr.Zero) await InstallInitScriptsAsync();
        else if (_wkWebView != IntPtr.Zero) InstallMacScripts();
    }

    /// <summary>
    /// WKWebView can only drop its scripts all at once, so the agent and the init scripts are put in again as a whole
    /// list every time (after the scripts that were there before avamux's, which <see cref="WebKitMac"/> keeps).
    /// </summary>
    private void InstallMacScripts()
    {
        var list = new List<(string, bool)> { (BrowserService.AgentScript(KeepAlive), false) };
        list.AddRange(_initScripts.Select(s => (s.Source, s.MainFrameOnly)));
        WebKitMac.SetUserScripts(_wkWebView, list);
    }

    private async Task InstallInitScriptsAsync()
    {
        foreach (var handle in _initScriptHandles) await WebKitGtk.RemoveUserScriptAsync(_view, handle);
        _initScriptHandles.Clear();
        foreach (var script in _initScripts) _initScriptHandles.Add(await WebKitGtk.AddUserScriptAsync(_view, script.Source, script.MainFrameOnly));
    }

    public async Task<string> DevToolsAsync(string action)
    {
        if (!App.Services.Config.Settings.Browser.DeveloperTools)
            throw new BrowserException("not_allowed", "Developer tools are switched off (browser.developerTools in Settings); turn them on and open a new pane.");
        if (action == "toggle") action = DevToolsOpen ? "close" : "open";
        if (action == "status") return DevToolsOpen ? "open" : "closed";
        if (_view != IntPtr.Zero)
        {
            // WebKit only docks the inspector into a view at least 750 px wide; narrower, it opens in a window of its own.
            if (action == "attach" && !CanDockInspector)
                throw new BrowserException("invalid_state", $"The pane is {Math.Round(Slot.Bounds.Width)} px wide; WebKit docks the inspector only into panes at least {InspectorDockWidth} px wide. Widen the dock or move the pane to a window (`avamux browser move window`).");
            if (action == "open" && CanDockInspector) action = "attach";
            DevToolsOpen = await WebKitGtk.InspectorAsync(_view, action, DevToolsOpen);
        }
        else if (action is "open" or "attach" or "detach")
        {
            DevToolsOpen = WebViews.OpenDevTools(Web);
            if (!DevToolsOpen) throw new BrowserException("not_supported", "Open the inspector from the page's context menu (right-click → Inspect Element).");
        }
        else throw new BrowserException("not_supported", "Close the inspector with its own close button.");
        UpdateToolbar();
        return DevToolsOpen ? "open" : "closed";
    }

    private const int InspectorDockWidth = 750;
    private bool CanDockInspector => Slot.Bounds.Width >= InspectorDockWidth;

    public async Task<double> ZoomAsync(string action)
    {
        var z = action switch
        {
            "in" => Step(Zoom, 1),
            "out" => Step(Zoom, -1),
            "reset" => 1,
            "status" or "" => Zoom,
            _ => double.TryParse(action.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? (action.EndsWith('%') || v > 5 ? v / 100 : v) : throw new BrowserException("invalid_params", "zoom in, out, reset, or a factor such as 1.25 (or 125%)."),
        };
        z = Math.Clamp(z, 0.25, 5);
        if (Math.Abs(z - Zoom) < 0.0001 && action is "status" or "") return Zoom;
        Zoom = z;
        await ApplyZoomAsync();
        UpdateToolbar();
        return Zoom;

        static double Step(double z, int dir)
        {
            double[] steps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];
            return dir > 0 ? steps.FirstOrDefault(s => s > z + 0.001, 5) : steps.LastOrDefault(s => s < z - 0.001, 0.25);
        }
    }

    private double _fit = 1;

    private async Task ApplyZoomAsync()
    {
        var level = Zoom * (Viewport is null ? 1 : _fit);
        if (_view != IntPtr.Zero) await WebKitGtk.SetZoomAsync(_view, level);
        else if (_adapterReady) { try { await Web.InvokeScript($"document.documentElement.style.zoom='{level.ToString(System.Globalization.CultureInfo.InvariantCulture)}'"); } catch { } }
    }

    public string? SetViewport(int? width, int? height)
    {
        if (width is null || height is null)
        {
            Viewport = null;
            Web.Width = double.NaN; Web.Height = double.NaN;
            Web.HorizontalAlignment = HorizontalAlignment.Stretch; Web.VerticalAlignment = VerticalAlignment.Stretch;
            _ = ApplyZoomAsync();
            UpdateToolbar();
            return null;
        }
        if (!WebKitGtk.Supported) throw new BrowserException("not_supported", "An exact viewport needs WebKitGTK's zoom (Linux).");
        Viewport = (width.Value, height.Value);
        ApplyViewport();
        UpdateToolbar();
        return $"{width}x{height}" + (_fit < 0.999 ? $" (shown at {Math.Round(_fit * 100)}% to fit the pane)" : "");
    }

    /// <summary>Sizes the web view to the requested viewport, shrunk with page zoom when the pane is smaller.</summary>
    private void ApplyViewport()
    {
        if (Viewport is not { } vp) return;
        var w = Slot.Bounds.Width > 0 ? Slot.Bounds.Width : vp.Width;
        var h = Slot.Bounds.Height > 0 ? Slot.Bounds.Height : vp.Height;
        _fit = Math.Min(1, Math.Min(w / vp.Width, h / vp.Height));
        if (Web.Parent == Slot)
        {
            Web.Width = Math.Floor(vp.Width * _fit); Web.Height = Math.Floor(vp.Height * _fit);
            Web.HorizontalAlignment = HorizontalAlignment.Center; Web.VerticalAlignment = VerticalAlignment.Center;
        }
        _ = ApplyZoomAsync();
    }

    public async Task<bool> SetDesignModeAsync(bool on)
    {
        var raw = await Web.InvokeScript(BrowserService.Wrap($"window.__avx.designMode({(on ? "true" : "false")})", KeepAlive));
        DesignMode = raw is not null && JsonNode.Parse(raw) is JsonObject o && o["v"]?.GetValue<bool>() == true;
        UpdateToolbar();
        if (DesignMode) FocusPage();
        return DesignMode;
    }

    public async Task ScreenshotAsync(string file, bool fullPage)
    {
        if (_view == IntPtr.Zero) throw new BrowserException("not_supported", "Screenshots of the page need WebKitGTK (Linux).");
        await WebKitGtk.SnapshotAsync(_view, file, fullPage);
    }

    public async Task PdfAsync(string file)
    {
        await using var pdf = await Web.PrintToPdfStreamAsync();
        await using var f = File.Create(file);
        await pdf.CopyToAsync(f);
    }

    public async Task<bool> AnswerDialogAsync(bool accept, string? text)
    {
        if (Dialog is null || _dialog == IntPtr.Zero) return false;
        var d = _dialog;
        var type = Dialog.Type;
        _dialog = IntPtr.Zero;
        Dialog = null;
        _dialogBar.IsVisible = false;
        UpdateToolbar();
        await WebKitGtk.AnswerDialogAsync(d, type, accept, text);
        return true;
    }

    // ---- messages from the page ----

    private void OnMessage(string? body)
    {
        if (string.IsNullOrEmpty(body)) return;
        JsonObject? m;
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonValue v && v.TryGetValue<string>(out var inner)) node = JsonNode.Parse(inner);
            m = node as JsonObject;
        }
        catch { return; }
        // Anything can post here; only the agent script's messages count, and they only ever carry data or UI actions.
        if (m is null || m["avx"]?.GetValue<int>() != 1) return;
        switch (m["k"]?.GetValue<string>())
        {
            case "log":
                App.Services.Browser.Record(Id, m);
                if (m["top"]?.GetValue<bool>() != false && m["items"] is JsonArray items)
                {
                    var errors = items.OfType<JsonArray>().Count(i => i.Count > 0 && i[0]?.GetValue<string>() == "errors");
                    if (errors > 0) { _errorsOnPage += errors; UpdateToolbar(); }
                }
                break;
            case "key" when m["top"]?.GetValue<bool>() != false:
                OnPageKey(m["action"]?.GetValue<string>() ?? "");
                break;
            case "pick" when DesignMode && m["data"] is JsonObject pick:
                _ = CopyPickAsync(pick);
                break;
            case "hello" when m["top"]?.GetValue<bool>() != false && !WebKitGtk.Supported:
                _ = RefreshTitleAsync();
                break;
        }
    }

    private void OnPageKey(string action)
    {
        switch (action)
        {
            case "devtools": _ = Try(DevToolsAsync("toggle")); break;
            case "address": FocusAddress(); break;
            case "reload": _ = Try(GoAsync("reload")); break;
            case "hardreload": _ = Try(GoAsync("hardreload")); break;
            case "back": _ = Try(GoAsync("back")); break;
            case "forward": _ = Try(GoAsync("forward")); break;
            case "zoomin": _ = ZoomAsync("in"); break;
            case "zoomout": _ = ZoomAsync("out"); break;
            case "zoomreset": _ = ZoomAsync("reset"); break;
            case "pick": _ = SetDesignModeAsync(!DesignMode); break;
            case "pickstop": _ = SetDesignModeAsync(false); break;
            case "hide":
                // Ctrl+Shift+L, as in the terminal: the browser goes away and the keyboard goes back to the tab.
                if (Placement == "dock" && Terminal is { } t) { BrowserHost.Instance.SetDockHidden(t, true); App.Services.Host.SelectTerminal(t); }
                break;
        }
    }

    private async Task Try(Task t)
    {
        try { await t; } catch (BrowserException ex) { Ui.Toast(this, "Browser", ex.Message[(ex.Code.Length + 2)..]); } catch (Exception ex) { Log.Warn("browser", ex.Message); }
    }

    /// <summary>What design mode copies: enough for an agent to find the element and see what it looks like.</summary>
    private async Task CopyPickAsync(JsonObject p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Element on {p["url"]} (avamux browser pane {Id}):");
        sb.AppendLine($"- element: {p["element"]}");
        sb.AppendLine($"- selector: {p["selector"]}");
        if (p["text"]?.ToString() is { Length: > 0 } text) sb.AppendLine($"- text: {text}");
        if (p["box"] is JsonObject box) sb.AppendLine($"- box: {box["width"]}×{box["height"]} at {box["x"]},{box["y"]}");
        if (p["styles"] is JsonObject st && st.Count > 0) sb.AppendLine("- styles: " + string.Join("; ", st.Select(kv => $"{kv.Key}: {kv.Value}")));
        sb.AppendLine("```html").AppendLine(p["html"]?.ToString()).AppendLine("```");
        await App.Services.Host.SetClipboardAsync(sb.ToString());
        Ui.Toast(this, "Copied for your agent", $"{p["element"]} — paste it into the terminal.");
    }

    // ---- WebKitGTK events (GLib thread) ----

    void IWebKitEvents.OnDialog(IntPtr dialog, string type, string message, string? defaultText) => Dispatcher.UIThread.Post(() =>
    {
        if (_dialog != IntPtr.Zero) _ = WebKitGtk.AnswerDialogAsync(_dialog, Dialog?.Type ?? "alert", false, null);
        _dialog = dialog;
        Dialog = new BrowserDialogInfo(type, message, defaultText);
        ShowDialogBar();
        UpdateToolbar();
    });

    void IWebKitEvents.OnProperty(string name, string? text, double number, bool flag) => Dispatcher.UIThread.Post(() =>
    {
        switch (name)
        {
            case "title": Title = text ?? ""; break;
            case "uri": if (text is not null) Url = text; break;
            case "estimated-load-progress": _progress.Value = number; break;
            case "is-loading":
                var was = Loading;
                Loading = flag;
                if (flag) NewDocument();
                if (was && !flag) FinishLoad();
                break;
            case "can-go-back": _canBack = flag; break;
            case "can-go-forward": _canForward = flag; break;
            case "is-playing-audio": _audible = flag; break;
            case "is-muted": _muted = flag; break;
        }
        UpdateToolbar();
    });

    void IWebKitEvents.OnCrashed(string reason) => Dispatcher.UIThread.Post(() =>
    {
        Log.Warn("browser", $"Browser pane {Id}: the page {reason} ({Url})");
        _crashBar.Background = Ui.Tk("bgDeep");
        _crashBar.BorderBrush = Ui.Tk("error");
        _crashBar.BorderThickness = new Thickness(0, 0, 0, 2);
        _crashBar.Child = new DockPanel
        {
            Children =
            {
                Ui.TextButton("Reload", () => { _crashBar.IsVisible = false; _ = GoAsync("reload"); }).Dock(Dock.Right),
                new TextBlock { Text = $"The page {reason}.", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Tk("error") },
            },
        };
        _crashBar.IsVisible = true;
        Loading = false;
        UpdateToolbar();
    });

    void IWebKitEvents.OnInspectorClosed() => Dispatcher.UIThread.Post(() => { DevToolsOpen = false; UpdateToolbar(); });

    private void ShowDialogBar()
    {
        if (Dialog is not { } d) { _dialogBar.IsVisible = false; return; }
        var input = new TextBox { Text = d.DefaultText ?? "", MinWidth = 160, IsVisible = d.Type == "prompt", Margin = new Thickness(0, 6, 0, 0) };
        var ok = Ui.TextButton(d.Type == "beforeunload" ? "Leave" : "OK", () => _ = AnswerDialogAsync(true, input.Text));
        var cancel = Ui.TextButton(d.Type == "beforeunload" ? "Stay" : "Cancel", () => _ = AnswerDialogAsync(false, null));
        cancel.IsVisible = d.Type != "alert";
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = AnswerDialogAsync(true, input.Text); } };
        var host = Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : "This page";
        _dialogBar.Background = Ui.Tk("panel");
        _dialogBar.BorderBrush = Ui.Tk("accent");
        _dialogBar.BorderThickness = new Thickness(0, 0, 0, 2);
        _dialogBar.Child = new DockPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top, Children = { cancel, ok } }.Dock(Dock.Right),
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = d.Type == "beforeunload" ? $"{host} asks: leave this page? Changes may not be saved." : $"{host} says:", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                        new TextBlock { Text = d.Message, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, Margin = new Thickness(0, 2, 8, 0), IsVisible = d.Type != "beforeunload" },
                        input,
                    },
                },
            },
        };
        _dialogBar.IsVisible = true;
        if (d.Type == "prompt") Dispatcher.UIThread.Post(() => { input.Focus(); input.SelectAll(); }, DispatcherPriority.Background);
    }

    public BrowserSurfaceInfo Info(bool onScreen, bool active) => new(
        Id, Url, Title, Loading, Terminal, Placement, active, onScreen, KeepAlive, Private ? "(private)" : Profile, Zoom, Loads, Dialog, DevToolsOpen, DesignMode,
        Viewport is { } v ? $"{v.Width}x{v.Height}" : null);

    /// <summary>Lets go of the native side before the pane goes away.</summary>
    public void Shutdown()
    {
        if (_dialog != IntPtr.Zero) { _ = WebKitGtk.AnswerDialogAsync(_dialog, Dialog?.Type ?? "alert", false, null); _dialog = IntPtr.Zero; }
        if (_view != IntPtr.Zero)
        {
            if (DevToolsOpen) _ = WebKitGtk.InspectorAsync(_view, "close", true);
            WebKitGtk.Disconnect(_view);
            _view = IntPtr.Zero;
        }
        if (_wkWebView != IntPtr.Zero) { WebKitMac.Forget(_wkWebView); _wkWebView = IntPtr.Zero; }
    }
}
