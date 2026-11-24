using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avamux.Core.Config;
using Avamux.Core.Text;
using Avamux.Core.Theming;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.AspNetCore.Http;

namespace Avamux.Core.Wiki;

public sealed record WikiNode(string Name, string Path, bool IsFolder, List<WikiNode> Children);

public enum WikiKind { Markdown, Text, Image, Pdf, Audio, Video, Archive, Other }

/// <summary>A folder of markdown files (and images) under the app's data folder.</summary>
public sealed partial class WikiService
{
    private readonly AppPaths _paths;
    private readonly Func<long> _previewLimit;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseEmojiAndSmiley().Use<HighlightExtension>().Build();

    /// <param name="previewLimit">Size in bytes above which a picture, document or recording is only shown after a click. Null or 0 shows everything at once.</param>
    public WikiService(AppPaths paths, Func<long>? previewLimit = null)
    {
        _paths = paths;
        _previewLimit = previewLimit ?? (() => 0);
        var home = System.IO.Path.Combine(Root, "Home.md");
        if (!Directory.EnumerateFileSystemEntries(Root).Any())
            File.WriteAllText(home, "# Home\n\nWelcome to your avamux wiki.\n\n- Pages are markdown files stored in `" + Root + "`.\n- Link to another page with `[Notes](Notes.md)` or `[[Notes]]`.\n- Put images next to a page and embed them with `![alt](picture.png)`.\n\n| Shortcut | Action |\n|---|---|\n| Ctrl+S | Save |\n");
    }

    public string Root => _paths.WikiDir;
    public event Action? Changed;
    /// <summary>Raised with the wiki path that was written, moved (both names) or deleted; null when anything may have changed.</summary>
    public event Action<string?>? PathChanged;

    private void Raise(params string?[] paths)
    {
        foreach (var p in paths) PathChanged?.Invoke(p);
        Changed?.Invoke();
    }

    /// <summary>The id the wiki folder goes by as a git repository (see <see cref="Repo"/>).</summary>
    public const string RepoId = "@wiki";
    /// <summary>The wiki folder as a repository, for keeping its history in git.</summary>
    public Repos.RepoEntry Repo => new() { Id = RepoId, Name = "Wiki", Path = Root };

    /// <summary>Says the files changed underneath the wiki (a branch was switched, a commit reverted).</summary>
    public void NotifyChanged() => Raise((string?)null);

    public string Resolve(string path) => PathSafety.Combine(Root, path);

    public WikiNode Tree() => Build(new DirectoryInfo(Root));

