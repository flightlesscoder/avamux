using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace Avamux.Core.SystemInfo;

public enum DiskScanState { New, Running, Paused, Stopped, Done }

/// <summary>
/// A scan of a volume or folder: a tree of everything in it and the size each thing takes on disk. It runs on a
/// few low-priority threads, can be paused and resumed, and stopped part way and continued later (even after a
/// restart, once saved), because every folder not read yet is kept in the tree as pending. It does not cross
/// into other file systems mounted inside, and never follows links.
/// </summary>
public sealed class DiskScan
{
    private readonly ConcurrentStack<DiskNode> _queue = new();
    private readonly ManualResetEventSlim _running = new(true);
    private readonly ConcurrentDictionary<(ulong, ulong), DiskNode> _hardLinks = new();
    private readonly ConcurrentDictionary<string, short> _extIds = new(StringComparer.Ordinal);
    private readonly List<string> _exts = [""];
    private readonly Stopwatch _clock = new();
    private TimeSpan _elapsedBefore;
    private CancellationTokenSource? _stop;
    private Thread[] _threads = [];
    private int _outstanding;
    private int _working;
    private HashSet<string> _skip = new(StringComparer.Ordinal);
    private DiskNode? _current;
    private readonly object _gate = new();

    public DiskScan(string id, string title, IReadOnlyList<string> roots, string? volumeKey, long? volumeUsed)
    {
        Id = id; Title = title; Roots = roots; VolumeKey = volumeKey; VolumeUsed = volumeUsed;
    }

    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<string> Roots { get; }
    public string? VolumeKey { get; }
    /// <summary>Bytes in use on the volume when the scan started: what "done" is measured against.</summary>
    public long? VolumeUsed { get; internal set; }
    /// <summary>Sizes are space allocated on disk (Linux), rather than file lengths.</summary>
    public bool Allocated { get; internal set; } = OperatingSystem.IsLinux() && Native.Works;
    public DiskScanState State { get; private set; } = DiskScanState.New;
    public DateTime? Started { get; internal set; }
    public DateTime? Finished { get; internal set; }
    public long Errors;
    /// <summary>The tree, or null while it is only on disk (see <see cref="DiskScanService.Load"/>).</summary>
    public DiskNode? Root { get; internal set; }
    /// <summary>Totals remembered from the saved file, for showing a scan whose tree is not loaded.</summary>
    public (long Size, long Files, long Folders) SavedTotals { get; internal set; }

    public TimeSpan Elapsed => _elapsedBefore + _clock.Elapsed;
    public bool Busy => State is DiskScanState.Running or DiskScanState.Paused;
    public string? CurrentPath => _current?.FullPath;
    public long Size => Root?.Size ?? SavedTotals.Size;
    public long Files => Root?.Files ?? SavedTotals.Files;
    public long Folders => Root?.Folders ?? SavedTotals.Folders;
    /// <summary>Folders waiting to be read.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>How far through, when the size of the volume is known.</summary>
    public double? Fraction => State == DiskScanState.Done ? 1 : VolumeUsed is > 0 and var used ? Math.Clamp(Size / (double)used, 0, 0.999) : null;

    /// <summary>Raised (on a scanner thread) when the scan starts, pauses, stops or finishes.</summary>
    public event Action<DiskScan>? StateChanged;

    public IReadOnlyList<string> Extensions { get { lock (_exts) return _exts.ToList(); } }

    public string ExtensionName(short id) { lock (_exts) return id >= 0 && id < _exts.Count ? _exts[id] : ""; }

    internal void SetExtensions(IEnumerable<string> names)
    {
        lock (_exts)
        {
            _exts.Clear();
            _extIds.Clear();
            foreach (var n in names) { _extIds.TryAdd(n, (short)_exts.Count); _exts.Add(n); }
            if (_exts.Count == 0) _exts.Add("");
        }
    }

    /// <summary>
    /// Files with several names (hard links), by (device, inode), and the name that was counted: the others count as
    /// nothing. Saved with the scan, so a resumed scan does not count them twice.
    /// </summary>
    internal Dictionary<DiskNode, (ulong, ulong)> HardLinkOwners() => _hardLinks.ToDictionary(kv => kv.Value, kv => kv.Key);

    internal void AddHardLink((ulong, ulong) key, DiskNode owner) => _hardLinks.TryAdd(key, owner);

