using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Avamux.Core.SystemInfo;

/// <summary>
/// The disk scans of this avamux: running ones keep going whatever the windows do, and every scan is saved (on
/// pausing, stopping, finishing, every few minutes while it runs, and when avamux closes) so a scan survives a
/// restart and can be continued from where it stopped. Saved trees are loaded only when something looks at them.
/// </summary>
public sealed class DiskScanService : IDisposable
{
    private const string Magic = "AVDSCAN3";
    private readonly string _dir;
    private readonly List<DiskScan> _scans = [];
    private readonly Dictionary<DiskScan, int> _users = new();
    private readonly Timer _checkpoint;
    private readonly object _io = new();
    private volatile bool _closing;

    public DiskScanService(AppPaths paths)
    {
        _dir = Path.Combine(paths.DataDir, "disk-scans");
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var file in Directory.GetFiles(_dir, "*.scan"))
            {
                try { if (ReadHeader(file) is { } scan) Add(scan); }
                catch (Exception ex) { Logging.Log.Warn("disk", $"Skipping {Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Logging.Log.Warn("disk", "Could not read saved disk scans: " + ex.Message); }
        _checkpoint = new Timer(_ => { foreach (var s in Scans.Where(s => s.State == DiskScanState.Running)) Save(s); }, null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));
    }

    /// <summary>Raised (on any thread) when a scan is added, removed, or changes state.</summary>
    public event Action<DiskScan>? Changed;

    public IReadOnlyList<DiskScan> Scans { get { lock (_scans) return _scans.ToList(); } }

    public DiskScan? Find(string id) { lock (_scans) return _scans.FirstOrDefault(s => s.Id == id); }

    private void Add(DiskScan scan)
    {
        lock (_scans) _scans.Add(scan);
        scan.StateChanged += s =>
        {
            if (s.State is DiskScanState.Paused or DiskScanState.Stopped or DiskScanState.Done && !_closing)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Save(s);
                    // Finished with no window looking at it: the tree can wait on disk.
                    bool watched;
                    lock (_users) watched = _users.ContainsKey(s);
                    if (!watched && !s.Busy) Unload(s);
                });
            Changed?.Invoke(s);
        };
    }

    public static string IdFor(string target) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target)))[..16].ToLowerInvariant();

    /// <summary>The scan of a volume, made (not started) if there is none yet.</summary>
    public DiskScan ForVolume(Volume v)
    {
        var id = IdFor(v.Key + "|" + string.Join("|", v.ScanRoots));
        if (Find(id) is { } found) return found;
        var scan = new DiskScan(id, v.Name, v.ScanRoots, v.Key, v.Used);
        Add(scan);
        Changed?.Invoke(scan);
        return scan;
    }

    /// <summary>The scan of a folder, made (not started) if there is none yet.</summary>
    public DiskScan ForFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (folder.Length > 1) folder = folder.TrimEnd(Path.DirectorySeparatorChar);
        var id = IdFor("folder|" + folder);
        if (Find(id) is { } found) return found;
        var scan = new DiskScan(id, folder, [folder], null, null);
        Add(scan);
        Changed?.Invoke(scan);
        return scan;
    }

    /// <summary>
    /// Says a window is looking at the scan: its tree is loaded (from disk, if need be, which can take a few seconds:
    /// call it off the UI thread). Pair each call with <see cref="Release"/>.
    /// </summary>
    public void Load(DiskScan scan)
    {
        lock (_users) _users[scan] = _users.GetValueOrDefault(scan) + 1;
        EnsureLoaded(scan);
    }

    /// <summary>Loads the scan's tree from disk if it is not in memory (to resume it, say).</summary>
    public void EnsureLoaded(DiskScan scan)
    {
        lock (scan)
        {
            if (scan.Root is not null) return;
            var file = FileFor(scan);
            if (!File.Exists(file)) return;
            try { ReadTree(file, scan); }
            catch (Exception ex) { Logging.Log.Warn("disk", $"Could not load the scan of {scan.Title}: {ex.Message}"); }
        }
    }

    /// <summary>Nobody is looking at the scan any more: a scan that is not running lets go of its tree (it is on disk).</summary>
    public void Release(DiskScan scan)
    {
        lock (_users)
        {
            var n = _users.GetValueOrDefault(scan) - 1;
            if (n > 0) { _users[scan] = n; return; }
            _users.Remove(scan);
        }
        if (!scan.Busy) Unload(scan);
    }

    private void Unload(DiskScan scan)
    {
        if (scan.Root is null || !File.Exists(FileFor(scan))) return;
        lock (scan)
        {
            scan.SavedTotals = (scan.Size, scan.Files, scan.Folders);
            scan.Root = null;
            scan.ForgetHardLinks();
        }
    }

    /// <summary>Forgets a scan and deletes its file.</summary>
    public void Delete(DiskScan scan)
    {
        scan.Stop(wait: true);
        lock (_scans) _scans.Remove(scan);
        lock (_io) try { File.Delete(FileFor(scan)); } catch { /* already gone */ }
        Changed?.Invoke(scan);
    }

    private string FileFor(DiskScan scan) => Path.Combine(_dir, scan.Id + ".scan");

    public void Save(DiskScan scan)
    {
        if (scan.Root is not { } root) return;
        lock (_io)
        {
            var file = FileFor(scan);
            var tmp = file + ".tmp";
            try
            {
                using (var fs = File.Create(tmp))
                {
                    using (var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true)) WriteHeader(w, scan);
                    using var z = new GZipStream(fs, CompressionLevel.Fastest);
                    using var zw = new BinaryWriter(z, Encoding.UTF8);
                    var exts = scan.Extensions;
                    zw.Write(exts.Count);
                    foreach (var e in exts) zw.Write(e);
                    WriteNode(zw, root, scan.HardLinkOwners());
                }
                File.Move(tmp, file, overwrite: true);
            }
            catch (Exception ex)
            {
                Logging.Log.Warn("disk", $"Could not save the scan of {scan.Title}: {ex.Message}");
                try { File.Delete(tmp); } catch { /* nothing to tidy */ }
            }
        }
    }

    private static void WriteHeader(BinaryWriter w, DiskScan s)
    {
        w.Write(Magic);
        w.Write(s.Id);
        w.Write(s.Title);
        w.Write(s.Roots.Count);
        foreach (var r in s.Roots) w.Write(r);
        w.Write(s.VolumeKey ?? "");
        w.Write(s.VolumeUsed ?? -1);
        w.Write(s.Allocated);
        w.Write((int)s.State);
        w.Write(s.Started?.Ticks ?? 0);
        w.Write(s.Finished?.Ticks ?? 0);
        w.Write(s.Elapsed.Ticks);
        w.Write(Interlocked.Read(ref s.Errors));
        w.Write(s.Size);
        w.Write(s.Files);
        w.Write(s.Folders);
    }

    private static DiskScan? ReadHeader(string file)
    {
        using var fs = File.OpenRead(file);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        return ReadHeader(r);
    }

    private static DiskScan? ReadHeader(BinaryReader r)
    {
        if (r.ReadString() != Magic) return null;
        var id = r.ReadString();
        var title = r.ReadString();
        var roots = Enumerable.Range(0, r.ReadInt32()).Select(_ => r.ReadString()).ToList();
        var volume = r.ReadString();
        var used = r.ReadInt64();
        var scan = new DiskScan(id, title, roots, volume.Length == 0 ? null : volume, used < 0 ? null : used) { Allocated = r.ReadBoolean() };
        var state = (DiskScanState)r.ReadInt32();
        var started = r.ReadInt64();
        var finished = r.ReadInt64();
        var elapsed = r.ReadInt64();
        scan.Errors = r.ReadInt64();
        scan.Started = started == 0 ? null : new DateTime(started);
        scan.Finished = finished == 0 ? null : new DateTime(finished);
        scan.SavedTotals = (r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
        scan.Restore(state, TimeSpan.FromTicks(elapsed));
        return scan;
    }

    private static void ReadTree(string file, DiskScan scan)
    {
        using var fs = File.OpenRead(file);
        using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        if (ReadHeader(r) is null) return;
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var zr = new BinaryReader(new BufferedStream(z, 1 << 16), Encoding.UTF8);
        var count = zr.ReadInt32();
        scan.SetExtensions(Enumerable.Range(0, count).Select(_ => zr.ReadString()).ToList());
        scan.ForgetHardLinks();
        var root = ReadNode(zr, null, scan);
        root.Total();
        scan.Root = root;
    }

    // Each node: kind, state, name, modified; a file adds its size, extension and the inode it counted (if a hard
    // link); a read folder adds its children.
    private static void WriteNode(BinaryWriter w, DiskNode n, Dictionary<DiskNode, (ulong Dev, ulong Ino)> links)
    {
        var state = n.State;
        w.Write((byte)n.Kind);
        w.Write((byte)state);
        w.Write(n.Name);
        w.Write(n.Modified);
        if (n.IsFolder)
        {
            var children = state == DiskNodeState.Done ? n.Children : [];
            w.Write(children.Length);
            foreach (var c in children) WriteNode(w, c, links);
        }
        else if (n.Kind != DiskNodeKind.Mount)
        {
            w.Write(n.Size);
            w.Write(n.Ext);
            var linked = links.TryGetValue(n, out var key);
            w.Write(linked);
            if (linked) { w.Write(key.Dev); w.Write(key.Ino); }
        }
    }

    private static DiskNode ReadNode(BinaryReader r, DiskNode? parent, DiskScan scan)
    {
        var kind = (DiskNodeKind)r.ReadByte();
        var state = (DiskNodeState)r.ReadByte();
        var node = new DiskNode(r.ReadString(), kind, parent) { Modified = r.ReadInt64() };
        if (node.IsFolder)
        {
            var children = new DiskNode[r.ReadInt32()];
            for (var i = 0; i < children.Length; i++) children[i] = ReadNode(r, node, scan);
            node.Children = children;
        }
        else if (kind != DiskNodeKind.Mount)
        {
            node.Size = r.ReadInt64();
            node.Ext = r.ReadInt16();
            if (r.ReadBoolean()) scan.AddHardLink((r.ReadUInt64(), r.ReadUInt64()), node);
        }
        node.State = state;
        return node;
    }

    /// <summary>Stops every running scan and saves it, to be resumed next time.</summary>
    public void Dispose()
    {
        _closing = true;
        _checkpoint.Dispose();
        foreach (var s in Scans.Where(s => s.Busy))
        {
            s.Stop(wait: true);
            Save(s);
        }
    }
}
