using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Avamux.Core.Text;

namespace Avamux.Views;

/// <summary>
/// Colours code in a text editor with the app's own tokenizer and theme colours, the way diffs and files are
/// coloured elsewhere. The text is tokenized once per change and each line is coloured from that result.
/// Lines that are git's conflict markers stand out in the theme's error colour.
/// </summary>
public sealed class SyntaxColorizer : DocumentColorizingTransformer
{
    private List<List<Avamux.Core.Text.Span>>? _lines;

    public string? Language { get; set; }

    /// <summary>Forget the tokens; the next paint tokenizes again.</summary>
    public void Invalidate() => _lines = null;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;
        var text = CurrentContext.Document.GetText(line);
        if (text.StartsWith("<<<<<<< ", StringComparison.Ordinal) || text.StartsWith(">>>>>>> ", StringComparison.Ordinal) || text == "=======")
        {
            var marker = Ui.Tk("errorHi");
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(marker);
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.Bold));
            });
            return;
        }
        _lines ??= Syntax.Highlight(CurrentContext.Document.Text.Replace("\r\n", "\n"), Language);
        var index = line.LineNumber - 1;
        if (index >= _lines.Count) return;
        var at = line.Offset;
        foreach (var span in _lines[index])
        {
            var length = span.Text.Length;
            if (span.Kind != Tok.Text && at + length <= line.EndOffset)
            {
                var kind = span.Kind;
                var brush = Ui.Tk(Syntax.ThemeToken(kind));
                ChangeLinePart(at, at + length, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    if (kind == Tok.Comment) element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyle.Italic, FontWeight.Normal));
                });
            }
            at += length;
        }
    }
}