    private WikiNode Build(DirectoryInfo dir)
    {
        var children = new List<WikiNode>();
        foreach (var d in dir.EnumerateDirectories().Where(d => !d.Name.StartsWith('.')).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            children.Add(Build(d));
        foreach (var f in dir.EnumerateFiles().Where(f => !f.Name.StartsWith('.')).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            children.Add(new WikiNode(f.Name, PathSafety.Relative(Root, f.FullName), false, []));
        return new WikiNode(dir.FullName == System.IO.Path.GetFullPath(Root) ? "Wiki" : dir.Name, dir.FullName == System.IO.Path.GetFullPath(Root) ? "" : PathSafety.Relative(Root, dir.FullName), true, children);
    }

    public IEnumerable<string> Pages() => Flatten(Tree()).Where(n => !n.IsFolder).Select(n => n.Path);
    private static IEnumerable<WikiNode> Flatten(WikiNode n) => n.Children.SelectMany(c => new[] { c }.Concat(Flatten(c)));

    public static bool IsMarkdown(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".csv", ".tsv", ".rst", ".org", ".adoc", ".tex", ".bib", ".srt", ".vtt", ".gitignore", ".gitattributes", ".dockerignore",
        ".lock", ".list", ".nfo", ".out", ".err", ".diff", ".patch", ".sln", ".ps1", ".bat", ".cmd", ".vim", ".el", ".lisp", ".clj", ".ex", ".exs", ".erl", ".hs", ".ml", ".r", ".jl", ".pl", ".pm", ".tf", ".hcl", ".nix", ".proto", ".graphql", ".gql",
    };

    /// <summary>What a file is, judged by its name alone.</summary>
    public static WikiKind KindOf(string path)
    {
        if (IsMarkdown(path)) return WikiKind.Markdown;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".bmp" or ".avif" or ".ico" or ".apng" => WikiKind.Image,
            ".pdf" => WikiKind.Pdf,
            ".mp3" or ".wav" or ".ogg" or ".oga" or ".flac" or ".m4a" or ".aac" or ".opus" => WikiKind.Audio,
            ".mp4" or ".webm" or ".mov" or ".m4v" or ".ogv" or ".mkv" => WikiKind.Video,
            ".zip" or ".jar" or ".nupkg" or ".apk" => WikiKind.Archive,
            _ => TextExtensions.Contains(ext) || Syntax.LanguageOf(path) is not null ? WikiKind.Text : WikiKind.Other,
        };
    }

    /// <summary>
    /// Like <see cref="KindOf"/>, but a file with an unfamiliar name is also looked into: if its first block has
    /// no NUL byte it is treated as text.
    /// </summary>
    public WikiKind Kind(string path)
    {
        var kind = KindOf(path);
        if (kind != WikiKind.Other) return kind;
        try
        {
            using var f = File.OpenRead(Resolve(path));
            var buf = new byte[4096];
            var n = f.Read(buf, 0, buf.Length);
            return Array.IndexOf(buf, (byte)0, 0, n) < 0 ? WikiKind.Text : WikiKind.Other;
        }
        catch { return WikiKind.Other; }
    }

    /// <summary>Largest file opened in the editor.</summary>
    public const long MaxEditableBytes = 2 * 1024 * 1024;

    public bool IsEditable(string path)
    {
        try { return Kind(path) is WikiKind.Markdown or WikiKind.Text && new FileInfo(Resolve(path)).Length <= MaxEditableBytes; }
        catch { return false; }
    }

    /// <summary>Finds a page by path, tolerating a missing ".md".</summary>
    public string? FindPage(string path)
    {
        path = path.Trim().TrimStart('/');
        foreach (var candidate in new[] { path, path + ".md" })
        {
            try { if (File.Exists(Resolve(candidate))) return candidate; } catch { return null; }
        }
        // [[Page]] links: search by file name anywhere.
        var name = System.IO.Path.GetFileName(path);
        return Pages().FirstOrDefault(p => string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
    }

    public string Read(string path) => File.ReadAllText(Resolve(FindPage(path) ?? throw new FileNotFoundException($"No wiki page '{path}'.")));

    public string Write(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A page path is required.");
        if (System.IO.Path.GetExtension(path).Length == 0) path += ".md";
        var full = Resolve(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        var rel = PathSafety.Relative(Root, full);
        Raise(rel);
        return rel;
    }

    public void CreateFolder(string path) { Directory.CreateDirectory(Resolve(path)); Raise(path); }

    public void Delete(string path)
    {
        var full = Resolve(path);
        if (full == System.IO.Path.GetFullPath(Root)) throw new InvalidOperationException("Cannot delete the wiki root.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
        else if (File.Exists(full)) File.Delete(full);
        else throw new FileNotFoundException($"No wiki entry '{path}'.");
        Raise(PathSafety.Relative(Root, full));
    }

    public string Move(string from, string to)
    {
        var src = Resolve(from);
        if (File.Exists(src) && System.IO.Path.GetExtension(to).Length == 0) to += System.IO.Path.GetExtension(src);
        var dst = Resolve(to);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
        if (Directory.Exists(src)) Directory.Move(src, dst);
        else if (File.Exists(src)) File.Move(src, dst);
        else throw new FileNotFoundException($"No wiki entry '{from}'.");
        var moved = PathSafety.Relative(Root, dst);
        Raise(PathSafety.Relative(Root, src), moved);
        return moved;
    }

    /// <summary>Copies an external file (usually an image) next to a page; returns its wiki path.</summary>
    public string Import(string sourceFile, string folder)
    {
        var dst = FreePath(folder, System.IO.Path.GetFileName(sourceFile));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
        File.Copy(sourceFile, dst);
        Raise(PathSafety.Relative(Root, dst));
        return PathSafety.Relative(Root, dst);
    }

    /// <summary>
    /// Full path for a new file in a wiki folder: the name itself if it is free, otherwise "name-N.ext" with N one past
    /// the highest number already used. The folder is listed once, rather than trying N = 1, 2, 3… one file at a time,
    /// which cost thousands of checks per file in a folder holding thousands of copies of one name.
    /// </summary>
    private string FreePath(string folder, string name)
    {
        var dst = Resolve(System.IO.Path.Combine(folder, name));
        if (!File.Exists(dst)) return dst;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        var next = 1;
        try
        {
            foreach (var file in Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(dst)!, stem + "-*" + ext))
            {
                var middle = System.IO.Path.GetFileName(file);
                if (middle.Length <= stem.Length + 1 + ext.Length) continue;
                middle = middle.Substring(stem.Length + 1, middle.Length - stem.Length - 1 - ext.Length);
                if (int.TryParse(middle, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= next) next = n + 1;
            }
        }
        catch (IOException) { }
        for (; ; next++)
        {
            dst = Resolve(System.IO.Path.Combine(folder, $"{stem}-{next}{ext}"));
            if (!File.Exists(dst)) return dst;
        }
    }

    /// <summary>Writes bytes (a pasted picture, say) into a folder under a name that is not taken yet; returns its wiki path.</summary>
    public string ImportBytes(byte[] data, string folder, string name)
    {
        name = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) name = "file";
        var dst = FreePath(folder, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
        File.WriteAllBytes(dst, data);
        Raise(PathSafety.Relative(Root, dst));
        return PathSafety.Relative(Root, dst);
    }

    /// <summary>Markdown that embeds or links a file sitting next to the page.</summary>
    public static string LinkMarkdown(string fileName)
    {
        var target = fileName.Any(c => c is ' ' or '(' or ')') ? "<" + fileName + ">" : fileName;
        var label = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return KindOf(fileName) == WikiKind.Image ? $"![{label}]({target})" : $"[{label}]({target})";
    }

    // ---- daily notes ----

    /// <summary>Wiki path of the daily note for a date, from wiki.dailyNoteFormat and wiki.dailyNoteFolder.</summary>
    public static string DailyNotePath(DateTime date, WikiSettings s)
    {
        string name;
        try { name = date.ToString(string.IsNullOrWhiteSpace(s.DailyNoteFormat) ? "yyyy-MM-dd dddd" : s.DailyNoteFormat, CultureInfo.CurrentCulture); }
        catch (FormatException) { name = date.ToString("yyyy-MM-dd dddd", CultureInfo.CurrentCulture); }
        // A format may produce path separators (yyyy/MM/dd): keep those as folders, drop anything else a file name cannot hold.
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => string.Concat(p.Split(System.IO.Path.GetInvalidFileNameChars())).Replace(':', '-').Trim()).Where(p => p.Length > 0 && p != "." && p != "..").ToList();
        if (parts.Count == 0) parts.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var folder = (s.DailyNoteFolder ?? "").Replace('\\', '/').Trim('/', ' ');
        return (folder.Length > 0 ? folder + "/" : "") + string.Join('/', parts) + ".md";
    }

    /// <summary>Returns the daily note for a date, creating it from wiki.dailyNoteTemplate when it does not exist.</summary>
    public string EnsureDailyNote(DateTime date, WikiSettings s)
    {
        var path = DailyNotePath(date, s);
        if (File.Exists(Resolve(path))) return path;
        var title = System.IO.Path.GetFileNameWithoutExtension(path);
        var content = (s.DailyNoteTemplate ?? "").Replace("\\n", "\n").Replace("{title}", title).Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Write(path, content);
    }

    public IReadOnlyList<(string Path, int Line, string Text)> Search(string query)
    {
        var hits = new List<(string, int, string)>();
        foreach (var page in Pages().Where(p => KindOf(p) is WikiKind.Markdown or WikiKind.Text))
        {
            string[] lines;
            try { if (new FileInfo(Resolve(page)).Length > MaxEditableBytes) continue; lines = File.ReadAllLines(Resolve(page)); }
            catch { continue; }
            for (var i = 0; i < lines.Length && hits.Count < 500; i++)
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add((page, i + 1, lines[i].Trim()));
        }
        return hits;
    }


    // ---- rendering ----

    // Group 1 is code (fenced or inline), which is left alone; groups 2 and 3 are a [[Target|Label]] link.
    [GeneratedRegex(@"(```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]+`)|\[\[([^\]|]+)(?:\|([^\]]+))?\]\]")]
    private static partial Regex WikiLink();

    public sealed record FrontMatterEntry(string Key, IReadOnlyList<string> Values, bool IsList);

    /// <summary>
    /// Splits a leading YAML front matter block (between "---" lines) from the body. Understands the common
    /// shapes: "key: value", inline lists "[a, b]" and "- item" lists; anything deeper is kept as text.
    /// </summary>
    public static (IReadOnlyList<FrontMatterEntry>? Meta, string Body) SplitFrontMatter(string markdown)
    {
        var text = markdown.TrimStart('﻿');
        if (!text.StartsWith("---", StringComparison.Ordinal)) return (null, markdown);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines[0].TrimEnd() != "---") return (null, markdown);
        var end = Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...");
        if (end < 0) return (null, markdown);

        var entries = new List<(string Key, List<string> Values, bool IsList)>();
        static string Unquote(string v)
        {
            v = v.Trim();
            return v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;
        }
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var indented = char.IsWhiteSpace(line[0]);
            var item = line.TrimStart();
            if (entries.Count > 0 && item.StartsWith("- ") && (indented || entries[^1].Values.Count == 0 || entries[^1].IsList))
            {
                var e = entries[^1];
                e.Values.Add(Unquote(item[2..]));
                entries[^1] = (e.Key, e.Values, true);
                continue;
            }
            var colon = line.IndexOf(':');
            if (!indented && colon > 0 && (colon == line.Length - 1 || line[colon + 1] == ' '))
            {
                var value = line[(colon + 1)..].Trim();
                if (value.StartsWith('[') && value.EndsWith(']'))
                    entries.Add((line[..colon].Trim(), value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Unquote).ToList(), true));
                else entries.Add((line[..colon].Trim(), value.Length == 0 || value is "|" or ">" or "|-" or ">-" ? [] : [Unquote(value)], false));
                continue;
            }
            // Nested maps and block scalars: keep the text with the key it belongs to.
            if (entries.Count > 0) entries[^1].Values.Add(indented ? line.Trim() : line);
            else return (null, markdown);
        }
        var body = string.Join('\n', lines.Skip(end + 1));
        return (entries.Select(e => new FrontMatterEntry(e.Key, e.Values, e.IsList)).ToList(), body);
    }

