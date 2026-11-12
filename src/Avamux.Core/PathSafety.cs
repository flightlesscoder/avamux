namespace Avamux.Core;

public static class PathSafety
{
    /// <summary>Combines and guarantees the result stays inside <paramref name="root"/>.</summary>
    public static string Combine(string root, string? relative)
    {
        var rootFull = Path.GetFullPath(root);
        var rel = (relative ?? "").Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(rootFull, rel));
        if (full != rootFull && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Path '{relative}' is outside the allowed folder.");
        return full;
    }

    public static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');
}
