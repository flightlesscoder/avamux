using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Avamux.Core.Text;

namespace Avamux.Views;

/// <summary>
/// Colours markdown in the wiki editor with the same tokenizer and theme colours as code blocks elsewhere in
/// the app. The whole text is tokenized once per change (fenced blocks need state across lines) and each line
/// is then coloured from that result.
/// </summary>
public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private List<List<Avamux.Core.Text.Span>>? _lines;

    /// <summary>Theme colours of heading levels 1 to 6, picked to stay apart from each other in every theme.</summary>
    private static readonly string[] HeadingTokens = ["accent", "cyan", "purple", "orange", "warn", "ok"];

    private static int HeadingLevel(string text)
    {
        var t = text.TrimStart();
        var n = 0;
        while (n < t.Length && t[n] == '#') n++;
        return Math.Clamp(n, 1, 6);
    }

    /// <summary>Forget the tokens; the next paint tokenizes again.</summary>
    public void Invalidate() => _lines = null;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;
        _lines ??= Syntax.Highlight(CurrentContext.Document.Text, "markdown");
        var index = line.LineNumber - 1;
        if (index >= _lines.Count) return;
        var at = line.Offset;
        foreach (var span in _lines[index])
        {
            var length = span.Text.Length;
            if (span.Kind != Tok.Text && at + length <= line.EndOffset)
            {
                var kind = span.Kind;
                var brush = Ui.Tk(kind == Tok.Heading ? HeadingTokens[HeadingLevel(span.Text) - 1] : Syntax.ThemeToken(kind));
                ChangeLinePart(at, at + length, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    if (kind == Tok.Heading) element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.Bold));
                    else if (kind == Tok.Comment) element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyle.Italic, FontWeight.Normal));
                });
            }
            at += length;
        }
    }
}
