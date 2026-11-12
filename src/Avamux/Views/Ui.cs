using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avamux.Core.Theming;

namespace Avamux.Views;

/// <summary>Small helpers for building views in code.</summary>
public static class Ui
{
    // One brush per theme token, shared by everything that uses the token. Switching theme changes the colour
    // of these brushes in place, so controls that were built long ago repaint without being rebuilt.
    private static readonly Dictionary<string, SolidColorBrush> Tokens = new();
    private static readonly Dictionary<string, string> LegacyHex = BuildLegacy();

    public static ThemeDef Theme { get; private set; } = Themes.Simple;
    /// <summary>Raised on the UI thread after the theme's colours have been applied.</summary>
    public static event Action? ThemeChanged;

    private static Dictionary<string, string> BuildLegacy()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (token, hex) in Themes.Simple.Colors) map.TryAdd(hex, token);
        return map;
    }

    /// <summary>The brush for a theme token such as "accent", "dim" or "panel".</summary>
    public static IBrush Tk(string token)
    {
        if (Tokens.TryGetValue(token, out var brush)) return brush;
        return Tokens[token] = new SolidColorBrush(Avalonia.Media.Color.Parse(Theme[token]));
    }

    public static Color Color(string token) => Avalonia.Media.Color.Parse(Theme[token]);

    /// <summary>
    /// A colour written as it appears in the Simple theme. If it is one of that theme's colours the matching
    /// token's brush is returned, so it follows the active theme; any other colour is used as is.
    /// </summary>
    public static IBrush Brush(string hex) => LegacyHex.TryGetValue(hex, out var token) ? Tk(token) : Raw(hex);

    /// <summary>A fixed colour that no theme changes (a colour the user picked, say). Falls back to the accent.</summary>
    public static IBrush Raw(string? hex) => Avalonia.Media.Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Tk("accent");

    public static void ApplyTheme(ThemeDef theme)
    {
        Theme = theme;
        foreach (var (token, brush) in Tokens) brush.Color = Avalonia.Media.Color.Parse(theme[token]);
        ThemeChanged?.Invoke();
    }

    public static IBrush Dim => Tk("dim");
    public static IBrush Panel => Tk("panel");
    public static IBrush Line => Tk("line");
    /// <summary>Text drawn on top of an accent- or status-coloured chip.</summary>
    public static IBrush OnAccent => Tk("onAccent");
    public static IBrush Strong => Tk("textStrong");

    /// <summary>Corner radius that respects square-cornered themes.</summary>
    public static CornerRadius R(double radius) => Theme.Radius <= 0 ? default : new CornerRadius(radius);
    public static CornerRadius R(double tl, double tr, double br, double bl) => Theme.Radius <= 0 ? default : new CornerRadius(tl, tr, br, bl);

    /// <summary>The first installed monospace family from a preference list (a comma list does not fall back by itself).</summary>
    public static readonly FontFamily Mono = PickMono();

    private static FontFamily PickMono()
    {
        string[] wanted = ["JetBrainsMono Nerd Font Mono", "JetBrainsMono Nerd Font", "JetBrains Mono", "Cascadia Mono", "SF Mono", "Menlo", "DejaVu Sans Mono", "Noto Sans Mono", "Liberation Mono", "Consolas", "Courier New"];
        try
        {
            var installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted.FirstOrDefault(installed.Contains) is { } name) return new FontFamily(name);
            if (installed.FirstOrDefault(n => n.Contains("Mono", StringComparison.OrdinalIgnoreCase)) is { } any) return new FontFamily(any);
        }
        catch { /* fall through */ }
        return new FontFamily("monospace");
    }

    public static IBrush StatusBrush(string? status) => Tk(status switch
    {
        "ok" => "ok", "info" => "accent", "warn" => "warn", "error" => "error", _ => "faint",
    });

    public static Button IconButton(Geometry icon, string tip, Action onClick, string? text = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new PathIcon { Data = icon } } };
        if (text is not null) content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Classes = { "flat" }, VerticalAlignment = VerticalAlignment.Center };
        if (text is not null) b.Padding = new Thickness(10, 6);
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBlock Text(string text, string? cls = null, double? size = null) =>
        Apply(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, t => { if (cls is not null) t.Classes.Add(cls); if (size is { } s) t.FontSize = s; });

    public static T Apply<T>(T control, Action<T> configure) { configure(control); return control; }

    public static T At<T>(this T control, int row = 0, int col = 0, int rowSpan = 1, int colSpan = 1) where T : Control
    {
        Grid.SetRow(control, row); Grid.SetColumn(control, col); Grid.SetRowSpan(control, rowSpan); Grid.SetColumnSpan(control, colSpan);
        return control;
    }

    public static T Dock<T>(this T control, Dock dock) where T : Control { DockPanel.SetDock(control, dock); return control; }

    /// <summary>Runs on the UI thread, coalescing bursts of calls into one.</summary>
    public static Action Debounced(Action action, int ms = 60)
    {
        var pending = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref pending, 1) == 1) return;
            Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(ms);
                Interlocked.Exchange(ref pending, 0);
                try { action(); } catch (Exception ex) { Avamux.Core.Logging.Log.Error("ui", "Refresh failed", ex); }
            });
        };
    }

    /// <summary>Header row used at the top of most pages.</summary>
    public static Control Header(string title, string subtitle, params Control[] actions)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var a in actions) right.Children.Add(a);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 14, 16, 10),
            Children =
            {
                new StackPanel { Children = { Text(title, "h1"), Apply(Text(subtitle, "dim"), t => t.FontSize = 12) } },
                right.At(0, 1),
            },
        };
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 380 };
        string? result = null;
        var dlg = new AppWindow
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Tk("panel"),
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { result = box.Text ?? ""; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Body = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children = { new TextBlock { Text = label }, box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } },
        };
        dlg.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dlg.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string action = "Delete")
    {
        var result = false;
        var dlg = new AppWindow
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Tk("panel"),
        };
        var ok = new Button { Content = action, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Body = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14, MaxWidth = 460,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } },
        };
        await dlg.ShowDialog(owner);
        return result;
    }

    public static Window Owner(Control c) => (Window)TopLevel.GetTopLevel(c)!;

    public static MainWindow? Main => (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    /// <summary>Shows a toast in the main window, whichever window asked.</summary>
    public static void Toast(Control? from, string title, string message = "") => Main?.Toast(title, message);

    /// <summary>Runs a UI action, turning any failure into a toast instead of a crash report.</summary>
    public static void Try(Control? from, Action action, string what = "That did not work")
    {
        try { action(); }
        catch (Exception ex) { Toast(from, what, ex.GetBaseException().Message); }
    }

    public static async Task TryAsync(Control? from, Func<Task> action, string what = "That did not work")
    {
        try { await action(); }
        catch (Exception ex) { Toast(from, what, ex.GetBaseException().Message); }
    }

    public static Border Card(Control child, Thickness? margin = null) =>
        new() { Classes = { "card" }, Child = child, Margin = margin ?? default };

    public static Border Dot(string? status, double size = 10) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = StatusBrush(status), VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button TextButton(string text, Action onClick, string? tip = null)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 6) };
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Text that looks and behaves like a hyperlink.</summary>
    public static Button Link(string text, Action onClick, string? tip = null)
    {
        var b = new Button
        {
            Classes = { "link" }, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextDecorations = TextDecorations.Underline },
        };
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A labelled form row: label (and optional help) above the editor.</summary>
    public static StackPanel Field(string label, Control editor, string? help = null)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
        p.Children.Add(editor);
        if (!string.IsNullOrEmpty(help)) p.Children.Add(new TextBlock { Text = help, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        return p;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>
    /// A modal form. <paramref name="onSave"/> applies the form; throw (or return a message) to keep the dialog open
    /// and show the problem. Returns true when saved.
    /// </summary>
    public static async Task<bool> FormAsync(Window owner, string title, Control body, Func<Task<string?>> onSave, string saveText = "Save", double width = 520)
    {
        var saved = false;
        var error = new TextBlock { Foreground = Tk("error"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var dlg = new AppWindow
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, MaxHeight = 780, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Tk("panel"),
        };
        // The default button: Enter anywhere in the form saves (multi-line boxes keep Enter for themselves).
        var ok = new Button { Content = saveText, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += async (_, _) =>
        {
            string? problem;
            try { problem = await onSave(); }
            catch (Exception ex) { problem = ex.GetBaseException().Message; }
            if (problem is null) { saved = true; dlg.Close(); }
            else { error.Text = problem; error.IsVisible = true; }
        };
        cancel.Click += (_, _) => dlg.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };
        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(18, 10, 18, 16), Children = { error, buttons } };
        dlg.Body = new DockPanel
        {
            Children = { footer.Dock(Avalonia.Controls.Dock.Bottom), new ScrollViewer { Content = new Border { Padding = new Thickness(18, 16, 18, 4), Child = body } } },
        };
        dlg.Opened += (_, _) => (body.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEnabled) as InputElement)?.Focus();
        await dlg.ShowDialog(owner);
        return saved;
    }

    public static string Ago(DateTime? when)
    {
        if (when is not { } t) return "never";
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return t.ToString("yyyy-MM-dd HH:mm");
    }

    public static string Until(DateTime when)
    {
        var d = when - DateTime.Now;
        if (d < TimeSpan.Zero) return "now";
        if (d.TotalSeconds < 60) return $"in {(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"in {(int)d.TotalMinutes}m {d.Seconds:00}s";
        if (d.TotalHours < 24) return $"in {(int)d.TotalHours}h {d.Minutes:00}m";
        if (d.TotalDays < 7) return $"in {(int)d.TotalDays}d {d.Hours}h";
        return when.ToString("ddd yyyy-MM-dd HH:mm");
    }

    /// <summary>A menu item that runs an action, with failures shown as a toast.</summary>
    public static MenuItem Item(string header, Action onClick, bool enabled = true, Geometry? icon = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (icon is not null) item.Icon = new PathIcon { Data = icon, Width = 14, Height = 14 };
        item.Click += (_, _) => Try(null, onClick);
        return item;
    }

    public static MenuItem Item(string header, Func<Task> onClick, bool enabled = true, Geometry? icon = null) =>
        Item(header, () => { _ = TryAsync(null, onClick); }, enabled, icon);

    /// <summary>A small coloured label: "Task", "css", a status.</summary>
    public static Border Chip(string text, IBrush background, double size = 10.5) => new()
    {
        Background = background, CornerRadius = R(3), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = size, Foreground = OnAccent },
    };

    /// <summary>Opens a file or folder in the configured editor (repositories.editor) in a new terminal tab.</summary>
    public static void OpenInEditor(string path, string? cwd = null)
    {
        var editor = App.Services.Config.Settings.Repositories.Editor;
        var folder = cwd ?? (Directory.Exists(path) ? path : Path.GetDirectoryName(path)) ?? App.Services.Paths.HomeDir;
        var target = cwd is null && Directory.Exists(path) ? "." : path;
        var label = Directory.Exists(path) ? Path.GetFileName(path.TrimEnd('/')) + "/" : Path.GetFileName(path);
        var session = App.Services.Terminals.Create(new Core.Terminal.TerminalStartOptions(editor, [target], folder, $"{editor} {label}"));
        App.Services.Host.Navigate("terminals");
        App.Services.Host.SelectTerminal(session.Id);
    }

    public static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1048576 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1048576.0:0.##} MB";
}
