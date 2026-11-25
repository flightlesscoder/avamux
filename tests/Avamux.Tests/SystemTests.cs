using Avamux.Core;
using Avamux.Core.SystemInfo;

namespace Avamux.Tests;

public class SystemTests
{
    // An ostree / btrfs machine: one disk with three subvolumes, two bind mounts of folders inside one of them.
    private const string MountInfo = """
        26 1 0:41 / / ro,relatime shared:1 - overlay composefs ro,lowerdir=x
        27 26 0:36 /root/ostree/deploy/default/deploy/abc.0/etc /etc rw,relatime shared:2 - btrfs /dev/nvme0n1p3 rw,subvol=/root
        28 26 0:36 /root /sysroot rw,relatime shared:3 - btrfs /dev/nvme0n1p3 rw,subvol=/root
        29 28 0:36 /root/ostree/deploy/default/var /sysroot/ostree/deploy/default/var rw shared:4 - btrfs /dev/nvme0n1p3 rw,subvol=/root
        30 26 0:7 / /dev rw,nosuid shared:5 - devtmpfs devtmpfs rw
        31 26 0:29 / /run rw,nosuid shared:6 - tmpfs tmpfs rw
        32 26 0:36 /var /var rw,relatime shared:7 - btrfs /dev/nvme0n1p3 rw,subvol=/var
        33 32 0:36 /home /var/home rw,relatime shared:8 - btrfs /dev/nvme0n1p3 rw,subvol=/home
        34 26 259:2 / /boot rw,relatime shared:9 - ext4 /dev/nvme0n1p2 rw
        35 34 259:1 / /boot/efi rw,relatime shared:10 - vfat /dev/nvme0n1p1 rw
        36 33 0:50 / /var/home/me/My\040Drive rw shared:11 - fuse.rclone remote: rw
        """;

    [Fact]
    public void Mounts_group_into_volumes_with_the_folders_a_scan_must_walk()
    {
        var mounts = Volumes.ParseMountInfo(MountInfo);
        Assert.Contains(mounts, m => m.MountPoint == "/var/home/me/My Drive");
        var groups = Volumes.Group(mounts);
        Assert.DoesNotContain(groups, g => g.All.Any(m => m.FsType is "tmpfs" or "devtmpfs" or "overlay"));
        var disk = groups.Single(g => g.Device == "0:36");
        Assert.Equal(["/sysroot", "/var", "/var/home"], disk.Roots.Select(r => r.MountPoint));
        Assert.Equal(5, disk.All.Count);
        Assert.Single(groups.Single(g => g.Device == "259:2").Roots);
    }

    [Fact]
    public void A_scan_skips_other_file_systems_but_walks_folders_bound_onto_themselves()
    {
        var mounts = Volumes.ParseMountInfo(MountInfo);
        Assert.Empty(DiskScan.SkipList("/sysroot", mounts));
        Assert.Equal(["/var/home", "/var/home/me/My Drive"], DiskScan.SkipList("/var", mounts));
        Assert.Equal(["/boot", "/boot/efi", "/dev", "/etc", "/run", "/sysroot", "/sysroot/ostree/deploy/default/var", "/var", "/var/home", "/var/home/me/My Drive"],
            DiskScan.SkipList("/", mounts).Order(StringComparer.Ordinal));
        Assert.Equal(["/var/home/me/My Drive"], DiskScan.SkipList("/var/home/me", mounts));
    }

