using System.Text;
using System.Text.RegularExpressions;

namespace Avamux.Plugins.AiActions;

/// <summary>What AI Actions ask the model. Kept together so they are easy to read and adjust.</summary>
public static partial class Prompts
{
    /// <summary>The most of a page sent with a prompt; longer pages are cut (and the model told so).</summary>
    public const int MaxChars = 60_000;

    public static string Clip(string text) =>
        text.Length <= MaxChars ? text : text[..MaxChars] + $"\n\n[… the rest of the page ({text.Length - MaxChars:N0} more characters) was left out]";

    public static string AddTags(string path, string page, IReadOnlyList<string> existing) => $"""
        Suggest tags for this wiki page. Tags are single words or short-words-joined-by-dashes, lower case, and may be nested
        with a slash (project/avamux). Prefer tags the wiki already uses (listed below) when they fit; add new ones only when
        nothing there fits. Suggest 3 to 7 tags that would help find this page again. Leave out tags the page already has.

        Answer with the tags only, on one line, each starting with #, nothing else. For example: #recipes #baking #weekend

        Tags the wiki already uses: {(existing.Count == 0 ? "(none yet)" : string.Join(" ", existing.Take(400).Select(t => "#" + t)))}

        The page ({path}):
        ```markdown
        {Clip(page)}
        ```
        """;

    public static string Summarize(string? path, string text, bool selection) => $"""
        Summarise {(selection ? "this passage" : "this wiki page")} in a few short bullet points, then one sentence on what
        matters most. Keep names, dates and numbers exact. Write in markdown. Do not add a heading.

        {(path is null ? "" : $"({(selection ? "From" : "The page")} {path})")}
        ```markdown
        {Clip(text)}
        ```
        """;

    public static string Format(string text) => $"""
        Tidy the formatting of this markdown without changing what it says: consistent headings, lists, spacing, code
        fences and tables; fix broken markdown. Do not reword, add or remove content, and keep every #tag and [[link]].
        Answer with the markdown only (no fences around the whole answer, no commentary).

        {Clip(text)}
        """;

    public static string Rewrite(string text, string instruction) => $"""
        Rewrite this markdown as asked: {instruction}

        Keep every #tag and [[link]] unless asked otherwise. Answer with the new markdown only (no fences around the
        whole answer, no commentary).

        {Clip(text)}
        """;

    public static string Explain(string text) => $"""
        Explain this terminal output briefly: what happened, and what (if anything) to do next. Write in markdown.

        ```
        {Clip(text)}
        ```
        """;

    /// <summary>The system prompt for research in the wiki (Do Research…, Ask About This Page…).</summary>
    public static string Research(string wikiFolder, string? about = null) => $"""
        You are helping the user research their own notes. Your working folder is their avamux wiki: {wikiFolder}
        It is a folder of markdown pages (subfolders, front matter, [[wiki links]], #tags; nested tags use a slash).
        Daily notes live in Daily/ and are named like "2026-11-19 Thursday.md".

        - Search before answering: use grep / ripgrep (or your search tool) over the folder for the words, names and
          synonyms in the question, then read the pages that match. Look in more than one page when the answer may be
          spread out; dates in daily notes' names tell you when things were written.
        - Answer from what the pages say, and name the pages you used (their paths, e.g. Daily/2026-11-19 Thursday.md),
          quoting the deciding words. If the pages do not say, say so rather than guess.
        - The `avamux` command line also works here: `avamux wiki search <text>`, `avamux wiki tag <tag>`,
          `avamux wiki read <page>`, `avamux wiki write <page> --append <text>` (to add what you found to a page, if
          asked), `avamux terminal list`, `avamux terminal read <id>` and `avamux terminal send <id> <text>` (to take
          from or put into a terminal, if asked). Do not change pages unless the user asks you to.
        {(about is null ? "" : "\nThe user is asking about " + about + ". Read it first.")}
        """;

    /// <summary>A model's answer without a fence wrapped around all of it, and without a leading "Here is…" line.</summary>
    public static string Unwrap(string reply)
    {
        var text = reply.Trim();
        var fence = FenceAll().Match(text);
        if (fence.Success) text = fence.Groups[1].Value;
        return text.TrimEnd() + "\n";
    }

    [GeneratedRegex(@"^```[a-zA-Z]*\n([\s\S]*?)\n```$")]
    private static partial Regex FenceAll();

    [GeneratedRegex(@"#?([\p{L}][\p{L}\p{N}_/-]{0,60})")]
    private static partial Regex TagWord();

    /// <summary>The tags in a reply like "#a #b/c, d", lower case, without '#', in order, each once.</summary>
    public static List<string> ParseTags(string reply)
    {
        var line = reply.Trim().Split('\n').FirstOrDefault(l => l.Contains('#')) ?? reply;
        var seen = new HashSet<string>();
        return TagWord().Matches(line).Select(m => m.Groups[1].Value.ToLowerInvariant().TrimEnd('/', '-')).Where(t => t.Length > 0 && seen.Add(t)).Take(12).ToList();
    }

    /// <summary>Fills a custom action's placeholders.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template);
        foreach (var (k, v) in values) sb.Replace("{{" + k + "}}", v);
        return sb.ToString();
    }
}

/// <summary>A line diff (longest common subsequence), for showing what Format and Rewrite change before it is accepted.</summary>
public static class LineDiff
{
    public enum Kind { Same, Removed, Added }

    public static List<(Kind Kind, string Line)> Compute(string before, string after)
    {
        var a = before.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var b = after.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        // Big inputs: skip the common start and end before the quadratic part.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var endA = a.Length; var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        var n = endA - start; var m = endB - start;
        var result = new List<(Kind, string)>();
        for (var i = 0; i < start; i++) result.Add((Kind.Same, a[i]));
        if ((long)n * m > 4_000_000)
        {
            for (var i = start; i < endA; i++) result.Add((Kind.Removed, a[i]));
            for (var j = start; j < endB; j++) result.Add((Kind.Added, b[j]));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { result.Add((Kind.Same, a[start + x])); x++; y++; }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1])) { result.Add((Kind.Removed, a[start + x])); x++; }
                else { result.Add((Kind.Added, b[start + y])); y++; }
            }
        }
        for (var i = endA; i < a.Length; i++) result.Add((Kind.Same, a[i]));
        return result;
    }
}
