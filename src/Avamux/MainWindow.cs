using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avamux.Core.Commands;
using Avamux.Core.Nav;
using Avamux.Views;

namespace Avamux;

public sealed class MainWindow : AppWindow
{
    private sealed record NavItem(string Key, string Label, string Tip, Geometry Icon);

    private static readonly NavItem[] NavItems =
    [
        new("terminals", "Terminals", "Terminals", Icons.Console),
        new("search", "Search", "Search everything", Icons.Magnify),
        new("today", "Today", "Open today's daily note in the wiki", Icons.CalendarToday),
        new("settings", "Settings", "Settings", Icons.Cog),
        new("watching", "Watching", "Watching", Icons.Eye),
        new("events", "Events", "Events", Icons.CalendarClock),
        new("dashboard", "Dashboard", "Dashboard", Icons.ViewDashboard),
        new("logs", "Logs", "Logs", Icons.TextBoxOutline),
        new("wiki", "Wiki", "Wiki", Icons.BookOpenVariant),
        new("repositories", "Repos", "Repositories", Icons.SourceBranch),
        new("interactive", "C# Interactive", "C# Interactive: run C# inside the running avamux", Icons.CodeBraces),
        new("caffeine", "Caffeine", "Caffeine: keep the computer awake", Icons.Coffee),
        new("system", "System", "System: CPU, memory, GPU, and what takes the space on each disk", Icons.Chip),
        new("chat", "AI Chat", "AI Chat", Icons.Forum),
        new("workloads", "Workloads", "Workloads: providers, work queues and models", Icons.Server),
    ];

    private readonly Dictionary<string, Control> _pages = new();
    private readonly ListBox _nav = new() { Classes = { "nav" }, Background = Brushes.Transparent, Padding = new Thickness(0, 8), SelectionMode = SelectionMode.Single };
    private readonly Panel _pageHost = new();
    private readonly Grid _main;
    private readonly ContentControl _boss = new() { IsVisible = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private string? _realTitle;
    private readonly List<Window> _hiddenForBoss = [];
    private readonly StackPanel _toasts = new() { Spacing = 8 };
    private readonly Popup _toastPopup;
    private string _navSignature = "";
    private bool _buildingNav;
    /// <summary>What each custom page that has been opened was running, to tell when it has to be started afresh.</summary>
    private readonly Dictionary<string, string> _customSignatures = new();

    private static NavService Custom => App.Services.Nav;

    public MainWindow()
    {
        Title = "avamux";
        Icon = AppIcon.Window;
        Width = 1320; Height = 840; MinWidth = 720; MinHeight = 420;
        // Toasts live in a popup (its own window) so they show above the native web views.
        _toastPopup = new Popup
        {
            PlacementTarget = _pageHost, Placement = PlacementMode.AnchorAndGravity, PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomRight,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.TopLeft, HorizontalOffset = -18, VerticalOffset = -18, IsLightDismissEnabled = false, Child = _toasts,
        };
        _main = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                new Border { Background = Ui.Tk("bgDeep"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 1, 0), Child = _nav },
                _pageHost.At(0, 1),
                _toastPopup.At(0, 1),
            },
        };
        // The boss screen sits over everything; the pages stay where they are (hidden), so terminals keep running.
        Body = new Panel { Children = { _main, _boss } };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (BossKey() is { } key && key.Matches(e) || _boss.IsVisible && e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; ToggleBoss(null); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        App.Services.Caffeine.Changed += () => Dispatcher.UIThread.Post(BuildNav);
        _nav.SelectionChanged += (_, _) =>
        {
            if (_buildingNav || _nav.SelectedItem is not ListBoxItem { Tag: string key }) return;
            if (key == "today") Dispatcher.UIThread.Post(OpenToday); else Show(key);
        };
        // Terminals exists from the start so sessions created over IPC have a view to attach to.
        Terminals = new TerminalsPage();
        _pages["terminals"] = Terminals;
        _pageHost.Children.Add(Terminals);
        BuildNav();
        App.Services.Config.Changed += () => Dispatcher.UIThread.Post(BuildNav);
        Custom.Changed += () => Dispatcher.UIThread.Post(SyncCustomPages);
        Custom.MarksChanged += () => Dispatcher.UIThread.Post(BuildNav);
        Custom.ReloadRequested += page => Dispatcher.UIThread.Post(() => ReloadCustomPage(page.Key));
        // Looking at the window is looking at the page in it: whatever it wanted attention for has been seen.
        Activated += (_, _) => { TaskbarAlert.Clear(this); if (IsCustom(CurrentPage)) Custom.ClearAttention(CurrentPage); };
        Ui.ThemeChanged += () => (_pages.GetValueOrDefault(CurrentPage) as IPage)?.OnShown();
        var start = App.Services.Config.Settings.Ui.StartPage;
        Navigate(ResolvePage(start) is { } first && IsShown(first) ? first : "terminals");
    }

