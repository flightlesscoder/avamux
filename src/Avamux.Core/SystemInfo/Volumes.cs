namespace Avamux.Core.SystemInfo;

/// <summary>One mount of a file system: where it is mounted and which part of the file system it shows.</summary>
public sealed record MountEntry(string Device, string MountPoint, string FsType, string Source, string Root);

/// <summary>
/// A disk (or partition, or network share) with its space, and every place it is mounted. A btrfs partition with
/// subvolumes at /, /var and /home is one volume with three <see cref="ScanRoots"/>.
/// </summary>
public sealed record Volume(string Key, string Name, string FsType, string MountPoint, IReadOnlyList<string> MountPoints, IReadOnlyList<string> ScanRoots,
    long Total, long Free, long Available, bool Removable, bool Network)
{
    public long Used => Math.Max(0, Total - Free);
    public double UsedFraction => Total <= 0 ? 0 : Used / (double)Total;
}

/// <summary>Finds the mounted volumes, best effort, on Linux, macOS and Windows.</summary>
public static class Volumes
{
    private static readonly HashSet<string> Pseudo = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "securityfs", "cgroup", "cgroup2", "pstore", "efivarfs", "bpf", "configfs", "debugfs",
        "tracefs", "hugetlbfs", "mqueue", "fusectl", "selinuxfs", "autofs", "rpc_pipefs", "binfmt_misc", "nsfs", "ramfs", "overlay", "squashfs",
        "fuse.portal", "fuse.gvfsd-fuse", "fuse.snapfuse", "nfsd", "devfs", "composefs", "erofs", "tracefs", "fuse.lxcfs", "fuse.xdg-document-portal",
    };

    private static readonly HashSet<string> NetworkTypes = new(StringComparer.Ordinal) { "nfs", "nfs4", "cifs", "smb3", "smbfs", "fuse.sshfs", "afs", "9p", "davfs", "fuse.rclone" };

    /// <summary>Mounts that did not answer in time (a hung network share): left alone for a while.</summary>
    private static readonly Dictionary<string, DateTime> Stuck = new();

    /// <summary>The lines of /proc/self/mountinfo, parsed. Octal escapes (\040 for a space) are undone.</summary>
    public static List<MountEntry> ParseMountInfo(string text)
    {
        var list = new List<MountEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            var dash = Array.IndexOf(parts, "-");
            if (parts.Length < 10 || dash < 6 || dash + 2 >= parts.Length) continue;
            list.Add(new MountEntry(parts[2], Unescape(parts[4]), parts[dash + 1], Unescape(parts[dash + 2]), Unescape(parts[3])));
        }
        return list;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] is >= '0' and <= '7')
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static bool IsPseudo(MountEntry m) =>
        Pseudo.Contains(m.FsType) || m.FsType.StartsWith("cgroup", StringComparison.Ordinal) || m.MountPoint.StartsWith("/proc", StringComparison.Ordinal)
        || m.MountPoint.StartsWith("/sys/", StringComparison.Ordinal) || m.MountPoint == "/sys" || m.MountPoint.StartsWith("/run/credentials", StringComparison.Ordinal)
        || m.MountPoint.StartsWith("/snap/", StringComparison.Ordinal) || m.MountPoint.StartsWith("/var/lib/docker/", StringComparison.Ordinal)
        || m.MountPoint.StartsWith("/var/lib/containers/", StringComparison.Ordinal);

    /// <summary>
    /// Groups real mounts by the file system they show (the device number), keeping the mounts whose part of the
    /// file system is not already inside another's: those are what a scan has to walk to see the whole volume once.
    /// </summary>
    public static List<(string Device, List<MountEntry> All, List<MountEntry> Roots)> Group(IEnumerable<MountEntry> mounts)
    {
        var result = new List<(string, List<MountEntry>, List<MountEntry>)>();
        foreach (var g in mounts.Where(m => !IsPseudo(m)).GroupBy(m => m.Device))
        {
            var all = g.OrderBy(m => m.MountPoint.Length).ThenBy(m => m.MountPoint, StringComparer.Ordinal).ToList();
            var roots = new List<MountEntry>();
            foreach (var m in all.OrderBy(m => m.Root.Length).ThenBy(m => m.MountPoint.Length))
                if (!roots.Any(r => Inside(m.Root, r.Root)) && !roots.Any(r => r.Root == m.Root)) roots.Add(m);
            roots.Sort((a, b) => string.CompareOrdinal(a.MountPoint, b.MountPoint));
            result.Add((g.Key, all, roots));
        }
        return result;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or below it (as '/'-separated paths).</summary>
    public static bool Inside(string path, string folder) =>
        folder == "/" || path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    public static List<MountEntry> LinuxMounts()
    {
        try { return ParseMountInfo(File.ReadAllText("/proc/self/mountinfo")); }
        catch { return []; }
    }

    /// <summary>The volumes, largest first, each measured with a short time limit so a dead network share cannot hang the caller.</summary>
    public static List<Volume> List()
    {
        try { return OperatingSystem.IsLinux() ? ListLinux() : ListOther(); }
        catch (Exception ex) { Logging.Log.Warn("system", "Could not list volumes: " + ex.Message); return []; }
    }

    private static List<Volume> ListLinux()
    {
        var labels = Labels();
        var list = new List<Volume>();
        foreach (var (device, all, roots) in Group(LinuxMounts()))
        {
            var main = all.FirstOrDefault(m => m.MountPoint == "/") ?? roots.FirstOrDefault(m => m.Root == "/") ?? all[0];
            if (Space(main.MountPoint) is not { } space) continue;
            if (space.Total <= 0) continue;
            var network = NetworkTypes.Contains(main.FsType) || main.Source.StartsWith("//", StringComparison.Ordinal) || main.Source.Contains(":/", StringComparison.Ordinal);
            var name = labels.GetValueOrDefault(main.Source) is { } label ? $"{label} ({main.Source})" : main.Source;
            var removable = main.MountPoint.StartsWith("/run/media/", StringComparison.Ordinal) || main.MountPoint.StartsWith("/media/", StringComparison.Ordinal);
            var points = all.Select(m => m.MountPoint).OrderBy(p => p != "/").ThenBy(p => p, StringComparer.Ordinal).ToList();
            list.Add(new Volume("dev:" + device, name, main.FsType, main.MountPoint, points, roots.Select(m => m.MountPoint).ToList(),
                space.Total, space.Free, space.Available, removable, network));
        }
        return list.OrderByDescending(v => v.MountPoint == "/").ThenByDescending(v => v.Total).ToList();
    }

    /// <summary>Partition labels by device path, from /dev/disk/by-label.</summary>
    private static Dictionary<string, string> Labels()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var link in Directory.EnumerateFileSystemEntries("/dev/disk/by-label"))
                if (new FileInfo(link).ResolveLinkTarget(true) is { } target)
                    map.TryAdd(target.FullName, Uri.UnescapeDataString(Path.GetFileName(link).Replace("\\x20", " ")));
        }
        catch { /* no labels */ }
        return map;
    }

    private static List<Volume> ListOther()
    {
        var list = new List<Volume>();
        foreach (var d in DriveInfo.GetDrives())
        {
            var root = d.RootDirectory.FullName;
            if (OperatingSystem.IsMacOS() && !(root == "/" || root.StartsWith("/Volumes/", StringComparison.Ordinal))) continue;
            if (d.DriveType is DriveType.Ram or DriveType.NoRootDirectory or DriveType.Unknown && !OperatingSystem.IsMacOS()) continue;
            if (Space(root) is not { } space || space.Total <= 0) continue;
            string fs;
            try { fs = d.DriveFormat; } catch { fs = ""; }
            string label;
            try { label = d.VolumeLabel; } catch { label = ""; }
            var name = string.IsNullOrWhiteSpace(label) ? root : $"{label} ({root.TrimEnd('\\')})";
            list.Add(new Volume("root:" + root, name, fs, root, [root], [root], space.Total, space.Free, space.Available,
                d.DriveType == DriveType.Removable, d.DriveType == DriveType.Network));
        }
        return list.OrderByDescending(v => v.MountPoint is "/" or @"C:\").ThenByDescending(v => v.Total).ToList();
    }

    /// <summary>Size, free and available bytes of the file system at a mount point; null if it does not answer within a second.</summary>
    public static (long Total, long Free, long Available)? Space(string mountPoint)
    {
        lock (Stuck)
            if (Stuck.TryGetValue(mountPoint, out var since) && DateTime.UtcNow - since < TimeSpan.FromMinutes(5)) return null;
        var task = Task.Run(() =>
        {
            var d = new DriveInfo(mountPoint);
            return (d.TotalSize, d.TotalFreeSpace, d.AvailableFreeSpace);
        });
        try
        {
            if (task.Wait(1000)) return task.Result;
            lock (Stuck) Stuck[mountPoint] = DateTime.UtcNow;
        }
        catch { /* not ready, or gone */ }
        return null;
    }
}
