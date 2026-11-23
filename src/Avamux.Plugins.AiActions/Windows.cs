using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Avamux.Plugins.AiActions;

/// <summary>Plain Avalonia pieces in avamux's theme colours (the plugin has only the contract, not avamux's own helpers).</summary>
internal sealed class Look(IPluginContext context)
{
    public IBrush Brush(string token, double opacity = 1) =>
        Color.TryParse(context.ThemeColor(token), out var c) ? new SolidColorBrush(c, opacity) : Brushes.Gray;

    public TextBlock Text(string text, double size = 13, string? token = null, bool bold = false, bool wrap = true) => new()
    {
        Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Foreground = token is null ? null : Brush(token), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
    };

    public Button Button(string text, Action click, bool accent = false, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
        if (accent) b.Classes.Add("accent");
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public StackPanel Row(params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public Border Box(Control child, string token = "bgDeep") => new()
    {
        Background = Brush(token), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = child,
    };

    public static FontFamily Mono => new("JetBrainsMono Nerd Font Mono, JetBrains Mono, DejaVu Sans Mono, monospace");
}

/// <summary>A prompt's reply streaming in under what it was about: Summarize, Explain and custom actions shown in a window.</summary>
internal sealed class ResultView : UserControl
{
    private readonly SelectableTextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 };
    private readonly TextBlock _status;
    private readonly StackPanel _buttons;
    private readonly System.Text.StringBuilder _text = new();
    private bool _pending;

    public ResultView(Look look, string sourceTitle, string source, string resultTitle)
    {
        _status = look.Text("Asking…", 12, "dim");
        _buttons = look.Row();
        _buttons.IsEnabled = false;
        var sourceBox = new TextBox
        {
            Text = source, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, FontSize = 12.5, AcceptsReturn = true,
        };
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new StackPanel { Spacing = 6, Children = { look.Text(sourceTitle, 12.5, "dim", bold: true), sourceBox } }.With(d => DockPanel.SetDock(d, Dock.Top)),
                new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { _status, _buttons } }.With(d => DockPanel.SetDock(d, Dock.Bottom)),
                new StackPanel
                {
                    Spacing = 6, Margin = new Thickness(0, 14, 0, 0),
                    Children = { look.Text(resultTitle, 12.5, "dim", bold: true), new ScrollViewer { Content = look.Box(_result, "bgEditor"), MaxHeight = 2000 } },
                },
            },
        };
    }

    public string Text => _text.ToString();

    /// <summary>Adds streamed text (any thread).</summary>
    public void Append(string piece)
    {
        lock (_text) _text.Append(piece);
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() => { _pending = false; lock (_text) _result.Text = _text.ToString(); _status.Text = "Writing…"; }, DispatcherPriority.Background);
    }

    public void Done(string text, string? error, IEnumerable<Control> buttons)
    {
        lock (_text) { _text.Clear(); _text.Append(text); }
        _result.Text = text;
        _status.Text = error is null ? "" : "Problem: " + error;
        if (error is not null) _status.Foreground = Brushes.IndianRed;
        _buttons.Children.Clear();
        foreach (var b in buttons) _buttons.Children.Add(b);
        _buttons.IsEnabled = true;
    }
}

/// <summary>Suggested tags as checkboxes (all ticked), a box for more, and Apply.</summary>
internal sealed class TagView : UserControl
{
    private readonly WrapPanel _chips = new();
    private readonly TextBox _more = new() { PlaceholderText = "More tags, separated by spaces" };
    private readonly TextBlock _status;
    private readonly Button _apply;
    private readonly HashSet<string> _existing;

