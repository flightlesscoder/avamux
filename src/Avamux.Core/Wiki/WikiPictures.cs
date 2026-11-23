using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Avamux.Core.Wiki;

/// <summary>A picture a page embeds: where its markdown (or &lt;img&gt; tag) sits in the text, and what it points at.</summary>
/// <param name="Start">Offset of the first character of the embed in the page's text.</param>
/// <param name="Length">Length of the embed's text, e.g. all of <c>![alt](a.png)</c>.</param>
/// <param name="Width">Width in pixels an &lt;img&gt; tag asks for, if any.</param>
/// <param name="Height">Height in pixels an &lt;img&gt; tag asks for, if any.</param>
public sealed record WikiPicture(int Start, int Length, string Url, string Alt, double? Width = null, double? Height = null)
{
    public int End => Start + Length;
}

/// <summary>Where a picture's address leads: a file of the wiki, a web address, or bytes written into the page.</summary>
public sealed record PictureSource(string? WikiPath = null, string? Remote = null, byte[]? Data = null);

/// <summary>
/// Finds the pictures a markdown page shows inline in its preview, so the editor can show them too. The page is
/// read the way <see cref="WikiService.Render"/> reads it: code blocks and code spans show nothing, front matter
/// is not part of the page, and links to videos or recordings are players rather than pictures.
/// </summary>
public static partial class WikiPictures
{
    // The preview's pipeline, plus exact source positions: without them an inline's span inside a list or a quote is
    // off by the container's markers.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UsePreciseSourceLocation().Build();

    /// <summary>Hosts whose links Markdig's media extension turns into an embedded player.</summary>
    private static readonly string[] PlayerHosts = ["youtube.com", "youtu.be", "vimeo.com", "yandex.ru", "ok.ru"];

    [GeneratedRegex(@"<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\balt\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex AltAttribute();

    [GeneratedRegex(@"\b(width|height)\s*=\s*[""']?\s*(\d+(?:\.\d+)?)\s*(?:px)?\s*[""']?(?=[\s/>])", RegexOptions.IgnoreCase)]
    private static partial Regex SizeAttribute();

    /// <summary>Every picture of the page, in the order they appear.</summary>
    public static IReadOnlyList<WikiPicture> Find(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || markdown.IndexOf("![", StringComparison.Ordinal) < 0 && markdown.IndexOf("<img", StringComparison.OrdinalIgnoreCase) < 0) return [];
        // Front matter is shown as a table, not parsed as markdown: blank it out, keeping every offset where it is.
        var skip = FrontMatterLength(markdown);
        var text = skip == 0 ? markdown : new string(markdown.Select((c, i) => i < skip && c is not '\n' and not '\r' ? ' ' : c).ToArray());
        MarkdownDocument doc;
        try { doc = Markdown.Parse(text, Pipeline); }
        catch { return []; }
        var found = new List<WikiPicture>();
        foreach (var node in doc.Descendants())
        {
            switch (node)
            {
                case LinkInline { IsImage: true } link when link.Span.Start >= 0 && link.Span.End < text.Length:
                    var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                    if (url.Length == 0 || IsPlayer(url)) break;
                    found.Add(new WikiPicture(link.Span.Start, link.Span.Length, url, AltOf(link)));
                    break;
                case HtmlInline html when html.Span.Start >= 0:
                    AddTags(found, text, html.Span.Start, html.Span.Length);
                    break;
                case HtmlBlock block when block.Span.Start >= 0:
                    AddTags(found, text, block.Span.Start, Math.Min(block.Span.Length, text.Length - block.Span.Start));
                    break;
            }
        }
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    private static string AltOf(LinkInline link)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in link.Descendants<LiteralInline>()) sb.Append(child.Content.ToString());
        return sb.ToString();
    }

    private static void AddTags(List<WikiPicture> found, string text, int start, int length)
    {
        if (length <= 0) return;
        foreach (Match m in ImgTag().Matches(text.Substring(start, length)))
        {
            var url = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (url.Length == 0) continue;
            var alt = AltAttribute().Match(m.Value) is { Success: true } a ? (a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Value) : "";
            double? width = null, height = null;
            foreach (Match size in SizeAttribute().Matches(m.Value))
            {
                var value = double.Parse(size.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (value <= 0) continue;
                if (size.Groups[1].Value.Equals("width", StringComparison.OrdinalIgnoreCase)) width = value; else height = value;
            }
            found.Add(new WikiPicture(start + m.Index, m.Length, System.Net.WebUtility.HtmlDecode(url), System.Net.WebUtility.HtmlDecode(alt), width, height));
        }
    }

    /// <summary>A link Markdig's media extension shows as a video or audio player, not as a picture.</summary>
    private static bool IsPlayer(string url)
    {
        var path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https")
        {
            var host = abs.Host.ToLowerInvariant();
            if (PlayerHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal))) return true;
            path = abs.AbsolutePath;
        }
        else path = StripQuery(url);
        return WikiService.KindOf(path) is WikiKind.Video or WikiKind.Audio;
    }

    private static string StripQuery(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    /// <summary>Length of the leading front matter block that <see cref="WikiService.SplitFrontMatter"/> takes off, or 0.</summary>
    internal static int FrontMatterLength(string markdown)
    {
        if (!markdown.TrimStart('﻿').StartsWith("---", StringComparison.Ordinal) || WikiService.SplitFrontMatter(markdown).Meta is null) return 0;
        var at = markdown.IndexOf('\n');
        while (at >= 0 && at < markdown.Length)
        {
            var next = markdown.IndexOf('\n', at + 1);
            var line = (next < 0 ? markdown[(at + 1)..] : markdown[(at + 1)..next]).TrimEnd('\r', ' ', '\t');
            if (line is "---" or "...") return next < 0 ? markdown.Length : next + 1;
            at = next;
        }
        return 0;
    }

    /// <summary>
    /// What a picture's address refers to, read the way the preview reads it: relative to the page's folder, with
    /// %-escapes undone. Null for an address the preview cannot show either (outside the wiki, another scheme).
    /// </summary>
    /// <param name="pageFolder">Wiki-relative folder of the page, "" for the top.</param>
    public static PictureSource? Resolve(string url, string pageFolder)
    {
        url = url.Trim();
        if (url.Length == 0) return null;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || !url[5..comma].StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            try { return new PictureSource(Data: Convert.FromBase64String(url[(comma + 1)..])); }
            catch (FormatException) { return null; }
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && !url.StartsWith('/'))
            return abs.Scheme is "http" or "https" ? new PictureSource(Remote: abs.AbsoluteUri) : null;
        var path = StripQuery(url);
        try { path = Uri.UnescapeDataString(path); } catch { }
        path = path.Replace('\\', '/');
        // The preview serves the wiki under /wiki/raw/; an address starting with a slash leaves the page's folder.
        string combined;
        if (path.StartsWith("/wiki/raw/", StringComparison.Ordinal)) combined = path["/wiki/raw/".Length..];
        else if (path.StartsWith('/')) return null;
        else combined = pageFolder.Length == 0 ? path : pageFolder.TrimEnd('/') + "/" + path;
        var parts = new List<string>();
        foreach (var part in combined.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : new PictureSource(WikiPath: string.Join('/', parts));
    }
}
