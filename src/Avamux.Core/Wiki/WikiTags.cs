using System.Text;

namespace Avamux.Core.Wiki;

/// <summary>One place a tag appears in a page: the tag as written (without '#'), its 1-based line, and where on that line.</summary>
public readonly record struct TagMention(string Tag, int Line, int Offset, int Length);

/// <summary>
/// Finds #tags in markdown. A tag is '#' then a letter, then letters, digits, '_', '-' and '/' (nesting:
/// <c>#avamux/browser</c>). Tags are compared without regard to case. Not tags: anything in code (fenced, indented or
/// inline), link targets and whole links, URLs, HTML tags, headings (<c># Title</c>), and a '#' that follows a letter,
/// digit or another symbol that joins it to a word (<c>page#anchor</c>, <c>C#</c>, <c>&amp;#38;</c>). Front matter's
/// <c>tags:</c> (a list, <c>[a, b]</c>, or words separated by commas or spaces) counts too.
/// </summary>
public static class WikiTags
{
    public const int MaxTagLength = 100;

    /// <summary>The canonical form a tag is indexed and compared by: lower case, no '#', no trailing '/' or '-'.</summary>
    public static string Normalize(string tag)
    {
        tag = tag.Trim().TrimStart('#').TrimEnd('/', '-', '_');
        return tag.ToLowerInvariant();
    }

    /// <summary>True when <paramref name="tag"/> is <paramref name="parent"/> or nested under it (both normalized).</summary>
    public static bool IsUnder(string tag, string parent) =>
        tag == parent || tag.Length > parent.Length && tag[parent.Length] == '/' && tag.StartsWith(parent, StringComparison.Ordinal);

    public static bool IsValid(string tag)
    {
        tag = tag.TrimStart('#');
        if (tag.Length == 0 || tag.Length > MaxTagLength || !char.IsLetter(tag[0])) return false;
        foreach (var c in tag) if (!IsTagChar(c)) return false;
        return true;
    }

    private static bool IsTagChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '/';

    /// <summary>What may stand right before a tag's '#': nothing, white space, or punctuation that opens or separates.</summary>
    private static bool CanPrecede(char c) => char.IsWhiteSpace(c) || c is '(' or '[' or '{' or '"' or '\'' or '*' or '_' or '~' or '>' or ',' or ';' or ':' or '!' or '?' or '|';

    /// <summary>Every tag mention in a page, front matter first, in the order they appear.</summary>
    public static List<TagMention> Find(string markdown)
    {
        var result = new List<TagMention>();
        var text = markdown.Replace("\r\n", "\n");
        var lines = text.Split('\n');
        var start = 0;
        // Front matter: the tags: entry, then the body starts after the closing line.
        if (lines.Length > 0 && lines[0].TrimStart('﻿').TrimEnd() == "---")
        {
            var end = Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...");
            if (end > 0)
            {
                FrontMatterTags(lines, end, result);
                start = end + 1;
            }
        }

        foreach (var (i, line) in BodyLines(lines, start)) ScanLine(line, i + 1, result);
        return result;
    }