    private static string MakeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "avamux-disk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "big", "deeper"));
        Directory.CreateDirectory(Path.Combine(root, "small"));
        File.WriteAllBytes(Path.Combine(root, "big", "video.mkv"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(root, "big", "deeper", "song.mp3"), new byte[100_000]);
        File.WriteAllBytes(Path.Combine(root, "small", "note.txt"), new byte[5_000]);
        File.WriteAllBytes(Path.Combine(root, "README"), new byte[10]);
        for (var i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(root, "small", $"f{i}.dat"), [1, 2, 3]);
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "big"));
        return root;
    }

    private static void WaitFor(DiskScan scan, DiskScanState state)
    {
        for (var i = 0; i < 500 && scan.State != state; i++) Thread.Sleep(10);
        Assert.Equal(state, scan.State);
    }

    [Fact]
    public void A_folder_scan_totals_sizes_and_survives_being_saved_and_loaded()
    {
        var root = MakeTree();
        var data = Path.Combine(Path.GetTempPath(), "avamux-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(data, data);
            var service = new DiskScanService(paths);
            var scan = service.ForFolder(root);
            service.Load(scan); // someone is looking, so the finished tree stays in memory
            scan.Start();
            WaitFor(scan, DiskScanState.Done);
            var tree = scan.Root!;
            Assert.Equal(44 + (OperatingSystem.IsWindows() ? 0 : 1), tree.Files);
            Assert.Equal(3, tree.Folders);
            Assert.True(tree.Size >= 405_000, $"{tree.Size}");
            Assert.Equal("big", tree.Children[0].Name);
            Assert.Equal(tree.Children.Sum(c => c.Size), tree.Size);
            if (!OperatingSystem.IsWindows()) Assert.Equal(DiskNodeKind.Link, tree.Children.Single(c => c.Name == "link").Kind);
            var song = tree.Children[0].Children.Single(c => c.Name == "deeper").Children.Single();
            Assert.Equal(Path.Combine(root, "big", "deeper", "song.mp3"), song.FullPath);
            Assert.Equal("mp3", scan.ExtensionName(song.Ext));
            service.Save(scan);

            // A new avamux sees the scan, loads its tree when asked, and lets it go again.
            var again = new DiskScanService(paths);
            var saved = Assert.Single(again.Scans);
            Assert.Equal(DiskScanState.Done, saved.State);
            Assert.Null(saved.Root);
            Assert.Equal(tree.Size, saved.Size);
            again.Load(saved);
            Assert.Equal(tree.Size, saved.Root!.Size);
            Assert.Equal(tree.Files, saved.Root.Files);
            Assert.Equal("mp3", saved.ExtensionName(saved.Root.Children[0].Children.Single(c => c.Name == "deeper").Children[0].Ext));

            // Rescanning one folder after a change.
            var before = saved.Root.Size;
            File.Delete(Path.Combine(root, "big", "video.mkv"));
            saved.Rescan(saved.Root.Children[0]);
            WaitFor(saved, DiskScanState.Done);
            Assert.True(before - saved.Root.Size >= 290_000, $"{before} -> {saved.Root.Size}");
            Assert.Equal(saved.Root.Children.Sum(c => c.Size), saved.Root.Size);
            again.Release(saved);
            Thread.Sleep(200); // the save after the rescan
            Assert.Null(saved.Root);
            again.Delete(saved);
            Assert.Empty(new DiskScanService(paths).Scans);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(data, true);
        }
    }

    [Fact]
    public void Hard_links_count_once_even_after_a_restart_and_a_rescan()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = MakeTree();
        var data = Path.Combine(Path.GetTempPath(), "avamux-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.Diagnostics.Process.Start("ln", [Path.Combine(root, "big", "video.mkv"), Path.Combine(root, "small", "same-video.mkv")])!.WaitForExit();
            var paths = new AppPaths(data, data);
            var service = new DiskScanService(paths);
            var scan = service.ForFolder(root);
            service.Load(scan);
            scan.Start();
            WaitFor(scan, DiskScanState.Done);
            var size = scan.Root!.Size;
            Assert.True(size < 600_000, $"{size}"); // the video once, not twice
            service.Save(scan);

            var again = new DiskScanService(paths);
            var saved = again.Scans.Single();
            again.Load(saved);
            Assert.Equal(size, saved.Root!.Size);
            // Reading the folder with the counted name again does not lose the video, nor count it twice.
            var counted = saved.Root.Children.First(c => c.Name is "big" or "small" && c.Size > 250_000);
            saved.Rescan(counted);
            WaitFor(saved, DiskScanState.Done);
            Assert.Equal(size, saved.Root.Size);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(data, true);
        }
    }

    [Fact]
    public void A_stopped_scan_keeps_its_pending_folders_and_resumes()
    {
        var root = MakeTree();
        try
        {
            var scan = new DiskScan("t", "t", [root], null, null);
            scan.Start();
            scan.Pause();
            Assert.True(scan.State is DiskScanState.Paused or DiskScanState.Done);
            scan.Stop(wait: true);
            Assert.True(scan.State is DiskScanState.Stopped or DiskScanState.Done);
            scan.Resume();
            WaitFor(scan, DiskScanState.Done);
            Assert.Equal(3, scan.Root!.Folders);
            Assert.Equal(1.0, scan.Fraction);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_file_time_of_zero_or_one_second_is_no_time()
    {
        var node = new DiskNode("f", DiskNodeKind.File, null);
        Assert.Null(node.ModifiedTime);
        node.Modified = DateTime.UnixEpoch.Ticks;                              // ostree
        Assert.Null(node.ModifiedTime);
        node.Modified = DateTime.UnixEpoch.AddSeconds(1).Ticks;                // Nix
        Assert.Null(node.ModifiedTime);
        node.Modified = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc).Ticks;
        Assert.Equal(new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc).ToLocalTime(), node.ModifiedTime);
    }

    [Fact]
    public void The_treemap_covers_every_pixel_with_areas_to_scale()
    {
        var root = new DiskNode("/r", DiskNodeKind.Folder, null) { State = DiskNodeState.Done };
        var sizes = new long[] { 600, 250, 100, 30, 15, 5 };
        root.Children = sizes.Select((s, i) => new DiskNode($"f{i}", DiskNodeKind.File, root) { Size = s, Ext = (short)i }).ToArray();
        root.Size = sizes.Sum();
        const int w = 200, h = 100;
        var pixels = new uint[w * h];
        uint[] colours = [0xff000001, 0xff000002, 0xff000003, 0xff000004, 0xff000005, 0xff000006];
        // Flat colours (alpha below 255) so each file's pixels can be counted.
        var tiles = TreeMap.Render(root, w, h, pixels, 0xff000000, n => colours[n.Ext] & 0xfeffffff);
        Assert.DoesNotContain(0xff000000u, pixels);
        for (var i = 0; i < sizes.Length; i++)
        {
            var share = pixels.Count(p => p == colours[i]) / (double)(w * h);
            Assert.InRange(share, sizes[i] / 1000.0 - 0.01, sizes[i] / 1000.0 + 0.01);
        }
        Assert.Equal("f0", tiles.PathTo(5, 5)[^1].Node.Name);
        Assert.Same(tiles.Kids[2], tiles.Find(root.Children[2]));
        var worst = tiles.Kids.Take(3).Max(t => Math.Max(t.W / t.H, t.H / t.W));
        Assert.True(worst < 3, $"{worst}"); // squarified: no long slivers for the big ones
    }

    [Fact]
    public void The_monitor_reads_what_it_can_without_throwing()
    {
        var m = new SystemMonitor();
        m.Sample();
        Thread.Sleep(100);
        var s = m.Sample();
        Assert.False(string.IsNullOrEmpty(s.Host));
        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(s.Cpu);
            Assert.InRange(s.Cpu!.Usage, 0, 1);
            Assert.True(s.Memory!.Total > 0);
            Assert.NotEmpty(Volumes.List());
        }
    }
}
