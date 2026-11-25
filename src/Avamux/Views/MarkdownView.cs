using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avamux.Core;
using Avamux.Core.Text;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Avamux.Views;

/// <summary>
/// Renders markdown with native controls, for places where a web view would be too heavy (dialogs, side panels).
/// Covers headings, paragraphs, emphasis, code, lists, task lists, quotes, rules and links.
/// </summary>
/// <summary>What a rendering may do beyond the plain view (the AI Chat uses all of it).</summary>
public sealed class MarkdownOptions
{
    /// <summary>Text can be selected and copied.</summary>
    public bool Selectable { get; init; }
    /// <summary>Code blocks get a copy button.</summary>
    public bool CopyCode { get; init; }
    /// <summary>Makes the control for a picture (<c>![alt](src)</c>); null shows the alt text.</summary>
    public Func<string, string, Control?>? Picture { get; init; }
}

public static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseEmphasisExtras().UseTaskLists().UseAutoLinks().UsePipeTables().Build();

    [ThreadStatic] private static MarkdownOptions? _options;

    public static Control Build(string? markdown, double fontSize = 13, MarkdownOptions? options = null)
    {
        var panel = new StackPanel { Spacing = 7 };
        if (string.IsNullOrWhiteSpace(markdown)) return panel;
        var outer = _options;
        _options = options;
        try
        {
            foreach (var block in Markdown.Parse(markdown, Pipeline)) Add(panel, block, fontSize);
        }
        catch (Exception ex)
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = markdown, TextWrapping = TextWrapping.Wrap, FontSize = fontSize });
            Core.Logging.Log.Debug("markdown", "Falling back to plain text: " + ex.Message);
        }
        finally { _options = outer; }
        return panel;
    }

    private static void Add(Panel panel, Block block, double size)
    {
        switch (block)
        {
            case HeadingBlock h:
                panel.Children.Add(Text(h.Inline, size + Math.Max(0, 7 - h.Level * 2), FontWeight.SemiBold, Ui.Strong));
                break;
            case ParagraphBlock p when _options?.Picture is { } makePicture && p.Inline?.Descendants<LinkInline>().Any(l => l.IsImage) == true:
                // Pictures stand on their own under the paragraph's text: inside a line they would overlap the next one.
                var pictures = p.Inline.Descendants<LinkInline>().Where(l => l.IsImage).ToList();
                var words = Text(p.Inline, size);
                if (words.Inlines?.OfType<Run>().Any(r => !string.IsNullOrWhiteSpace(r.Text)) == true) panel.Children.Add(words);
                foreach (var picture in pictures)
                {
                    var alt = string.Concat(picture.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                    panel.Children.Add(makePicture(picture.Url ?? "", alt) ?? new TextBlock { Text = alt.Length > 0 ? $"[{alt}]" : "[picture]", FontStyle = FontStyle.Italic, Foreground = Ui.Dim });
                }
                break;
            case ParagraphBlock p:
                panel.Children.Add(Text(p.Inline, size));
                break;
            case ThematicBreakBlock:
                panel.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 3) });
                break;
            case CodeBlock code:
                var source = code.Lines.ToString();
                var codeBox = new Border
                {
                    Background = Ui.Tk("bgEditor"), BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(1), CornerRadius = Ui.R(5), Padding = new Thickness(9, 6),
                    Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = Code(source, (code as FencedCodeBlock)?.Info, size - 1, _options?.Selectable == true) },
                };
                if (_options?.CopyCode == true)
                {
                    var copy = Ui.IconButton(Icons.ContentCopy, "Copy the code", () => _ = App.Services.Host.SetClipboardAsync(source.TrimEnd('\n')));
                    copy.HorizontalAlignment = HorizontalAlignment.Right;
                    copy.VerticalAlignment = VerticalAlignment.Top;
                    copy.Margin = new Thickness(0, 3, 4, 0);
                    copy.Opacity = 0.75;
                    var lang = (code as FencedCodeBlock)?.Info;
                    var holder = new Panel { Children = { codeBox, copy } };
                    if (!string.IsNullOrWhiteSpace(lang))
                        holder.Children.Add(new TextBlock { Text = lang, FontSize = 10.5, Foreground = Ui.Tk("faint"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 40, 0) });
                    panel.Children.Add(holder);
                }
                else panel.Children.Add(codeBox);
                break;
            case Markdig.Extensions.Tables.Table table:
                panel.Children.Add(Table(table, size));
                break;
            case QuoteBlock quote:
                var inner = new StackPanel { Spacing = 5, Opacity = 0.85 };
                foreach (var b in quote) Add(inner, b, size);
                panel.Children.Add(new Border { BorderBrush = Ui.Tk("lineStrong"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 1, 0, 1), Child = inner });
                break;
            case ListBlock list:
                var items = new StackPanel { Spacing = 3 };
                var n = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var body = new StackPanel { Spacing = 4 };
                    string marker = list.IsOrdered ? $"{n++}." : "•";
                    foreach (var b in item)
                    {
                        // Task list items carry their checkbox as the paragraph's first inline.
                        if (b is ParagraphBlock { Inline.FirstChild: TaskList task }) marker = task.Checked ? "☑" : "☐";
                        Add(body, b, size);
                    }
                    items.Children.Add(new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("22,*"),
                        Children = { new TextBlock { Text = marker, FontSize = size, Foreground = Ui.Dim, HorizontalAlignment = HorizontalAlignment.Left }, body.At(0, 1) },
                    });
                }
                panel.Children.Add(items);
                break;
            case ContainerBlock container:
                foreach (var b in container) Add(panel, b, size);
                break;
            case LeafBlock leaf:
                panel.Children.Add(leaf.Inline is not null ? Text(leaf.Inline, size) : new TextBlock { Text = leaf.Lines.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = size });
                break;
        }
    }

    private static Control Table(Markdig.Extensions.Tables.Table table, double size)
    {
        var grid = new Grid();
        var rows = table.OfType<Markdig.Extensions.Tables.TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cells = rows[r].OfType<Markdig.Extensions.Tables.TableCell>().ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                var content = new StackPanel { Spacing = 3 };
                foreach (var b in cells[c]) Add(content, b, size - 0.5);
                if (rows[r].IsHeader) foreach (var t in content.Children.OfType<TextBlock>()) t.FontWeight = FontWeight.SemiBold;
                grid.Children.Add(new Border
                {
                    BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8, 4),
                    Background = rows[r].IsHeader ? Ui.Tk("bgDeep") : null, Child = content,
                }.At(r, c));
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { BorderBrush = Ui.Tk("lineSoft"), BorderThickness = new Thickness(1, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = grid },
        };
    }

    private static TextBlock Text(ContainerInline? inline, double size, FontWeight weight = FontWeight.Normal, IBrush? foreground = null)
    {
        TextBlock block = _options?.Selectable == true ? new SelectableTextBlock() : new TextBlock();
        block.TextWrapping = TextWrapping.Wrap; block.FontSize = size; block.FontWeight = weight; block.LineHeight = size * 1.45;
        if (foreground is not null) block.Foreground = foreground;
        if (inline is not null) Inlines(block.Inlines!, inline, size, weight, FontStyle.Normal, null);
        return block;
    }

    private static void Inlines(InlineCollection target, ContainerInline container, double size, FontWeight weight, FontStyle style, TextDecorationCollection? decorations)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    target.Add(new Run(lit.Content.ToString()) { FontWeight = weight, FontStyle = style, TextDecorations = decorations });
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = Ui.Mono, FontSize = size - 0.5, Foreground = Ui.Tk("ok"), Background = Ui.Tk("lineSoft") });
                    break;
                case LineBreakInline br:
                    if (br.IsHard) target.Add(new LineBreak()); else target.Add(new Run(" "));
                    break;
                case TaskList:
                    break;
                case EmphasisInline em:
                    var strike = em.DelimiterChar == '~' && em.DelimiterCount == 2;
                    Inlines(target, em, size,
                        em.DelimiterChar is '*' or '_' && em.DelimiterCount >= 2 ? FontWeight.Bold : weight,
                        em.DelimiterChar is '*' or '_' && em.DelimiterCount % 2 == 1 ? FontStyle.Italic : style,
                        strike ? TextDecorations.Strikethrough : decorations);
                    break;
                case LinkInline { IsImage: true } when _options?.Picture is not null:
                    break; // shown under the paragraph (see Add)
                case LinkInline link:
                    var label = new TextBlock { FontSize = size, FontWeight = weight };
                    Inlines(label.Inlines!, link, size, weight, style, TextDecorations.Underline);
                    if (label.Inlines!.Count == 0) label.Text = link.Url ?? "";
                    var url = link.Url ?? "";
                    var button = new Button { Classes = { "link" }, Content = label, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                    ToolTip.SetTip(button, url);
                    button.Click += (_, _) => { if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("mailto:")) Shell.Open(url); };
                    target.Add(new InlineUIContainer { Child = button, BaselineAlignment = BaselineAlignment.TextBottom });
                    break;
                case AutolinkInline auto:
                    var address = auto.Url;
                    var autoButton = Ui.Link(address, () => Shell.Open(auto.IsEmail ? "mailto:" + address : address), address);
                    ((TextBlock)autoButton.Content!).FontSize = size;
                    target.Add(new InlineUIContainer { Child = autoButton, BaselineAlignment = BaselineAlignment.TextBottom });
                    break;
                case ContainerInline nested:
                    Inlines(target, nested, size, weight, style, decorations);
                    break;
                default:
                    target.Add(new Run(inline.ToString() ?? "") { FontWeight = weight, FontStyle = style });
                    break;
            }
        }
    }

    /// <summary>Syntax-highlighted code as one text block.</summary>
    public static TextBlock Code(string code, string? language, double size = 12.5, bool selectable = false)
    {
        TextBlock block = selectable ? new SelectableTextBlock() : new TextBlock();
        block.FontFamily = Ui.Mono; block.FontSize = size; block.Foreground = Ui.Tk("textCode");
        var lines = Syntax.Highlight(code.TrimEnd('\n'), language);
        for (var i = 0; i < lines.Count; i++)
        {
            Fill(block.Inlines!, lines[i]);
            if (i < lines.Count - 1) block.Inlines!.Add(new LineBreak());
        }
        return block;
    }

    /// <summary>Adds one highlighted line to a text block's inlines.</summary>
    public static void Fill(InlineCollection target, IEnumerable<Avamux.Core.Text.Span> spans)
    {
        foreach (var s in spans)
        {
            var run = new Run(s.Text);
            if (s.Kind != Tok.Text) run.Foreground = Ui.Tk(Syntax.ThemeToken(s.Kind));
            if (s.Kind == Tok.Comment) run.FontStyle = FontStyle.Italic;
            target.Add(run);
        }
    }
}