    /// <summary>The lines of the body that hold ordinary text (not code), with their index, from <paramref name="start"/> on.</summary>
    internal static IEnumerable<(int Index, string Line)> BodyLines(string[] lines, int start)
    {
        string? fence = null;      // the fence that opened the code block we are in
        var previousBlank = true;   // indented code needs a blank line (or more code) above it
        var inIndentedCode = false;
        var inList = false;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart(' ');
            var indent = line.Length - trimmed.Length;
            if (fence is not null)
            {
                if (indent < 4 && trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.TrimEnd().Trim(fence[0]).Length == 0) fence = null;
                continue;
            }
            if (indent < 4 && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                var c = trimmed[0];
                var n = 0;
                while (n < trimmed.Length && trimmed[n] == c) n++;
                fence = new string(c, n);
                previousBlank = false;
                continue;
            }
            if (line.Trim().Length == 0) { previousBlank = true; continue; }
            var tabbed = line.StartsWith('\t');
            var listItem = IsListItem(trimmed.TrimStart('\t'));
            if ((indent >= 4 || tabbed) && !listItem && !inList && (previousBlank || inIndentedCode))
            {
                inIndentedCode = true;
                previousBlank = false;
                continue;
            }
            inIndentedCode = false;
            if (listItem) inList = true;
            else if (indent == 0 && !tabbed && previousBlank) inList = false;
            previousBlank = false;
            yield return (i, line);
        }
    }

    /// <summary>The distinct tags of a page, normalized, in the order they first appear.</summary>
    public static List<string> Distinct(string markdown)
    {
        var seen = new HashSet<string>();
        var list = new List<string>();
        foreach (var m in Find(markdown))
            if (seen.Add(Normalize(m.Tag))) list.Add(Normalize(m.Tag));
        return list;
    }

    private static bool IsListItem(string t)
    {
        if (t.Length >= 2 && t[0] is '-' or '*' or '+' && t[1] == ' ') return true;
        var n = 0;
        while (n < t.Length && char.IsDigit(t[n])) n++;
        return n > 0 && n + 1 < t.Length && t[n] is '.' or ')' && t[n + 1] == ' ';
    }

    private static void FrontMatterTags(string[] lines, int end, List<TagMention> result)
    {
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0])) continue;
            var key = line[..colon].Trim();
            if (!key.Equals("tags", StringComparison.OrdinalIgnoreCase) && !key.Equals("tag", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(colon + 1)..].Trim();
            if (value.Length > 0)
            {
                AddWords(value.Trim('[', ']'), i + 1, colon + 1, result);
                continue;
            }
            for (var j = i + 1; j < end && lines[j].TrimStart().StartsWith("- ", StringComparison.Ordinal); j++)
                AddWords(lines[j].TrimStart()[2..], j + 1, lines[j].IndexOf('-') + 2, result);
        }
    }

    private static void AddWords(string value, int line, int baseOffset, List<TagMention> result)
    {
        foreach (var raw in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim().Trim('"', '\'').TrimStart('#');
            if (IsValid(word)) result.Add(new TagMention(word, line, baseOffset, word.Length));
        }
    }

    /// <summary>Finds tags on one line of ordinary markdown, skipping inline code, links, URLs and HTML.</summary>
    private static void ScanLine(string line, int number, List<TagMention> result)
    {
        foreach (var (offset, length) in Spans(line))
            result.Add(new TagMention(line.Substring(offset + 1, length - 1), number, offset, length));
    }

    /// <summary>Where the tags of one line of ordinary markdown are: offset of the '#' and length including it.</summary>
    public static IEnumerable<(int Offset, int Length)> Spans(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '`')
            {
                // Inline code: a run of backticks up to the same run.
                var n = 0;
                while (i + n < line.Length && line[i + n] == '`') n++;
                var close = line.IndexOf(new string('`', n), i + n, StringComparison.Ordinal);
                i = close < 0 ? i + n : close + n;
                continue;
            }
            if (c == '[')
            {
                // [[Wiki link]], [text](target), [text][ref] and ![picture](target): skipped whole.
                var end = LinkEnd(line, i);
                if (end > i) { i = end; continue; }
            }
            if (c == '<')
            {
                var close = line.IndexOf('>', i + 1);
                if (close > i + 1 && (char.IsLetter(line[i + 1]) || line[i + 1] is '/' or '!')) { i = close + 1; continue; }
            }
            if (char.IsLetter(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
            {
                // A URL: scheme:// up to white space.
                var j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '+' or '.' or '-')) j++;
                if (j + 2 < line.Length && line[j] == ':' && line[j + 1] == '/' && line[j + 2] == '/')
                {
                    while (j < line.Length && !char.IsWhiteSpace(line[j]) && line[j] != '>') j++;
                    i = j;
                    continue;
                }
                if (j + 7 <= line.Length && line.AsSpan(i, j - i).Equals("www", StringComparison.OrdinalIgnoreCase) && line[j] == '.')
                {
                    while (j < line.Length && !char.IsWhiteSpace(line[j])) j++;
                    i = j;
                    continue;
                }
                i = j;
                continue;
            }
            if (c == '#' && (i == 0 || CanPrecede(line[i - 1])) && i + 1 < line.Length && char.IsLetter(line[i + 1]))
            {
                var j = i + 1;
                while (j < line.Length && IsTagChar(line[j])) j++;
                var end = j;
                while (end > i + 1 && line[end - 1] is '/' or '-' or '_') end--;
                // "#tag#more" is not a tag; neither is a run longer than any sensible tag.
                if ((j >= line.Length || line[j] != '#') && end - i - 1 <= MaxTagLength) yield return (i, end - i);
                i = j;
                continue;
            }
            i++;
        }
    }

    /// <summary>The index just past a link that starts at <paramref name="i"/> ('['), or <paramref name="i"/> when there is none.</summary>
    private static int LinkEnd(string line, int i)
    {
        if (i + 1 < line.Length && line[i + 1] == '[')
        {
            var close = line.IndexOf("]]", i + 2, StringComparison.Ordinal);
            return close < 0 ? i : close + 2;
        }
        var depth = 0;
        var j = i;
        for (; j < line.Length; j++)
        {
            if (line[j] == '\\') { j++; continue; }
            if (line[j] == '[') depth++;
            else if (line[j] == ']' && --depth == 0) break;
        }
        if (j >= line.Length - 1) return i;
        if (line[j + 1] == '(')
        {
            var close = line.IndexOf(')', j + 2);
            return close < 0 ? i : close + 1;
        }
        if (line[j + 1] == '[')
        {
            var close = line.IndexOf(']', j + 2);
            return close < 0 ? i : close + 1;
        }
        return i;
    }

    /// <summary>
    /// The markdown with each tag of the body turned into a link the preview understands
    /// (<c>&lt;a class="tag" data-tag="…"&gt;</c>), leaving code, links and front matter as they were.
    /// </summary>
    public static string Linkify(string body)
    {
        if (body.IndexOf('#') < 0) return body;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        // Front matter is not part of a body; a body that starts with "---" lines is scanned from the top like any other.
        var sb = (StringBuilder?)null;
        var next = 0;
        foreach (var (i, line) in BodyLines(lines, 0))
        {
            if (line.IndexOf('#') < 0) continue;
            var at = 0;
            StringBuilder? lineOut = null;
            foreach (var (offset, length) in Spans(line))
            {
                lineOut ??= new StringBuilder(line.Length + 64);
                lineOut.Append(line, at, offset - at);
                var tag = System.Net.WebUtility.HtmlEncode(Normalize(line.Substring(offset + 1, length - 1)));
                lineOut.Append("<a class=\"tag\" href=\"#\" data-tag=\"").Append(tag).Append("\">").Append(System.Net.WebUtility.HtmlEncode(line.Substring(offset, length))).Append("</a>");
                at = offset + length;
            }
            if (lineOut is null) continue;
            lineOut.Append(line, at, line.Length - at);
            sb ??= new StringBuilder(body.Length + 256);
            for (; next < i; next++) sb.Append(lines[next]).Append('\n');
            sb.Append(lineOut);
            if (i < lines.Length - 1) sb.Append('\n');
            next = i + 1;
        }
        if (sb is null) return body;
        for (; next < lines.Length; next++) { sb.Append(lines[next]); if (next < lines.Length - 1) sb.Append('\n'); }
        return sb.ToString();
    }

    /// <summary>
    /// Adds tags to a page: into its front matter's <c>tags:</c> when it has one, otherwise onto a "Tags:" line at the
    /// end (extending the one already there). Tags the page already has are left out.
    /// </summary>
    public static string AddTags(string markdown, IEnumerable<string> tags)
    {
        var have = Distinct(markdown).ToHashSet();
        var add = tags.Select(t => t.Trim().TrimStart('#')).Where(IsValid).Where(t => have.Add(Normalize(t))).ToList();
        if (add.Count == 0) return markdown;
        var newline = markdown.Contains("\r\n") ? "\r\n" : "\n";
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart('﻿').TrimEnd() == "---")
        {
            var end = lines.FindIndex(1, l => l.TrimEnd() is "---" or "...");
            if (end > 0)
            {
                var key = lines.FindIndex(1, end - 1, l => l.StartsWith("tags:", StringComparison.OrdinalIgnoreCase));
                if (key < 0) lines.Insert(end, "tags: [" + string.Join(", ", add) + "]");
                else
                {
                    var value = lines[key][5..].Trim();
                    if (value.StartsWith('[') && value.EndsWith(']'))
                    {
                        var inner = value[1..^1].Trim();
                        lines[key] = "tags: [" + (inner.Length > 0 ? inner + ", " : "") + string.Join(", ", add) + "]";
                    }
                    else if (value.Length > 0) lines[key] = "tags: " + value + ", " + string.Join(", ", add);
                    else
                    {
                        var after = key + 1;
                        while (after < end && lines[after].TrimStart().StartsWith("- ", StringComparison.Ordinal)) after++;
                        lines.InsertRange(after, add.Select(t => "  - " + t));
                    }
                }
                return string.Join(newline, lines);
            }
        }
        var tagLine = string.Join(' ', add.Select(t => "#" + t));
        // Extend an existing "Tags:" line near the end, else add one after a blank line.
        var last = lines.FindLastIndex(l => l.Trim().Length > 0);
        if (last >= 0 && lines[last].TrimStart().StartsWith("Tags:", StringComparison.OrdinalIgnoreCase))
        {
            lines[last] = lines[last].TrimEnd() + " " + tagLine;
            return string.Join(newline, lines);
        }
        var body = string.Join(newline, lines).TrimEnd('\n', '\r', ' ');
        return (body.Length > 0 ? body + newline + newline : "") + "Tags: " + tagLine + newline;
    }
}
