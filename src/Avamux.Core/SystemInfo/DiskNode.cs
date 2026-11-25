namespace Avamux.Core.SystemInfo;

public enum DiskNodeKind : byte
{
    File = 0,
    Folder = 1,
    /// <summary>A symbolic link: counted at its own (tiny) size, never followed.</summary>
    Link = 2,
    /// <summary>Another file system mounted here: left out of the scan.</summary>
    Mount = 3,
    /// <summary>The top of a scan that covers several folders (the subvolumes of one btrfs disk, say).</summary>
    Group = 4,
}

public enum DiskNodeState : byte
{
    /// <summary>A folder not read yet.</summary>
    Pending = 0,
    Done = 1,
    /// <summary>A folder that could not be read (no permission, gone).</summary>
    Denied = 2,
}

/// <summary>
/// A file or folder in a disk scan. Folders carry the totals of everything below them, which grow while the scan
/// runs (scanner threads add to them; readers may see a total that is a moment behind). A folder's children are
/// published all at once, as a finished array, when it has been read.
/// </summary>
public sealed class DiskNode(string name, DiskNodeKind kind, DiskNode? parent)
{
    public readonly string Name = name;
    public readonly DiskNodeKind Kind = kind;
    public DiskNode? Parent = parent;
    /// <summary>Bytes taken on disk by this and everything below it.</summary>
    public long Size;
    /// <summary>Files below (or 1 for a file).</summary>
    public long Files;
    /// <summary>Folders below, not counting this one.</summary>
    public long Folders;
    /// <summary>Last modified, UTC ticks.</summary>
    public long Modified;
    /// <summary>
    /// When it was last modified, in local time, or null when no real time was kept: unknown, or a time on the first
    /// day of 1970, which image-based systems (ostree, Nix) give every file on purpose.
    /// </summary>
    public DateTime? ModifiedTime => Modified > DateTime.UnixEpoch.Ticks + TimeSpan.TicksPerDay
        ? new DateTime(Modified, DateTimeKind.Utc).ToLocalTime() : null;
    /// <summary>Index into the scan's extension table (files only).</summary>
    public short Ext;
    private volatile DiskNode[] _children = [];
    private volatile byte _state = kind is DiskNodeKind.Folder or DiskNodeKind.Group ? (byte)DiskNodeState.Pending : (byte)DiskNodeState.Done;

    public DiskNode[] Children
    {
        get => _children;
        set => _children = value;
    }

    public DiskNodeState State
    {
        get => (DiskNodeState)_state;
        set => _state = (byte)value;
    }

    public bool IsFolder => Kind is DiskNodeKind.Folder or DiskNodeKind.Group;

    /// <summary>The path on disk. The children of a group are named by their full path.</summary>
    public string FullPath
    {
        get
        {
            if (Parent is null || Parent.Kind == DiskNodeKind.Group || Kind == DiskNodeKind.Group) return Name;
            var parts = new List<string>();
            var n = this;
            while (n.Parent is { } p && p.Kind != DiskNodeKind.Group) { parts.Add(n.Name); n = p; }
            parts.Reverse();
            return Path.Combine([n.Name, .. parts]);
        }
    }

    /// <summary>The chain from the top of the scan down to this node.</summary>
    public List<DiskNode> Ancestry()
    {
        var list = new List<DiskNode>();
        for (var n = this; n is not null; n = n.Parent) list.Add(n);
        list.Reverse();
        return list;
    }

    public bool IsUnder(DiskNode other)
    {
        for (var n = this; n is not null; n = n.Parent) if (ReferenceEquals(n, other)) return true;
        return false;
    }

    /// <summary>Adds to this node's totals and every folder above it.</summary>
    public void AddUp(long size, long files, long folders)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            if (size != 0) Interlocked.Add(ref n.Size, size);
            if (files != 0) Interlocked.Add(ref n.Files, files);
            if (folders != 0) Interlocked.Add(ref n.Folders, folders);
        }
    }

    /// <summary>Recomputes the totals from the leaves up (after loading a saved scan).</summary>
    public void Total()
    {
        if (!IsFolder) { Files = Kind == DiskNodeKind.Mount ? 0 : 1; return; }
        long size = 0, files = 0, folders = 0;
        foreach (var c in Children)
        {
            c.Total();
            size += c.Size;
            files += c.Files;
            folders += c.Folders + (c.IsFolder ? 1 : 0);
        }
        Size = size; Files = files; Folders = folders;
    }

    public override string ToString() => Name;
}