    /// <summary>Lets go of the hard-link owners (they hold on to the tree).</summary>
    internal void ForgetHardLinks() => _hardLinks.Clear();

    internal void Restore(DiskScanState state, TimeSpan elapsed)
    {
        State = state is DiskScanState.Running or DiskScanState.Paused ? DiskScanState.Stopped : state;
        _elapsedBefore = elapsed;
    }

    private short ExtId(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var ext = dot <= 0 || dot == fileName.Length - 1 || fileName.Length - dot > 12 ? "" : fileName[(dot + 1)..].ToLowerInvariant();
        if (_extIds.TryGetValue(ext, out var id)) return id;
        lock (_exts)
        {
            if (_extIds.TryGetValue(ext, out id)) return id;
            if (_exts.Count >= short.MaxValue) return 0;
            id = (short)_exts.Count;
            _exts.Add(ext);
            _extIds[ext] = id;
            return id;
        }
    }

    // ---- control ----

    /// <summary>Starts again from nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (Busy) return;
            var tops = Roots.Select(r => new DiskNode(r, DiskNodeKind.Folder, null)).ToList();
            if (tops.Count == 1) Root = tops[0];
            else
            {
                Root = new DiskNode(Title, DiskNodeKind.Group, null) { Children = tops.ToArray(), Folders = tops.Count, State = DiskNodeState.Done };
                foreach (var t in tops) t.Parent = Root;
            }
            _hardLinks.Clear();
            SetExtensions([""]);
            Errors = 0;
            _elapsedBefore = TimeSpan.Zero;
            _clock.Reset();
            Started = DateTime.Now;
            Finished = null;
            if (VolumeKey is not null && Volumes.List().FirstOrDefault(v => v.Key == VolumeKey) is { } vol) VolumeUsed = vol.Used;
            Launch();
        }
    }

    /// <summary>Carries on a stopped scan (or one loaded from disk) from the folders it had not read.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (State == DiskScanState.Paused) { SetState(DiskScanState.Running); _running.Set(); _clock.Start(); return; }
            if (Busy || Root is null) return;
            Finished = null;
            Launch();
        }
    }

    /// <summary>Holds the scanner threads where they are; <see cref="Resume"/> lets them go on.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (State != DiskScanState.Running) return;
            _running.Reset();
            _clock.Stop();
            SetState(DiskScanState.Paused);
        }
    }

    /// <summary>Ends the threads, keeping what has been read; the rest stays pending, to resume later.</summary>
    public void Stop(bool wait = false)
    {
        Thread[] threads;
        lock (_gate)
        {
            if (!Busy) return;
            _stop?.Cancel();
            _running.Set();
            threads = _threads;
        }
        if (wait) foreach (var t in threads) t.Join(5000);
    }

    /// <summary>Reads one folder again (after files were deleted, say). Only while the scan is not running.</summary>
    public void Rescan(DiskNode folder)
    {
        lock (_gate)
        {
            if (Busy || Root is null || folder.Kind != DiskNodeKind.Folder) return;
            folder.Parent?.AddUp(-folder.Size, -folder.Files, -folder.Folders);
            // Files counted in there are counted afresh (or by another of their names, found again).
            foreach (var (key, owner) in _hardLinks) if (owner.IsUnder(folder)) _hardLinks.TryRemove(key, out _);
            folder.Children = [];
            folder.Size = folder.Files = folder.Folders = 0;
            folder.State = DiskNodeState.Pending;
            Finished = null;
            Launch();
        }
    }

    private void Launch()
    {
        _queue.Clear();
        _outstanding = 0;
        foreach (var pending in PendingFolders(Root!)) { _queue.Push(pending); _outstanding++; }
        _skip = SkipList(Roots);
        _stop = new CancellationTokenSource();
        _running.Set();
        _clock.Start();
        SetState(DiskScanState.Running);
        if (_outstanding == 0) { Finish(); return; }
        var token = _stop.Token;
        var count = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        _working = count;
        _threads = Enumerable.Range(0, count).Select(i => new Thread(() => Work(token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"disk-scan-{i}" }).ToArray();
        foreach (var t in _threads) t.Start();
    }

    private static IEnumerable<DiskNode> PendingFolders(DiskNode root)
    {
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Kind == DiskNodeKind.Folder && n.State == DiskNodeState.Pending) { yield return n; continue; }
            foreach (var c in n.Children) if (c.IsFolder) stack.Push(c);
        }
    }

    private void SetState(DiskScanState state)
    {
        State = state;
        try { StateChanged?.Invoke(this); }
        catch (Exception ex) { Logging.Log.Error("disk", "Scan state handler failed", ex); }
    }

    private void Finish()
    {
        lock (_gate)
        {
            _clock.Stop();
            _elapsedBefore += _clock.Elapsed;
            _clock.Reset();
            _current = null;
            var stopped = _stop?.IsCancellationRequested == true || Outstanding > 0;
            if (Root is not null) Sort(Root);
            if (!stopped) Finished = DateTime.Now;
            SetState(stopped ? DiskScanState.Stopped : DiskScanState.Done);
        }
    }

    /// <summary>Puts every folder's contents biggest first (they were sorted as read, before their folders had sizes).</summary>
    private static void Sort(DiskNode root)
    {
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            var children = n.Children;
            if (children.Length == 0) continue;
            var sorted = (DiskNode[])children.Clone();
            Array.Sort(sorted, (a, b) => b.Size.CompareTo(a.Size));
            n.Children = sorted;
            foreach (var c in sorted) if (c.IsFolder) stack.Push(c);
        }
    }

    private void Work(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                _running.Wait(token);
                if (!_queue.TryPop(out var dir))
                {
                    if (Volatile.Read(ref _outstanding) == 0) break;
                    Thread.Sleep(10);
                    continue;
                }
                try
                {
                    if (!Read(dir, token)) { _queue.Push(dir); break; }
                    Interlocked.Decrement(ref _outstanding);
                }
                catch (OperationCanceledException) { _queue.Push(dir); break; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref Errors);
                    dir.State = DiskNodeState.Denied;
                    Interlocked.Decrement(ref _outstanding);
                    Logging.Log.Debug("disk", $"{dir.FullPath}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        if (Interlocked.Decrement(ref _working) == 0) Finish();
    }

    /// <summary>Reads one folder. False if stopped part way (the folder stays pending, untouched).</summary>
    private bool Read(DiskNode dir, CancellationToken token)
    {
        _current = dir;
        var path = dir.FullPath;
        var children = new List<DiskNode>();
        long size = 0, files = 0, folders = 0;
        IEnumerable<Entry> entries;
        try { entries = List(path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            dir.State = DiskNodeState.Denied;
            Interlocked.Increment(ref Errors);
            return true;
        }
        var n = 0;
        try
        {
            foreach (var e in entries)
            {
                if (++n % 512 == 0)
                {
                    if (token.IsCancellationRequested) return false;
                    if (!_running.IsSet) { _running.Wait(token); }
                }
                DiskNode node;
                if (e.Kind == DiskNodeKind.Folder)
                {
                    var full = Path.Combine(path, e.Name);
                    if (_skip.Contains(full)) node = new DiskNode(e.Name, DiskNodeKind.Mount, dir) { Modified = e.Modified };
                    else
                    {
                        node = new DiskNode(e.Name, DiskNodeKind.Folder, dir) { Modified = e.Modified };
                        folders++;
                    }
                }
                else
                {
                    var bytes = e.Size;
                    node = new DiskNode(e.Name, e.Kind, dir) { Files = 1, Modified = e.Modified, Ext = e.Kind == DiskNodeKind.File ? ExtId(e.Name) : (short)0 };
                    if (e.Key is { } key && !_hardLinks.TryAdd(key, node)) bytes = 0; // a further name for a file already counted
                    node.Size = bytes;
                    size += bytes;
                    files++;
                }
                children.Add(node);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Interlocked.Increment(ref Errors);
        }
        if (token.IsCancellationRequested) return false;
        children.Sort((a, b) => b.Size.CompareTo(a.Size));
        dir.Children = children.ToArray();
        dir.AddUp(size, files, folders);
        dir.State = DiskNodeState.Done;
        foreach (var c in children)
            if (c.Kind == DiskNodeKind.Folder)
            {
                Interlocked.Increment(ref _outstanding);
                _queue.Push(c);
            }
        return true;
    }

    private readonly record struct Entry(string Name, DiskNodeKind Kind, long Size, long Modified, (ulong, ulong)? Key);

    private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    private IEnumerable<Entry> List(string path)
    {
        if (Allocated)
        {
            var names = new FileSystemEnumerable<string>(path, (ref FileSystemEntry e) => e.FileName.ToString(), Options);
            return names.Select(name => Native.Stat(Path.Combine(path, name)) is { } st ? new Entry(name, st.Kind, st.Size, st.Modified, st.Key) : default)
                .Where(e => e.Name is not null);
        }
        return new FileSystemEnumerable<Entry>(path, (ref FileSystemEntry e) =>
        {
            var link = (e.Attributes & FileAttributes.ReparsePoint) != 0;
            var kind = link ? DiskNodeKind.Link : e.IsDirectory ? DiskNodeKind.Folder : DiskNodeKind.File;
            return new Entry(e.FileName.ToString(), kind, kind == DiskNodeKind.File ? e.Length : 0, e.LastWriteTimeUtc.UtcTicks, null);
        }, Options);
    }

    /// <summary>
    /// The mount points a scan of <paramref name="roots"/> must not walk into: every other file system mounted
    /// below them. A folder mounted onto itself (an ostree bind mount, say) shows the same files and is walked.
    /// </summary>
    public static HashSet<string> SkipList(IEnumerable<string> roots)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            var mounts = Volumes.LinuxMounts();
            foreach (var root in roots) foreach (var s in SkipList(root, mounts)) skip.Add(s);
        }
        else if (OperatingSystem.IsMacOS())
        {
            skip.Add("/System/Volumes");
            skip.Add("/Volumes");
            skip.Add("/dev");
            foreach (var root in roots) skip.Remove(root);
        }
        return skip;
    }

    public static IEnumerable<string> SkipList(string root, IReadOnlyList<MountEntry> mounts)
    {
        // The mount the root lives on: the deepest one containing it (the last such, if mounted over).
        var home = mounts.LastOrDefault(m => Volumes.Inside(root, m.MountPoint) && mounts.Where(o => Volumes.Inside(root, o.MountPoint)).All(o => o.MountPoint.Length <= m.MountPoint.Length));
        foreach (var m in mounts)
        {
            if (m.MountPoint == root || !Volumes.Inside(m.MountPoint, root)) continue;
            if (home is not null && m.Device == home.Device)
            {
                var rel = m.MountPoint[home.MountPoint.TrimEnd('/').Length..];
                if (m.Root == home.Root.TrimEnd('/') + rel) continue;
            }
            yield return m.MountPoint;
        }
    }

    /// <summary>statx(2): the size allocated on disk, the inode for spotting hard links, without following links.</summary>
    private static class Native
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
        private static extern unsafe int StatX(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buf);

        public static readonly bool Works = Probe();

        private static bool Probe()
        {
            if (!OperatingSystem.IsLinux()) return false;
            try { return Stat("/") is not null; }
            catch { return false; }
        }

        public static unsafe (DiskNodeKind Kind, long Size, long Modified, (ulong, ulong)? Key)? Stat(string path)
        {
            var buf = stackalloc byte[256];
            // AT_FDCWD, AT_SYMLINK_NOFOLLOW | AT_NO_AUTOMOUNT | AT_STATX_DONT_SYNC, type|mode|nlink|mtime|ino|size|blocks
            if (StatX(-100, path, 0x100 | 0x800 | 0x4000, 0x747, buf) != 0) return null;
            var nlink = *(uint*)(buf + 16);
            var mode = *(ushort*)(buf + 28);
            var ino = *(ulong*)(buf + 32);
            var blocks = *(ulong*)(buf + 48);
            var mtime = *(long*)(buf + 112);
            var dev = ((ulong)*(uint*)(buf + 136) << 32) | *(uint*)(buf + 140);
            var kind = (mode & 0xF000) switch { 0x4000 => DiskNodeKind.Folder, 0xA000 => DiskNodeKind.Link, _ => DiskNodeKind.File };
            var modified = DateTime.UnixEpoch.Ticks + Math.Clamp(mtime, -62135596800L, 191266703999L) * TimeSpan.TicksPerSecond;
            return (kind, (long)blocks * 512, modified, kind == DiskNodeKind.File && nlink > 1 ? (dev, ino) : null);
        }
    }
}
