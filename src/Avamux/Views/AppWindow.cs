using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Avamux.Core.Theming;

namespace Avamux.Views;

/// <summary>
/// Base for every avamux window. Put the content in <see cref="Body"/>. With the "custom" window chrome the
/// desktop's title bar is switched off and the window draws its own title bar, buttons and resize frame in the
/// theme's colours; with "system" chrome the body simply fills the window.
/// </summary>
public class AppWindow : Window
{
    private const double Grip = 5;
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Border _frame = new();
    private readonly Border _titleBar;
    private readonly TextBlock _titleText = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _min, _max, _close;
    private readonly PathIcon _maxIcon = new() { Width = 10, Height = 10 };
    private bool _custom;

    public AppWindow()
    {
        Background = Ui.Tk("bg");
        _min = ChromeButton(Icons.WindowMinimize, "Minimize", () => WindowState = WindowState.Minimized);
        _max = ChromeButton(Icons.WindowMaximize, "Maximize", ToggleMaximize);
        _max.Content = _maxIcon;
        _close = ChromeButton(Icons.Close, "Close", Close);
        var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Source = AppIcon.Bitmap };
        _titleText.Foreground = Ui.Tk("titleFg");
        _titleBar = new Border
        {
            Background = Ui.Tk("titleBg"), Height = 30, IsVisible = false,
            Child = new DockPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 3, 0), Children = { _min, _max, _close } }.Dock(Dock.Right),
                    icon.Dock(Dock.Left),
                    _titleText,
                },
            },
        };
        _titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2 && CanResize) ToggleMaximize(); else BeginMoveDrag(e);
        };
        _frame.Child = new Panel { Children = { new DockPanel { Children = { _titleBar.Dock(Dock.Top), _body } }, ParkingLot } };
        ParkingLot.SizeChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Avamux.Browser.BrowserHost.Instance.Sync());
        Content = _frame;

        // The frame's border is the resize grip while the window draws its own chrome.
        _frame.PointerMoved += (_, e) => { if (_custom) _frame.Cursor = EdgeAt(e.GetPosition(this)) is { } edge ? new Cursor(CursorFor(edge)) : Cursor.Default; };
        _frame.PointerPressed += (_, e) =>
        {
            if (_custom && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && EdgeAt(e.GetPosition(this)) is { } edge) { BeginResizeDrag(edge, e); e.Handled = true; }
        };

        // Whatever of the window's own takes the keyboard (a text box clicked, an address bar after Ctrl+L, a rename
        // box) must get it from X too, or the keys keep going to the web view (a terminal, a page) that had them.
        // Buttons are left out: clicking one should not pull the keyboard out of the terminal.
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is Button || e.Source is Visual v && (v is NativeWebView || v.FindAncestorOfType<NativeWebView>() is not null)) return;
            WebViews.FocusWindow(this);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        Ui.ThemeChanged += ApplyChrome;
        Closed += (_, _) => Ui.ThemeChanged -= ApplyChrome;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == TitleProperty) _titleText.Text = Title;
            else if (e.Property == WindowStateProperty || e.Property == CanResizeProperty) ApplyChrome();
        };
        ApplyChrome();
    }

    /// <summary>
    /// Where browser panes' web views wait while their pane is out of view, so their pages live on. Nothing in it is
    /// ever seen: see <see cref="Avamux.Browser.BrowserHost"/>.
    /// </summary>
    public Canvas ParkingLot { get; } = new() { IsHitTestVisible = false, ClipToBounds = false };

    /// <summary>Set once the window has agreed to close: nothing should be parked in it any more.</summary>
    public bool IsClosing { get; private set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        IsClosing = true;
        // Browser pages in this window (on screen or parked) would go down with it.
        Avamux.Browser.BrowserHost.Instance.Evacuate(this);
    }

    /// <summary>The window's content.</summary>
    public Control? Body
    {
        get => _body.Content as Control;
        set => _body.Content = value;
    }

    private static Button ChromeButton(Geometry icon, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = new PathIcon { Data = icon, Width = 10, Height = 10 }, Width = 26, Height = 22, Padding = new Thickness(0), Classes = { "chrome" },
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Focusable = false,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ApplyChrome()
    {
        var theme = Ui.Theme;
        _custom = Themes.CustomChrome(App.Services.Config.Settings.Theme, theme);
        var decorations = _custom ? WindowDecorations.None : WindowDecorations.Full;
        if (WindowDecorations != decorations) WindowDecorations = decorations;
        FontFamily = ThemeManager.UiFont;
        _titleText.Text = Title;
        _titleBar.IsVisible = _custom;
        _min.IsVisible = _max.IsVisible = CanResize;
        _maxIcon.Data = WindowState == WindowState.Maximized ? Icons.WindowRestore : Icons.WindowMaximize;
        var framed = _custom && WindowState == WindowState.Normal;
        _frame.BorderThickness = new Thickness(framed ? (theme.Bevel ? 4 : 1) : 0);
        _frame.BorderBrush = theme.Bevel ? Ui.Tk("panel") : Ui.Tk("lineStrong");
        _frame.BoxShadow = framed && theme.Bevel ? BoxShadows.Parse("inset 1 1 0 0 #ffffff, inset -1 -1 0 0 #404040") : default;
        _frame.Padding = new Thickness(framed && theme.Bevel ? 0 : 0);
        if (!_custom) _frame.Cursor = Cursor.Default;
    }

    private WindowEdge? EdgeAt(Point p)
    {
        if (!CanResize || WindowState != WindowState.Normal) return null;
        var (w, h) = (Bounds.Width, Bounds.Height);
        bool left = p.X < Grip, right = p.X > w - Grip, top = p.Y < Grip, bottom = p.Y > h - Grip;
        // Corners reach a little further along each edge so they are easy to catch.
        const double corner = 16;
        if ((top && p.X < corner) || (left && p.Y < corner)) return WindowEdge.NorthWest;
        if ((top && p.X > w - corner) || (right && p.Y < corner)) return WindowEdge.NorthEast;
        if ((bottom && p.X < corner) || (left && p.Y > h - corner)) return WindowEdge.SouthWest;
        if ((bottom && p.X > w - corner) || (right && p.Y > h - corner)) return WindowEdge.SouthEast;
        return left ? WindowEdge.West : right ? WindowEdge.East : top ? WindowEdge.North : bottom ? WindowEdge.South : null;
    }

    private static StandardCursorType CursorFor(WindowEdge edge) => edge switch
    {
        WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
        WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
        WindowEdge.NorthWest => StandardCursorType.TopLeftCorner, WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
        WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner, _ => StandardCursorType.BottomRightCorner,
    };
}

/// <summary>The application icon, loaded once.</summary>
public static class AppIcon
{
    private static readonly Uri Source = new("avares://avamux/Assets/avamux.png");
    public static readonly Avalonia.Media.Imaging.Bitmap? Bitmap = Load();
    public static WindowIcon? Window => Bitmap is null ? null : new WindowIcon(Bitmap);

    private static Avalonia.Media.Imaging.Bitmap? Load()
    {
        try { using var s = AssetLoader.Open(Source); return new Avalonia.Media.Imaging.Bitmap(s); }
        catch { return null; }
    }
}