    public TagView(Look look, string page, IEnumerable<string> existing, Action<List<string>> apply, Action cancel)
    {
        _existing = [.. existing];
        _status = look.Text("Asking for tags…", 12.5, "dim");
        _apply = look.Button("Add Tags", () => apply(Chosen()), accent: true);
        _apply.IsEnabled = false;
        Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 12,
            Children =
            {
                look.Text($"Tags for {page}", 14, bold: true),
                _status, _chips, _more,
                look.Text("Ticked tags are added to the page: into its front matter's tags: when it has one, else onto a Tags: line at the end. Undo (Ctrl+Z) takes them out again.", 11.5, "dim"),
                look.Row(_apply, look.Button("Cancel", cancel)),
            },
        };
    }

    public void Show(IReadOnlyList<string> tags, string? error)
    {
        _chips.Children.Clear();
        foreach (var t in tags)
        {
            var known = _existing.Contains(t);
            var box = new CheckBox { Content = "#" + t + (known ? "" : "  (new)"), IsChecked = true, Tag = t, Margin = new Thickness(0, 0, 14, 4) };
            _chips.Children.Add(box);
        }
        _status.Text = error is not null ? "Problem: " + error : tags.Count == 0 ? "No tags were suggested; type some below." : "Untick any you do not want.";
        _apply.IsEnabled = true;
    }

    private List<string> Chosen() =>
        _chips.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!)
            .Concat((_more.Text ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('#')))
            .Distinct().ToList();
}

/// <summary>What a change does, line by line, with Accept and Reject.</summary>
internal sealed class DiffView : UserControl
{
    private readonly StackPanel _lines = new();
    private readonly TextBlock _status;
    private readonly Button _accept;
    private readonly Look _look;

    public DiffView(Look look, string title, Action accept, Action reject)
    {
        _look = look;
        _status = look.Text("Asking…", 12.5, "dim");
        _accept = look.Button("Accept", accept, accent: true, tip: "Put the new text in the page (one undo step)");
        _accept.IsEnabled = false;
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new StackPanel { Spacing = 6, Children = { look.Text(title, 14, bold: true), _status } }.With(d => DockPanel.SetDock(d, Dock.Top)),
                look.Row(_accept, look.Button("Reject", reject)).With(d => { DockPanel.SetDock(d, Dock.Bottom); d.Margin = new Thickness(0, 10, 0, 0); }),
                new ScrollViewer { Margin = new Thickness(0, 10, 0, 0), Content = look.Box(_lines, "bgEditor") },
            },
        };
    }

    public void Show(string before, string after, string? error)
    {
        _lines.Children.Clear();
        if (error is not null) { _status.Text = "Problem: " + error; return; }
        var diff = LineDiff.Compute(before, after);
        var changed = diff.Count(d => d.Kind != LineDiff.Kind.Same);
        _status.Text = changed == 0 ? "Nothing would change." : $"{diff.Count(d => d.Kind == LineDiff.Kind.Removed)} lines out, {diff.Count(d => d.Kind == LineDiff.Kind.Added)} lines in.";
        // Unchanged stretches are folded to their first and last lines.
        var i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind == LineDiff.Kind.Same)
            {
                var j = i;
                while (j < diff.Count && diff[j].Kind == LineDiff.Kind.Same) j++;
                if (j - i > 6)
                {
                    for (var k = i; k < i + 2; k++) Line(diff[k]);
                    _lines.Children.Add(_look.Text($"   … {j - i - 4} lines the same …", 11.5, "faint"));
                    for (var k = j - 2; k < j; k++) Line(diff[k]);
                }
                else for (var k = i; k < j; k++) Line(diff[k]);
                i = j;
            }
            else Line(diff[i++]);
        }
        _accept.IsEnabled = changed > 0;
    }

    private void Line((LineDiff.Kind Kind, string Line) d)
    {
        var mark = d.Kind switch { LineDiff.Kind.Added => "+ ", LineDiff.Kind.Removed => "- ", _ => "  " };
        _lines.Children.Add(new Border
        {
            Background = d.Kind switch { LineDiff.Kind.Added => _look.Brush("ok", 0.16), LineDiff.Kind.Removed => _look.Brush("error", 0.16), _ => Brushes.Transparent },
            Child = new SelectableTextBlock { Text = mark + d.Line, FontFamily = Look.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap },
        });
    }
}

internal static class ControlExtensions
{
    public static T With<T>(this T control, Action<T> configure) where T : Control { configure(control); return control; }
}
