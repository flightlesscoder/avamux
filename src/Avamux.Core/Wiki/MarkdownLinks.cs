using System.Text.RegularExpressions;

namespace Avamux.Core.Wiki;

/// <summary>What a link in a page's markdown points at.</summary>
public enum LinkKind
{
    /// <summary>A web address (http, https, ftp) or mail (mailto:).</summary>
    Web,
    /// <summary>A file on this computer (file:///…).</summary>
    File,
    /// <summary>A page or file of the wiki: a relative markdown link or a [[wiki link]].</summary>
    Wiki,
    /// <summary>A heading on the same page (#heading).</summary>
    Anchor,
}

/// <param name="Start">Where the whole link starts in the line.</param>
/// <param name="Length">The whole link: <c>[label](target)</c>, <c>[[target|label]]</c>, <c>&lt;url&gt;</c> or a bare address.</param>
/// <param name="Target">Where it goes, as written (a wiki link's page name without the brackets).</param>
/// <param name="Label">The words shown for it, or the address itself for a bare one.</param>
/// <param name="LabelStart">Where <see cref="Label"/> is in the line.</param>
public sealed record MarkdownLink(int Start, int Length, string Target, string Label, int LabelStart, LinkKind Kind, bool IsWikiLink)
{
    public int End => Start + Length;
}

/// <summary>
/// Finds the links on one line of markdown, as the preview shows them: markdown links, [[wiki links]], &lt;autolinks&gt;
/// and bare web or file addresses. Pictures (![…](…)) and anything inside `code` are not links.
/// </summary>
public static partial class MarkdownLinks
{
    [GeneratedRegex(@"`+[^`]*`+")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\[\[([^\[\]|]+)(?:\|([^\[\]]+))?\]\]")]
    private static partial Regex WikiLink();

    // [label](target "title"): the label may hold one level of brackets; the target may be <wrapped>.
    [GeneratedRegex(@"(!?)\[((?:[^\[\]]|\[[^\[\]]*\])*)\]\(\s*(<[^>]*>|[^\s()]*(?:\([^\s()]*\)[^\s()]*)*)(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"<((?:https?|ftp|file|mailto):[^\s<>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"(?<![\w/:.@-])(?:(?:https?|ftp)://|file:///?|mailto:)[^\s<>\[\]""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();

    public static LinkKind KindOf(string target)
    {
        var t = target.Trim();
        if (t.StartsWith('#')) return LinkKind.Anchor;
        if (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return LinkKind.File;
        if (Regex.IsMatch(t, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")) return LinkKind.Web;
        return LinkKind.Wiki;
    }

    public static List<MarkdownLink> Find(string line)
    {
        var found = new List<MarkdownLink>();
        if (line.IndexOfAny(['[', ':', '<']) < 0) return found;
        var taken = new List<(int Start, int End)>();
        foreach (Match m in CodeSpan().Matches(line)) taken.Add((m.Index, m.Index + m.Length));
        bool Free(int start, int end) => !taken.Any(t => start < t.End && end > t.Start);
        void Add(MarkdownLink link)
        {
            if (!Free(link.Start, link.End)) return;
            taken.Add((link.Start, link.End));
            found.Add(link);
        }

        foreach (Match m in WikiLink().Matches(line))
        {
            var target = m.Groups[1].Value.Trim();
            var label = m.Groups[2].Success ? m.Groups[2] : m.Groups[1];
            Add(new MarkdownLink(m.Index, m.Length, target, label.Value.Trim(), label.Index + (label.Value.Length - label.Value.TrimStart().Length), LinkKind.Wiki, true));
        }
        foreach (Match m in InlineLink().Matches(line))
        {
            if (m.Groups[1].Value == "!") { if (Free(m.Index, m.Index + m.Length)) taken.Add((m.Index, m.Index + m.Length)); continue; } // a picture
            var target = m.Groups[3].Value.Trim('<', '>');
            if (target.Length == 0) continue;
            Add(new MarkdownLink(m.Index, m.Length, target, m.Groups[2].Value, m.Groups[2].Index, KindOf(target), false));
        }
        foreach (Match m in AutoLink().Matches(line))
            Add(new MarkdownLink(m.Index, m.Length, m.Groups[1].Value, m.Groups[1].Value, m.Groups[1].Index, KindOf(m.Groups[1].Value), false));
        foreach (Match m in BareUrl().Matches(line))
        {
            // Sentence punctuation after an address is not part of it; a closing bracket only when it opened inside.
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '*', '_', '~');
            while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '(')) url = url[..^1].TrimEnd('.', ',', ';', ':', '!', '?');
            if (url.Length < 8) continue;
            Add(new MarkdownLink(m.Index, url.Length, url, url, m.Index, KindOf(url), false));
        }
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>The link the given column of a line is on, if any.</summary>
    public static MarkdownLink? At(string line, int column) => Find(line).FirstOrDefault(l => column >= l.Start && column < l.End);
}
