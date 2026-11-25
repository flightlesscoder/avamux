namespace Avamux.Core.Repos;

/// <summary>A repository's .gitignore: what avamux puts in a new wiki's, and adding patterns to it.</summary>
public static class GitIgnore
{
    /// <summary>The .gitignore a wiki starts with when avamux begins keeping its history.</summary>
    public const string WikiDefault = """
        # Written by avamux when the wiki's history started. Files that match are left out of commits.
        # Add your own patterns below (one per line, see `git help gitignore`).

        # Operating system clutter
        .DS_Store
        ._*
        Thumbs.db
        desktop.ini
        .directory
        .Trash-*/

        # Editors' backup, swap and lock files
        *~
        *.swp
        *.swo
        .#*
        \#*#
        *.tmp
        *.bak
        .~lock.*#

        # Sync tools' scratch files
        .stfolder/
        .stversions/
        *.sync-conflict-*
        .dropbox
        .dropbox.attr
        """;

    public static string FileOf(string repoRoot) => Path.Combine(repoRoot, ".gitignore");

    /// <summary>The pattern that matches exactly one file or folder of the repository (a folder's path ends with '/').</summary>
    public static string PatternFor(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        // Characters git reads as pattern syntax are escaped, so the name matches only itself.
        var escaped = string.Concat(p.Select(c => c is '*' or '?' or '[' or ']' or '!' or '#' or '\\' ? "\\" + c : c.ToString()));
        if (escaped.EndsWith(' ')) escaped = escaped[..^1] + "\\ ";
        return "/" + escaped;
    }

    /// <summary>Ways to ignore a changed file: the file itself, every file with its extension, its folder.</summary>
    public static List<(string Label, string Pattern)> Suggestions(string path)
    {
        var list = new List<(string, string)>();
        var p = path.Replace('\\', '/').TrimStart('/');
        var name = p.TrimEnd('/');
        list.Add((p.EndsWith('/') ? $"This Folder ({name}/)" : $"This File ({Path.GetFileName(name)})", PatternFor(p)));
        var ext = Path.GetExtension(name);
        if (!p.EndsWith('/') && ext.Length > 1 && !ext.Contains(' ')) list.Add(($"All {ext} Files", "*" + ext));
        var folder = name.Contains('/') ? name[..name.LastIndexOf('/')] : null;
        if (folder is not null) list.Add(($"The Folder {folder}/", PatternFor(folder + "/")));
        return list;
    }

    /// <summary>Appends a pattern to the repository's .gitignore (made if missing). False when it was there already.</summary>
    public static bool Add(string repoRoot, string pattern)
    {
        var file = FileOf(repoRoot);
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        if (text.Replace("\r\n", "\n").Split('\n').Any(l => l.Trim() == pattern.Trim())) return false;
        var prefix = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        File.AppendAllText(file, prefix + pattern.Trim() + "\n");
        return true;
    }

    /// <summary>Writes <paramref name="content"/> as the .gitignore when the repository has none. True when written.</summary>
    public static bool WriteDefault(string repoRoot, string content)
    {
        var file = FileOf(repoRoot);
        if (File.Exists(file)) return false;
        File.WriteAllText(file, content.Replace("\r\n", "\n").TrimEnd('\n') + "\n");
        return true;
    }

    /// <summary>File types that are fine to commit to a wiki even though they are not text: pictures.</summary>
    public static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".avif", ".bmp", ".ico" };
}

/// <summary>A file about to be committed that is big, or not text.</summary>
public sealed record HeavyFile(string Path, long Size, bool Binary)
{
    public string Why => (Size, Binary) switch
    {
        _ when Binary && Size >= 1024 * 1024 => $"{Format(Size)}, binary",
        _ when Binary => "binary",
        _ => Format(Size),
    };

    public static string Format(long bytes) =>
        bytes < 1024 ? bytes + " B" : bytes < 1048576 ? $"{bytes / 1024.0:0.#} KB" : bytes < 1073741824 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1073741824.0:0.##} GB";
}