    private static string FrontMatterHtml(IReadOnlyList<FrontMatterEntry> meta)
    {
        if (meta.Count == 0) return "";
        var sb = new StringBuilder("<table class=\"frontmatter\"><tbody>");
        foreach (var e in meta)
        {
            sb.Append("<tr><th>").Append(WebUtility.HtmlEncode(e.Key)).Append("</th><td>");
            if (e.IsList) foreach (var v in e.Values) sb.Append("<span class=\"chip\">").Append(WebUtility.HtmlEncode(v)).Append("</span>");
            else
            {
                var v = string.Join("\n", e.Values);
                var enc = WebUtility.HtmlEncode(v);
                sb.Append(v.StartsWith("http://") || v.StartsWith("https://") ? $"<a href=\"{enc}\">{enc}</a>" : enc.Replace("\n", "<br>"));
            }
            sb.Append("</td></tr>");
        }
        return sb.Append("</tbody></table>\n").ToString();
    }

    /// <param name="sourceLines">
    /// Mark each block with the page line it starts on (<c>data-line</c>, counted from 1 like the editor), so the
    /// preview can scroll along with the editor.
    /// </param>
    public static string Render(string markdown, bool sourceLines = false)
    {
        var (meta, body) = SplitFrontMatter(markdown);
        // Tags and [[links]] become markdown on the same line, so line numbers in the body stay as written.
        var text = WikiLink().Replace(WikiTags.Linkify(body), m =>
        {
            if (m.Groups[1].Success) return m.Value;
            var target = m.Groups[2].Value.Trim();
            var label = m.Groups[3].Success ? m.Groups[3].Value.Trim() : target;
            return $"[{label}]({Uri.EscapeDataString(target).Replace("%2F", "/")}.md)";
        });
        var front = meta is null ? "" : FrontMatterHtml(meta);
        if (!sourceLines) return front + Markdown.ToHtml(text, Pipeline);
        if (front.Length > 0) front = front.Replace("<table class=\"frontmatter\"", "<table class=\"frontmatter\" data-line=\"1\"");
        var skipped = Newlines(markdown) - Newlines(body); // the front matter's lines
        var doc = Markdown.Parse(text, Pipeline);
        foreach (var block in doc.Descendants<Block>())
            if (block is not (LinkReferenceDefinitionGroup or LinkReferenceDefinition or BlankLineBlock))
                block.GetAttributes().AddProperty("data-line", (block.Line + 1 + skipped).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        return front + writer;
    }

    private static int Newlines(string text)
    {
        var n = 0;
        foreach (var c in text) if (c == '\n') n++;
        return n;
    }

    /// <summary>Fenced code blocks go through <see cref="Syntax"/>, using the language named after the opening fence.</summary>
    private sealed class HighlightExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) { }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer html) return;
            var original = html.ObjectRenderers.FindExact<CodeBlockRenderer>();
            if (original is not null) html.ObjectRenderers.Remove(original);
            html.ObjectRenderers.AddIfNotAlready(new HighlightRenderer(original ?? new CodeBlockRenderer()));
        }
    }

    private sealed class HighlightRenderer(CodeBlockRenderer fallback) : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            var info = (block as FencedCodeBlock)?.Info;
            // Blocks the stock renderer turns into something else (diagrams, maths) stay with it.
            if (!renderer.EnableHtmlForBlock || (info is not null && fallback.BlocksAsDiv.Contains(info))) { fallback.Write(renderer, block); return; }
            var lang = Syntax.Normalize(info);
            renderer.EnsureLine();
            renderer.Write("<pre");
            // Only the source line (Render with sourceLines): the class Markdig gives fenced blocks goes on <code> below.
            if (block.TryGetAttributes()?.Properties?.FirstOrDefault(p => p.Key == "data-line") is { Key: not null } line)
                renderer.Write(" data-line=\"").WriteEscape(line.Value ?? "").Write("\"");
            if (!string.IsNullOrWhiteSpace(info)) renderer.Write(" data-lang=\"").WriteEscape(info.Trim()).Write("\"");
            renderer.Write("><code");
            if (!string.IsNullOrWhiteSpace(info)) renderer.Write(" class=\"language-").WriteEscape(info.Trim().Split(' ')[0]).Write("\"");
            renderer.Write(">");
            var code = block.Lines.ToString();
            if (lang is null) renderer.WriteEscape(code); else renderer.Write(Syntax.ToHtml(code, lang));
            renderer.WriteLine("</code></pre>");
        }
    }

    private static string FileBar(string name, string detail) =>
        $"<div class=\"filebar\"><span class=\"fname\">{WebUtility.HtmlEncode(name)}</span><span class=\"fmeta\">{WebUtility.HtmlEncode(detail)}</span>" +
        "<button data-act=\"open\" title=\"Open with the default application for this kind of file\">Open in default app</button>" +
        "<button data-act=\"show\" title=\"Show the file in the file manager\">Show in folder</button></div>";

    /// <summary>
    /// Preview HTML for a file that is not markdown: a zoomable image, an embedded PDF, an audio or video player,
    /// highlighted text with line numbers, or a note that the default application can open it.
    /// </summary>
    /// <summary>Shown in place of a big file until the reader asks for it; the real markup waits in a template that the preview page swaps in.</summary>
    private static string ClickToLoad(string what, long length, string markup) =>
        $"<div class=\"bigfile\"><p>This {what} is {FormatSize(length)}, so it is not loaded automatically.</p>" +
        "<button data-load=\"1\">Load preview</button>" +
        $"<template>{markup}</template></div>";

    private static string FormatSize(long length) =>
        length < 1024 ? length + " B" : length < 1048576 ? $"{length / 1024.0:0.#} KB" : length < 1073741824 ? $"{length / 1048576.0:0.##} MB" : $"{length / 1073741824.0:0.##} GB";

    private const int MaxTableCells = 40000;

    /// <summary>Comma- or tab-separated text as a table. Quoted fields (with doubled quotes and line breaks inside) are understood.</summary>
    private static string CsvTable(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var cells = 0;
        bool truncated = false;
        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow() { EndField(); cells += row.Count; rows.Add(row); row = []; }
        for (var i = 0; i < text.Length && !truncated; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = false; }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == separator) EndField();
            else if (c == '\n') { EndRow(); truncated = cells > MaxTableCells; }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        if (rows.Count == 0) return "<p class=\"dim\">The file is empty.</p>";
        var width = rows.Max(r => r.Count);
        var sb = new StringBuilder("<div class=\"tablewrap\"><table class=\"csv\"><thead><tr><th class=\"n\"></th>");
        for (var c = 0; c < width; c++) sb.Append("<th>").Append(WebUtility.HtmlEncode(c < rows[0].Count ? rows[0][c] : "")).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        for (var r = 1; r < rows.Count; r++)
        {
            sb.Append("<tr><td class=\"n\">").Append(r).Append("</td>");
            for (var c = 0; c < width; c++) sb.Append("<td>").Append(WebUtility.HtmlEncode(c < rows[r].Count ? rows[r][c] : "")).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        if (truncated) sb.Append("<p class=\"dim\">Only the first rows are shown. Open the file in its default app to see all of it.</p>");
        return sb.ToString();
    }

    private const int MaxArchiveEntries = 1000;

    private string ArchiveListing(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(Resolve(path));
            var entries = zip.Entries.ToList();
            var files = entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            var sb = new StringBuilder($"<p class=\"dim\">{files.Count} file{(files.Count == 1 ? "" : "s")}, {FormatSize(files.Sum(e => e.Length))} uncompressed.</p>");
            sb.Append("<div class=\"tablewrap\"><table class=\"csv\"><thead><tr><th>Name</th><th>Size</th><th>Modified</th></tr></thead><tbody>");
            foreach (var e in entries.Take(MaxArchiveEntries))
            {
                var dir = e.FullName.EndsWith('/');
                sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(e.FullName)).Append("</td><td class=\"r\">").Append(dir ? "" : FormatSize(e.Length))
                  .Append("</td><td>").Append(e.LastWriteTime.Year > 1980 ? e.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : "").Append("</td></tr>");
            }
            sb.Append("</tbody></table></div>");
            if (entries.Count > MaxArchiveEntries) sb.Append($"<p class=\"dim\">The first {MaxArchiveEntries:N0} of {entries.Count:N0} entries are listed.</p>");
            return sb.ToString();
        }
        catch (Exception ex) { return $"<p class=\"dim\">Could not read the archive: {WebUtility.HtmlEncode(ex.Message)}</p>"; }
    }

    public string RenderFile(string path, string? text = null)
    {
        var name = System.IO.Path.GetFileName(path);
        var src = Uri.EscapeDataString(name);
        var detail = "";
        long length = 0;
        try
        {
            var info = new FileInfo(Resolve(path));
            length = info.Length;
            detail = $"{FormatSize(info.Length)} · {info.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch { }
        var limit = _previewLimit();
        var big = limit > 0 && length > limit;
        string Gate(string what, string markup) => big ? ClickToLoad(what, length, markup) : markup;
        var bar = FileBar(name, detail);
        switch (Kind(path))
        {
            case WikiKind.Image: return bar + Gate("picture", $"<div class=\"imgview\" data-src=\"{src}\"></div>");
            case WikiKind.Pdf: return bar + Gate("document", $"<iframe class=\"doc\" src=\"{src}\" title=\"{WebUtility.HtmlEncode(name)}\"></iframe>");
            // preload="none": nothing is fetched until play is pressed, however small the file.
            case WikiKind.Audio: return bar + Gate("recording", $"<audio class=\"media\" controls preload=\"none\" src=\"{src}\"></audio>");
            case WikiKind.Video: return bar + Gate("video", $"<video class=\"media\" controls preload=\"none\" src=\"{src}\"></video>");
            case WikiKind.Archive: return bar + ArchiveListing(path);
            case WikiKind.Markdown: return Render(text ?? File.ReadAllText(Resolve(path)));
            case WikiKind.Text:
                try
                {
                    if (text is null && new FileInfo(Resolve(path)).Length > MaxEditableBytes) return bar + "<p class=\"dim\">This file is too large to preview here.</p>";
                    var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
                    if (ext is ".csv" or ".tsv") return bar + CsvTable(text ?? File.ReadAllText(Resolve(path)), ext == ".tsv" ? '\t' : ',');
                    var sb = new StringBuilder(bar).Append("<pre class=\"file\"><code>");
                    foreach (var line in Syntax.Highlight(text ?? File.ReadAllText(Resolve(path)), Syntax.LanguageOf(name)))
                    {
                        sb.Append("<span class=\"l\">");
                        foreach (var s in line)
                        {
                            var cls = Syntax.CssClass(s.Kind);
                            if (cls.Length == 0) sb.Append(WebUtility.HtmlEncode(s.Text));
                            else sb.Append("<span class=\"").Append(cls).Append("\">").Append(WebUtility.HtmlEncode(s.Text)).Append("</span>");
                        }
                        sb.Append("</span>\n");
                    }
                    return sb.Append("</code></pre>").ToString();
                }
                catch (Exception ex) { return bar + $"<p class=\"dim\">{WebUtility.HtmlEncode(ex.Message)}</p>"; }
            default: return bar + "<p class=\"dim\">There is no built-in preview for this kind of file. Use “Open in default app”.</p>";
        }
    }

    public IResult ServeRaw(string path)
    {
        string full;
        try { full = Resolve(Uri.UnescapeDataString(path)); } catch { return Results.NotFound(); }
        // Range requests let audio and video seek.
        return File.Exists(full) ? Results.File(full, Web.WebServer.MimeOf(full), enableRangeProcessing: true) : Results.NotFound();
    }

    /// <summary>The preview page. The app pushes rendered HTML into it with setContent(html, folder, path, scroll).</summary>
    public static string PreviewShellHtml(ThemeDef theme) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><base id="base" href="/wiki/raw/">
        <style id="theme">:root { {{Themes.CssVariables(theme)}} }</style>
        <link rel="stylesheet" href="/wiki/_assets/preview.css">
        </head><body><div id="content"></div>
        <div id="lightbox"><div class="stage"></div></div>
        <div id="menu"></div>
        <script src="/wiki/_assets/preview.js"></script></body></html>
        """;
}