    public TerminalsPage Terminals { get; }

    public bool BossShowing => _boss.IsVisible;

    private static Avalonia.Input.KeyGesture? BossKey()
    {
        var text = App.Services.Config.Settings.Ui.BossKey;
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return Avalonia.Input.KeyGesture.Parse(text.Trim()); } catch { return null; }
    }

    /// <summary>Shows (true), hides (false) or toggles (null) the boss screen.</summary>
    public void ToggleBoss(bool? show)
    {
        var want = show ?? !_boss.IsVisible;
        if (want == _boss.IsVisible) return;
        var ui = App.Services.Config.Settings.Ui;
        if (want)
        {
            var kind = BossScreen.Kinds.Contains(ui.BossScreen.Trim().ToLowerInvariant()) ? ui.BossScreen.Trim().ToLowerInvariant() : "spreadsheet";
            _boss.Content = BossScreen.Build(kind);
            _realTitle = Title;
            Title = BossScreen.TitleFor(kind);
            _toastPopup.IsOpen = false;
            _main.IsVisible = false;
            _boss.IsVisible = true;
            App.Services.Sounds.Quiet = true;
            if (ui.BossHidesWindows && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life)
                foreach (var w in life.Windows.Where(w => w != this && w.IsVisible && w.WindowState != WindowState.Minimized).ToList())
                {
                    _hiddenForBoss.Add(w);
                    w.WindowState = WindowState.Minimized;
                }
            _boss.Focus();
        }
        else
        {
            _boss.IsVisible = false;
            _boss.Content = null;
            _main.IsVisible = true;
            Title = _realTitle ?? "avamux";
            App.Services.Sounds.Quiet = false;
            foreach (var w in _hiddenForBoss) w.WindowState = WindowState.Normal;
            _hiddenForBoss.Clear();
            Activate();
            (_pages.GetValueOrDefault(CurrentPage) as IPage)?.OnShown();
        }
    }
    public string CurrentPage { get; private set; } = "terminals";

    public T? Page<T>(string key) where T : Control => GetOrCreate(key) as T;

    /// <summary>The AI pages are only in the navigation when switched on under Settings → AI Workloads.</summary>
    private static bool IsShown(string key)
    {
        if (IsCustom(key)) return Custom.Find(key) is { Enabled: true };
        var ai = App.Services.Config.Settings.Ai;
        return key switch { "chat" => ai.Chat, "workloads" => ai.Workloads, _ => true };
    }

    private static bool IsCustom(string key) => key.StartsWith(CustomPage.KeyPrefix, StringComparison.Ordinal);

    /// <summary>The key of a page given as a built-in name, a custom page's key, or a custom page's name. Null when there is none.</summary>
    private static string? ResolvePage(string page) =>
        page == "today" || CommandRegistry.Pages.Contains(page) ? page : Custom.Find(page) is { Enabled: true } custom ? custom.Key : null;

    private void BuildNav()
    {
        var settings = App.Services.Config.Settings;
        var mini = settings.Ui.NavStyle.Trim().Equals("mini", StringComparison.OrdinalIgnoreCase);
        var custom = Custom.Pages.Where(p => p.Enabled).ToList();
        var caffeine = App.Services.Caffeine.Status;
        var signature = $"{mini}|{settings.Ai.Chat}|{settings.Ai.Workloads}|{caffeine.Mode}|{caffeine.Holding}|" +
                        string.Join("|", custom.Select(p => $"{p.Id}\u001f{p.Name}\u001f{p.Icon}\u001f{p.Kind}\u001f{Custom.Attention(p.Id)}\u001f{Custom.Badge(p.Id)}"));
        if (signature == _navSignature) return;
        _navSignature = signature;
        _buildingNav = true;
        try
        {
            _nav.Items.Clear();
            _nav.Width = mini ? 46 : 92;
            _nav.Classes.Set("mini", mini);
            var items = NavItems.Where(i => IsShown(i.Key)).Select(i => (Item: i, Page: (CustomPage?)null))
                .Concat(custom.Select(p => (Item: new NavItem(p.Key, p.Name, $"{p.Name}\n{p.Target}", CustomPages.IconFor(p)), Page: (CustomPage?)p)));
            foreach (var (item, page) in items)
            {
                var content = new StackPanel { Spacing = 4, Margin = new Thickness(0, mini ? 8 : 9), HorizontalAlignment = HorizontalAlignment.Center };
                Control icon = new PathIcon { Data = item.Icon, Width = mini ? 17 : 22, Height = mini ? 17 : 22 };
                var tip = item.Tip;
                if (item.Key == "caffeine" && caffeine.Mode != Avamux.Core.Awake.CaffeineMode.Off)
                {
                    icon = new Panel { Children = { icon, new Border
                    {
                        Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Ui.Tk(caffeine.Holding ? "ok" : "faint"),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, -9, 0), IsHitTestVisible = false,
                    } } };
                    tip += "\n" + caffeine.Describe(DateTime.Now);
                }
                if (page is not null && Mark(page) is { } mark)
                {
                    icon = new Panel { Children = { icon, mark } };
                    if (Custom.Attention(page.Id) is { } wants) tip += "\n" + (wants.Length > 0 ? wants : "Needs attention");
                }
                content.Children.Add(icon);
                if (!mini) content.Children.Add(new TextBlock { Text = item.Label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 76, TextTrimming = TextTrimming.CharacterEllipsis });
                var entry = new ListBoxItem { Tag = item.Key, Content = content, HorizontalContentAlignment = HorizontalAlignment.Center };
                ToolTip.SetTip(entry, tip);
                ToolTip.SetPlacement(entry, PlacementMode.Right);
                if (mini) ToolTip.SetShowDelay(entry, 200);
                _nav.Items.Add(entry);
            }
            _nav.SelectedItem = NavEntry(CurrentPage);
        }
        finally { _buildingNav = false; }
        // The page that was showing may just have been switched off.
        if (!IsShown(CurrentPage)) Navigate("terminals");
    }

    /// <summary>The mark on a custom page's icon: its badge, or a dot while it wants attention. Null when it has neither.</summary>
    private static Control? Mark(CustomPage page)
    {
        var wants = Custom.Attention(page.Id) is not null;
        var badge = Custom.Badge(page.Id);
        if (!wants && badge is null) return null;
        var mark = new Border
        {
            Background = Ui.Tk(wants ? "warn" : "accent"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, -9, 0), IsHitTestVisible = false,
        };
        if (badge is null) { mark.Width = 9; mark.Height = 9; mark.CornerRadius = new CornerRadius(4.5); }
        else
        {
            mark.CornerRadius = new CornerRadius(7); mark.Padding = new Thickness(4, 0); mark.MinWidth = 14; mark.Height = 14;
            mark.Child = new TextBlock { Text = badge, FontSize = 9.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.OnAccent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        return mark;
    }

    /// <summary>
    /// Brings the window in line with the custom pages after they changed: the navigation is rebuilt, and a page
    /// that was removed, switched off or pointed at something else is closed (its terminal ends, its plugin is
    /// let go). A page that is still wanted is opened again the next time it is shown.
    /// </summary>
    private void SyncCustomPages()
    {
        var wanted = Custom.Pages.Where(p => p.Enabled).ToDictionary(p => p.Key);
        var reopen = false;
        foreach (var key in _pages.Keys.Where(IsCustom).ToList())
        {
            if (wanted.TryGetValue(key, out var page) && _customSignatures.GetValueOrDefault(key) == page.Signature) continue;
            CloseCustomPage(key);
            if (key == CurrentPage) reopen = true;
        }
        BuildNav();
        if (reopen && IsShown(CurrentPage)) Show(CurrentPage);
    }

    private void CloseCustomPage(string key)
    {
        if (!_pages.Remove(key, out var page)) return;
        _customSignatures.Remove(key);
        _pageHost.Children.Remove(page);
        try { (page as IDisposable)?.Dispose(); }
        catch (Exception ex) { Avamux.Core.Logging.Log.Error("page", $"Closing {key} failed", ex); }
    }

    private void ReloadCustomPage(string key)
    {
        if (!_pages.ContainsKey(key)) return; // not open: it starts fresh when it is next shown
        CloseCustomPage(key);
        if (key == CurrentPage && IsShown(key)) Show(key);
    }

    private ListBoxItem? NavEntry(string key) => _nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == key);

    private Control GetOrCreate(string key)
    {
        if (_pages.TryGetValue(key, out var page)) return page;
        if (IsCustom(key) && Custom.Find(key) is { } custom)
        {
            try { page = CustomPages.Create(custom); }
            catch (Exception ex)
            {
                Avamux.Core.Logging.Log.Error("page", $"Could not open '{custom.Name}'", ex);
                page = CustomPages.Message(CustomPages.IconFor(custom), $"{custom.Name} could not be opened", ex.GetBaseException().Message, "Try again", () => Custom.Reload(custom.Id));
            }
            _customSignatures[key] = custom.Signature;
        }
        else page = key switch
        {
            "search" => new SearchPage(),
            "settings" => new SettingsPage(),
            "watching" => new WatchingPage(),
            "events" => new EventsPage(),
            "dashboard" => new DashboardPage(),
            "logs" => new LogsPage(),
            "wiki" => new WikiPage(),
            "repositories" => new RepositoriesPage(),
            "interactive" => new Avamux.Scripting.InteractivePage(),
            "caffeine" => new CaffeinePage(),
            "system" => new SystemPage(),
            "chat" => new ChatPage(),
            "workloads" => new WorkloadsPage(),
            _ => new TextBlock { Text = key },
        };
        page.IsVisible = false;
        _pages[key] = page;
        _pageHost.Children.Add(page);
        return page;
    }

    public void Navigate(string key)
    {
        if (ResolvePage(key) is { } resolved) key = resolved;
        if (key == "today") { OpenToday(); return; }
        // A page whose navigation entry is hidden can still be shown (from the CLI, say); nothing is selected then.
        if (NavEntry(key) is not { } item) { if (CommandRegistry.Pages.Contains(key)) { _buildingNav = true; _nav.SelectedItem = null; _buildingNav = false; Show(key); } return; }
        if (!ReferenceEquals(_nav.SelectedItem, item)) _nav.SelectedItem = item; else Show(key);
    }

    /// <summary>Opens today's daily note in the wiki, creating it when it does not exist yet.</summary>
    public void OpenToday()
    {
        Ui.Try(this, () =>
        {
            var note = App.Services.Wiki.EnsureDailyNote(DateTime.Now, App.Services.Config.Settings.Wiki);
            Navigate("wiki");
            Page<WikiPage>("wiki")?.Open(note);
        }, "Could not open today's note");
    }

    private void Show(string key)
    {
        var page = GetOrCreate(key);
        var leaving = CurrentPage != key && _pages.GetValueOrDefault(CurrentPage) is ICustomPage { } old && old is Control { IsVisible: true } ? old : null;
        foreach (var p in _pages.Values) p.IsVisible = ReferenceEquals(p, page);
        CurrentPage = key;
        leaving?.OnHidden();
        (page as IPage)?.OnShown();
        if (IsCustom(key)) Custom.ClearAttention(key);
    }

    public void Toast(string title, string message)
    {
        var toast = new Border
        {
            Background = Ui.Tk("panelHi"), BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(1),
            CornerRadius = Ui.R(8), Padding = new Thickness(14, 10), MaxWidth = 380,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = Ui.Tk("text"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Tk("textSoft"), IsVisible = message.Length > 0 },
                },
            },
        };
        void Remove()
        {
            _toasts.Children.Remove(toast);
            if (_toasts.Children.Count == 0) _toastPopup.IsOpen = false;
        }
        toast.PointerPressed += (_, _) => Remove();
        _toasts.Children.Add(toast);
        while (_toasts.Children.Count > 4) _toasts.Children.RemoveAt(0);
        _toastPopup.IsOpen = IsVisible;
        DispatcherTimer.RunOnce(Remove, TimeSpan.FromSeconds(6));
    }
}

/// <summary>Pages that want to refresh when they become visible.</summary>
public interface IPage
{
    void OnShown();
}
