using System.Text;
using System.Text.RegularExpressions;

namespace Avamux.Core.Repos;

public enum DiffKind { Meta, FileHeader, Hunk, Context, Add, Del, NoNewline }

/// <summary>One displayed line of a diff.</summary>
/// <param name="Text">The line without its leading "+", "-" or space (meta and header lines are kept whole).</param>
/// <param name="Old">Line number in the old file, when the line exists there.</param>
/// <param name="New">Line number in the new file, when the line exists there.</param>
/// <param name="File">Index into <see cref="DiffDoc.Files"/>, or -1.</param>
/// <param name="Hunk">Index of the hunk inside its file, or -1.</param>
/// <param name="Body">Index of the line inside its hunk's body, or -1.</param>
public sealed record DiffLine(DiffKind Kind, string Text, int? Old, int? New, int File, int Hunk, int Body);

public sealed class DiffHunk
{
    public int OldStart, OldCount, NewStart, NewCount;
    public string Section = "";
    /// <summary>Body lines exactly as git printed them, prefix included.</summary>
    public List<string> Body { get; } = [];
}

public sealed class DiffFile
{
    public List<string> Header { get; } = [];
    public string? OldPath, NewPath;
    public bool IsNew, IsDeleted, IsBinary;
    public List<DiffHunk> Hunks { get; } = [];
    public string Path => NewPath is null or "/dev/null" ? OldPath ?? "" : NewPath;
}

/// <summary>A parsed unified diff (the output of git diff or git show).</summary>
public sealed partial class DiffDoc
{
    public List<DiffLine> Lines { get; } = [];
    public List<DiffFile> Files { get; } = [];

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$")]
    private static partial Regex HunkHeader();

    private static string? StripPrefix(string path) =>
        path == "/dev/null" ? path : path.Length > 2 && path[1] == '/' && path[0] is 'a' or 'b' or 'c' or 'i' or 'w' or 'o' ? path[2..] : path;

