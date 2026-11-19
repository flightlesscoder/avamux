using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avamux.Views;

namespace Avamux.Browser;

/// <summary>The browser panes beside one terminal tab: a row of tabs over the pane in front.</summary>
public sealed class BrowserDock : UserControl
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private string? _terminal;

    public BrowserDock()
    {
        var add = Ui.IconButton(Icons.Plus, "New browser tab", () => { if (_terminal is not null) NewTab(_terminal); });
        var hide = Ui.IconButton(Icons.ChevronRight, "Hide the browser (it keeps its pages; the globe button brings it back)", () => { if (_terminal is not null) BrowserHost.Instance.SetDockHidden(_terminal, true); });
        var strip = new Border
        {
            Background = Ui.Tk("bgDeep"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(4, 3, 4, 0),
            Child = new DockPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { add, hide } }.Dock(Dock.Right),
                    new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _tabs },
                },
            },
        };
        Content = new DockPanel { Children = { strip.Dock(Dock.Top), _content } };
    }

    public static void NewTab(string terminal)
    {
        var url = App.Services.Browser.Normalize(App.Services.Config.Settings.Browser.HomePage);
        var s = BrowserHost.Instance.Create(url, terminal, false, null, null, false);
        BrowserHost.Instance.Show(s, focus: false);
        Avalonia.Threading.Dispatcher.UIThread.Post(s.FocusAddress, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Shows the panes of a terminal tab. Returns whether there is anything to show.</summary>
    public bool ShowFor(string? terminal)
    {
        _terminal = terminal;
        var host = BrowserHost.Instance;
        var docked = host.DockedFor(terminal);
        var active = host.ActiveFor(terminal);
        if (!ReferenceEquals(_content.Content, active))
        {
            // The pane leaving the dock takes its page to the parking layer first, or the page would be lost.
            if (_content.Content is BrowserSurface old) { host.ParkBeforeDetach(old); _content.Content = null; }
            if (active is not null)
            {
                // A pane can only be in one place: take it from wherever it was shown before (another window's dock).
                if (active.Parent is ContentControl other && other != _content)
                {
                    var otherWindow = TopLevel.GetTopLevel(other);
                    host.ParkBeforeDetach(active);
                    other.Content = null;
                    if (otherWindow != TopLevel.GetTopLevel(this))
                    {
                        // From another window: come back for it once that window has laid out without it.
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowFor(_terminal), Avalonia.Threading.DispatcherPriority.Loaded);
                        active = null;
                    }
                }
                if (active is not null) _content.Content = active;
            }
        }
        _tabs.Children.Clear();
        foreach (var s in docked) _tabs.Children.Add(Tab(s, s == active));
        host.Sync();
        return docked.Count > 0;
    }

    /// <summary>Lets go of the pane shown, before this dock goes away (its window is closing).</summary>
    public void Release()
    {
        if (_content.Content is BrowserSurface s) { BrowserHost.Instance.ParkBeforeDetach(s, windowClosing: true); _content.Content = null; }
    }

    private static Control Tab(BrowserSurface s, bool selected)
    {
        var title = new TextBlock
        {
            Text = s.Label, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            Foreground = selected ? Ui.Strong : Ui.Tk("textMute"),
        };
        var mark = new PathIcon
        {
            Data = s.Loading ? Icons.Refresh : s.KeepAlive ? Icons.Pin : Icons.Web, Width = 11, Height = 11, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = s.Dialog is not null ? Ui.Tk("warn") : s.KeepAlive ? Ui.Tk("accent") : Ui.Tk("faint"),
        };
        var close = new Button
        {
            Content = new PathIcon { Data = Icons.Close, Width = 9, Height = 9 }, Classes = { "flat" }, Padding = new Thickness(4), Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Opacity = selected ? 0.9 : 0.5,
        };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => BrowserHost.Instance.Close(s.Id);
        var tab = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, title, close } },
            Padding = new Thickness(8, 4, 4, 5), CornerRadius = Ui.R(6, 6, 0, 0), Cursor = new Cursor(StandardCursorType.Hand),
            Background = selected ? Ui.Tk("bgEditor") : Brushes.Transparent,
            BorderBrush = selected ? Ui.Tk("accent") : Brushes.Transparent, BorderThickness = new Thickness(0, 2, 0, 0),
        };
        ToolTip.SetTip(tab, $"{s.Id} — {s.Url}" + (s.KeepAlive ? "\nKept alive while out of view" : "") + (s.Dialog is { } d ? $"\nWaiting on a {d.Type}" : ""));
        tab.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(tab).Properties;
            if (p.IsMiddleButtonPressed) { BrowserHost.Instance.Close(s.Id); return; }
            if (p.IsLeftButtonPressed && !selected) BrowserHost.Instance.SetActive(s);
        };
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Move to a window", () => BrowserHost.Instance.Move(s.Id, "window", null), icon: Icons.OpenInNew));
        menu.Items.Add(Ui.Item(s.KeepAlive ? "Stop keeping alive" : "Keep alive while out of view", () => _ = s.SetKeepAliveAsync(!s.KeepAlive), icon: Icons.Pin));
        menu.Items.Add(Ui.Item("Copy address", () => _ = App.Services.Host.SetClipboardAsync(s.Url), icon: Icons.ContentCopy));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Close", () => BrowserHost.Instance.Close(s.Id)));
        tab.ContextMenu = menu;
        return tab;
    }
}

/// <summary>A browser pane in a window of its own.</summary>
public sealed class BrowserWindow : AppWindow
{
    private readonly BrowserSurface _surface;

    public BrowserWindow(BrowserSurface surface)
    {
        _surface = surface;
        Icon = AppIcon.Window;
        Width = 1000; Height = 720; MinWidth = 360; MinHeight = 240;
        Body = surface;
        UpdateTitle();
        surface.Changed += UpdateTitle;
        Closed += (_, _) => surface.Changed -= UpdateTitle;
        Activated += (_, _) => BrowserHost.Instance.Sync();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) BrowserHost.Instance.Sync(); };
    }

    private void UpdateTitle() => Title = $"{_surface.Label} — avamux browser";

    /// <summary>Takes the pane out of the window (its page goes to the parking layer first).</summary>
    public void Detach()
    {
        if (Body is BrowserSurface s) { BrowserHost.Instance.ParkBeforeDetach(s, windowClosing: true); Body = null; }
    }
}