    public static DiffDoc Parse(string text)
    {
        var doc = new DiffDoc();
        DiffFile? file = null;
        DiffHunk? hunk = null;
        int oldNo = 0, newNo = 0, oldLeft = 0, newLeft = 0;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) || (file is null && line.StartsWith("--- ", StringComparison.Ordinal)))
            {
                file = new DiffFile();
                doc.Files.Add(file);
                hunk = null;
            }
            if (file is null) { doc.Lines.Add(new DiffLine(DiffKind.Meta, line, null, null, -1, -1, -1)); continue; }
            var fi = doc.Files.Count - 1;

            // Inside a hunk, the header's counts say how many lines belong to it; that is what tells a removed
            // line that starts with "-- " from the next file's "--- a/…" header.
            if (hunk is not null && (oldLeft > 0 || newLeft > 0) && line.Length > 0 && line[0] is ' ' or '+' or '-')
            {
                var body = hunk.Body.Count;
                hunk.Body.Add(line);
                switch (line[0])
                {
                    case '+': doc.Lines.Add(new DiffLine(DiffKind.Add, line[1..], null, newNo++, fi, file.Hunks.Count - 1, body)); newLeft--; break;
                    case '-': doc.Lines.Add(new DiffLine(DiffKind.Del, line[1..], oldNo++, null, fi, file.Hunks.Count - 1, body)); oldLeft--; break;
                    default: doc.Lines.Add(new DiffLine(DiffKind.Context, line[1..], oldNo++, newNo++, fi, file.Hunks.Count - 1, body)); oldLeft--; newLeft--; break;
                }
                continue;
            }
            if (hunk is not null && line.Length == 0 && oldLeft > 0 && newLeft > 0)
            {
                // Some tools strip the single space of an empty context line.
                hunk.Body.Add(" ");
                doc.Lines.Add(new DiffLine(DiffKind.Context, "", oldNo++, newNo++, fi, file.Hunks.Count - 1, hunk.Body.Count - 1));
                oldLeft--; newLeft--;
                continue;
            }
            if (hunk is not null && line.StartsWith('\\'))
            {
                hunk.Body.Add(line);
                doc.Lines.Add(new DiffLine(DiffKind.NoNewline, line, null, null, fi, file.Hunks.Count - 1, hunk.Body.Count - 1));
                continue;
            }
            var m = HunkHeader().Match(line);
            if (m.Success)
            {
                hunk = new DiffHunk
                {
                    OldStart = int.Parse(m.Groups[1].Value), OldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1,
                    NewStart = int.Parse(m.Groups[3].Value), NewCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1,
                    Section = m.Groups[5].Value,
                };
                file.Hunks.Add(hunk);
                (oldNo, newNo, oldLeft, newLeft) = (hunk.OldStart, hunk.NewStart, hunk.OldCount, hunk.NewCount);
                doc.Lines.Add(new DiffLine(DiffKind.Hunk, line, null, null, fi, file.Hunks.Count - 1, -1));
                continue;
            }
            // File header.
            hunk = null;
            file.Header.Add(line);
            if (line.StartsWith("--- ", StringComparison.Ordinal)) file.OldPath = StripPrefix(line[4..].Split('\t')[0]);
            else if (line.StartsWith("+++ ", StringComparison.Ordinal)) file.NewPath = StripPrefix(line[4..].Split('\t')[0]);
            else if (line.StartsWith("new file mode", StringComparison.Ordinal)) file.IsNew = true;
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) file.IsDeleted = true;
            else if (line.StartsWith("Binary files", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) file.IsBinary = true;
            doc.Lines.Add(new DiffLine(DiffKind.FileHeader, line, null, null, fi, -1, -1));
        }
        return doc;
    }

    /// <summary>True when a hunk of this file can be applied on its own.</summary>
    public bool CanPatchHunk(DiffLine line) => line.File >= 0 && line.Hunk >= 0 && !Files[line.File].IsBinary;

    /// <summary>True when one changed line of this file can be applied on its own.</summary>
    public bool CanPatchLine(DiffLine line) =>
        CanPatchHunk(line) && line.Kind is DiffKind.Add or DiffKind.Del && !Files[line.File].IsNew && !Files[line.File].IsDeleted;

    private static void AppendHeader(StringBuilder sb, DiffFile file)
    {
        foreach (var h in file.Header) sb.Append(h).Append('\n');
    }

    private static string Range(int position, int count) => $"{(count > 0 ? position : position - 1)},{count}";

    /// <summary>
    /// A patch containing one hunk. <paramref name="reverse"/> says the patch will be applied with --reverse
    /// (unstaging, or discarding from the working tree), which decides where git should look for it.
    /// </summary>
    public string HunkPatch(int fileIndex, int hunkIndex, bool reverse) => Build(fileIndex, hunkIndex, null, reverse);

    /// <summary>
    /// A patch that changes only one line of a hunk. The other changes of the hunk are folded into context:
    /// going forward, the other additions do not exist yet (dropped) and the other removals are still there
    /// (context); in reverse it is the other way around.
    /// </summary>
    public string LinePatch(int fileIndex, int hunkIndex, int bodyIndex, bool reverse) => Build(fileIndex, hunkIndex, bodyIndex, reverse);

    private string Build(int fileIndex, int hunkIndex, int? only, bool reverse)
    {
        var file = Files[fileIndex];
        var hunk = file.Hunks[hunkIndex];
        var body = new List<string>();
        var kept = true; // whether the previous line survived, for "\ No newline at end of file"
        for (var i = 0; i < hunk.Body.Count; i++)
        {
            var line = hunk.Body[i];
            var sign = line[0];
            if (sign == '\\') { if (kept) body.Add(line); continue; }
            kept = true;
            if (only is null || i == only || sign == ' ') { body.Add(line); continue; }
            var vanishes = reverse ? sign == '-' : sign == '+';
            if (vanishes) kept = false; else body.Add(" " + line[1..]);
        }
        var oldCount = body.Count(l => l[0] is ' ' or '-');
        var newCount = body.Count(l => l[0] is ' ' or '+');
        // Where the hunk starts in the file git will patch: the old file going forward, the new file in reverse.
        var position = reverse ? (hunk.NewCount > 0 ? hunk.NewStart : hunk.NewStart + 1) : (hunk.OldCount > 0 ? hunk.OldStart : hunk.OldStart + 1);
        var sb = new StringBuilder();
        AppendHeader(sb, file);
        sb.Append("@@ -").Append(Range(position, oldCount)).Append(" +").Append(Range(position, newCount)).Append(" @@");
        if (hunk.Section.Length > 0) sb.Append(' ').Append(hunk.Section);
        sb.Append('\n');
        foreach (var l in body) sb.Append(l).Append('\n');
        return sb.ToString();
    }
}
