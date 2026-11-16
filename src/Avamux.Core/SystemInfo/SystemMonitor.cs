using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Avamux.Core.SystemInfo;

public sealed record CpuInfo(string Model, int Cores, double Usage, double[] PerCore, double? MHz, double? TempC);
public sealed record MemoryInfo(long Total, long Available, long SwapTotal, long SwapFree)
{
    public long Used => Math.Max(0, Total - Available);
    public long SwapUsed => Math.Max(0, SwapTotal - SwapFree);
}
public sealed record GpuInfo(string Name, string Vendor, double? Usage, long? VramTotal, long? VramUsed, double? TempC, double? PowerW);
public sealed record RateInfo(double InPerSec, double OutPerSec);
public sealed record BatteryInfo(string Name, int Percent, string Status, bool Device);

/// <summary>One reading of the machine. Anything that could not be read is null (or an empty list).</summary>
public sealed record SystemSnapshot(DateTime At, string Host, string Os, TimeSpan? Uptime, double[]? Load, CpuInfo? Cpu, MemoryInfo? Memory,
    IReadOnlyList<GpuInfo> Gpus, RateInfo? Network, RateInfo? DiskIo, IReadOnlyList<BatteryInfo> Batteries);

/// <summary>
/// Reads CPU, memory, GPU, network, disk and battery figures, best effort: each source is tried on its own and a
/// source that fails (a missing file, tool or library) is simply left out, and not tried again for a while.
/// Rates are measured between one <see cref="Sample"/> and the next; call it from one thread at a time.
/// </summary>
public sealed class SystemMonitor
{
    private long[]? _cpuPrev;
    private long[][]? _corePrev;
    private (long Rx, long Tx, long At)? _netPrev;
    private (long Read, long Write, long At)? _ioPrev;
    private string? _cpuModel;
    private string? _cpuTempFile;
    private bool _cpuTempLooked;
    private readonly Dictionary<string, DateTime> _broken = new();
    private readonly Nvml _nvml = new();
    private (DateTime At, List<GpuInfo> Gpus)? _smi;
    private readonly Dictionary<string, string> _gpuNames = new();

    public SystemSnapshot Sample()
    {
        var host = Environment.MachineName;
        var os = RuntimeInformation.OSDescription;
        var uptime = Try<TimeSpan?>("uptime", () => OperatingSystem.IsLinux()
            ? TimeSpan.FromSeconds(double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture))
            : TimeSpan.FromMilliseconds(Environment.TickCount64));
        var load = OperatingSystem.IsLinux()
            ? Try<double[]>("load", () => File.ReadAllText("/proc/loadavg").Split(' ').Take(3).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray())
            : null;
        return new SystemSnapshot(DateTime.Now, host, os, uptime, load, Try<CpuInfo>("cpu", Cpu), Try<MemoryInfo>("memory", Memory), Try<List<GpuInfo>>("gpu", Gpus) ?? [],
            Try<RateInfo>("network", Network), OperatingSystem.IsLinux() ? Try<RateInfo>("diskio", DiskIo) : null, OperatingSystem.IsLinux() ? Try<List<BatteryInfo>>("battery", Batteries) ?? [] : []);
    }

    /// <summary>Runs one source; a failure is logged once and the source is skipped for the next minute.</summary>
    private T? Try<T>(string what, Func<T?> read)
    {
        if (_broken.TryGetValue(what, out var since) && DateTime.UtcNow - since < TimeSpan.FromMinutes(1)) return default;
        try { return read(); }
        catch (Exception ex)
        {
            if (!_broken.ContainsKey(what)) Logging.Log.Debug("system", $"{what}: {ex.GetBaseException().Message}");
            _broken[what] = DateTime.UtcNow;
            return default;
        }
    }

    // ---- CPU ----

    private CpuInfo? Cpu()
    {
        var cores = Environment.ProcessorCount;
        if (OperatingSystem.IsLinux())
        {
            _cpuModel ??= File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal) || l.StartsWith("Model", StringComparison.Ordinal))
                ?.Split(':', 2)[1].Trim() ?? "CPU";
            var lines = File.ReadAllLines("/proc/stat").Where(l => l.StartsWith("cpu", StringComparison.Ordinal)).ToList();
            var total = Ticks(lines[0]);
            var usage = Busy(_cpuPrev, total);
            _cpuPrev = total;
            var per = lines.Skip(1).Select(Ticks).ToArray();
            var perUsage = new double[per.Length];
            for (var i = 0; i < per.Length; i++) perUsage[i] = Busy(_corePrev is { } p && i < p.Length ? p[i] : null, per[i]);
            _corePrev = per;
            return new CpuInfo(_cpuModel, cores, usage, perUsage, Try<double?>("cpufreq", LinuxMHz), Try<double?>("cputemp", LinuxCpuTemp));
        }
        if (OperatingSystem.IsWindows())
        {
            _cpuModel ??= Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            // Kernel time includes idle time.
            long[] t = [user, 0, kernel - idle, idle];
            var usage = Busy(_cpuPrev, t);
            _cpuPrev = t;
            return new CpuInfo(_cpuModel, cores, usage, [], null, null);
        }
        if (OperatingSystem.IsMacOS())
        {
            _cpuModel ??= Mac.SysctlString("machdep.cpu.brand_string") ?? "CPU";
            var info = new int[4];
            var count = 4;
            if (Mac.host_statistics(Mac.mach_host_self(), 3, info, ref count) != 0) return null;
            long[] t = [info[0], info[3], info[1], info[2]]; // user, nice, system, idle
            var usage = Busy(_cpuPrev, t);
            _cpuPrev = t;
            return new CpuInfo(_cpuModel, cores, usage, [], null, null);
        }
        return null;
    }

    /// <summary>The fields of a /proc/stat cpu line: user nice system idle iowait irq softirq steal.</summary>
    private static long[] Ticks(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>The busy share between two readings; idle is field 3 (plus iowait, field 4, when there is one).</summary>
    private static double Busy(long[]? before, long[] now)
    {
        if (before is null || before.Length != now.Length) return 0;
        long total = 0, idle = 0;
        for (var i = 0; i < now.Length; i++)
        {
            var d = now[i] - before[i];
            total += d;
            if (i is 3 or 4) idle += d;
        }
        return total <= 0 ? 0 : Math.Clamp(1 - idle / (double)total, 0, 1);
    }

    private static double? LinuxMHz()
    {
        var files = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*").Select(d => Path.Combine(d, "cpufreq", "scaling_cur_freq")).Where(File.Exists).ToList();
        if (files.Count == 0) return null;
        return files.Average(f => long.Parse(File.ReadAllText(f).Trim(), CultureInfo.InvariantCulture)) / 1000.0;
    }

    private double? LinuxCpuTemp()
    {
        if (!_cpuTempLooked)
        {
            _cpuTempLooked = true;
            _cpuTempFile = FindCpuTemp();
        }
        return _cpuTempFile is null ? null : long.Parse(File.ReadAllText(_cpuTempFile).Trim(), CultureInfo.InvariantCulture) / 1000.0;
    }

    /// <summary>The CPU package temperature sensor: coretemp's "Package", k10temp's "Tctl", or failing those the first ACPI zone.</summary>
    private static string? FindCpuTemp()
    {
        string? fallback = null;
        foreach (var dir in Directory.Exists("/sys/class/hwmon") ? Directory.GetDirectories("/sys/class/hwmon") : [])
        {
            var name = File.Exists(Path.Combine(dir, "name")) ? File.ReadAllText(Path.Combine(dir, "name")).Trim() : "";
            if (name is "coretemp" or "k10temp" or "zenpower" or "cpu_thermal" or "soc_thermal")
            {
                foreach (var label in Directory.GetFiles(dir, "temp*_label"))
                {
                    var text = File.ReadAllText(label).Trim();
                    if (text.StartsWith("Package", StringComparison.Ordinal) || text is "Tctl" or "Tdie")
                        return label.Replace("_label", "_input", StringComparison.Ordinal);
                }
                var first = Path.Combine(dir, "temp1_input");
                if (File.Exists(first)) return first;
            }
            if (name == "acpitz" && File.Exists(Path.Combine(dir, "temp1_input"))) fallback ??= Path.Combine(dir, "temp1_input");
        }
        return fallback;
    }

    // ---- memory ----

    private static MemoryInfo? Memory()
    {
        if (OperatingSystem.IsLinux())
        {
            var map = new Dictionary<string, long>();
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2 && long.TryParse(parts[1].Trim().Split(' ')[0], out var kb)) map[parts[0]] = kb * 1024;
            }
            var total = map.GetValueOrDefault("MemTotal");
            var available = map.TryGetValue("MemAvailable", out var a) ? a : map.GetValueOrDefault("MemFree") + map.GetValueOrDefault("Cached");
            return new MemoryInfo(total, available, map.GetValueOrDefault("SwapTotal"), map.GetValueOrDefault("SwapFree"));
        }
        if (OperatingSystem.IsWindows())
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref m)) return null;
            return new MemoryInfo((long)m.TotalPhys, (long)m.AvailPhys, (long)(m.TotalPageFile - m.TotalPhys), (long)(m.AvailPageFile - Math.Min(m.AvailPageFile, m.AvailPhys)));
        }
        if (OperatingSystem.IsMacOS())
        {
            var total = Mac.SysctlLong("hw.memsize") ?? 0;
            var page = Mac.SysctlLong("hw.pagesize") ?? 4096;
            var vm = new int[38];
            var count = vm.Length;
            if (Mac.host_statistics64(Mac.mach_host_self(), 4, vm, ref count) != 0) return new MemoryInfo(total, 0, 0, 0);
            // free + inactive + purgeable + speculative pages can be handed out without paging.
            var available = ((long)(uint)vm[0] + (uint)vm[2] + (uint)vm[22] + (uint)vm[23]) * page;
            return new MemoryInfo(total, Math.Min(total, available), 0, 0);
        }
        var gc = GC.GetGCMemoryInfo();
        return gc.TotalAvailableMemoryBytes > 0 ? new MemoryInfo(gc.TotalAvailableMemoryBytes, 0, 0, 0) : null;
    }

    // ---- GPUs ----

    private List<GpuInfo> Gpus()
    {
        var list = new List<GpuInfo>();
        if (_nvml.Available) list.AddRange(_nvml.Read());
        else if (NvidiaSmi() is { } smi) list.AddRange(smi);
        if (OperatingSystem.IsLinux()) list.AddRange(LinuxDrmGpus(skipNvidia: list.Count > 0));
        return list;
    }

    /// <summary>nvidia-smi, when the NVIDIA library could not be loaded directly; asked at most every five seconds.</summary>
    private List<GpuInfo>? NvidiaSmi()
    {
        if (_smi is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(5)) return cached.Gpus;
        if (_broken.TryGetValue("nvidia-smi", out var since) && DateTime.UtcNow - since < TimeSpan.FromMinutes(10)) return null;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "--query-gpu=name,utilization.gpu,memory.total,memory.used,temperature.gpu,power.draw", "--format=csv,noheader,nounits" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000) || p.ExitCode != 0) throw new InvalidOperationException("nvidia-smi failed");
            var gpus = new List<GpuInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(',').Select(s => s.Trim()).ToArray();
                if (f.Length < 6) continue;
                double? D(string s) => double.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;
                gpus.Add(new GpuInfo(f[0], "NVIDIA", D(f[1]) / 100, (long?)(D(f[2]) * 1048576), (long?)(D(f[3]) * 1048576), D(f[4]), D(f[5])));
            }
            _smi = (DateTime.UtcNow, gpus);
            return gpus;
        }
        catch
        {
            _broken["nvidia-smi"] = DateTime.UtcNow;
            return null;
        }
    }

    /// <summary>GPUs under /sys/class/drm: AMD cards report busy and VRAM there; Intel ones at least their name.</summary>
    private IEnumerable<GpuInfo> LinuxDrmGpus(bool skipNvidia)
    {
        if (!Directory.Exists("/sys/class/drm")) yield break;
        foreach (var card in Directory.GetDirectories("/sys/class/drm", "card*").Where(d => !Path.GetFileName(d).Contains('-')).Order())
        {
            var dev = Path.Combine(card, "device");
            var vendorId = ReadText(Path.Combine(dev, "vendor"));
            var vendor = vendorId switch { "0x1002" => "AMD", "0x8086" => "Intel", "0x10de" => "NVIDIA", _ => null };
            if (vendor is null || vendor == "NVIDIA" && skipNvidia) continue;
            double? busy = ReadLong(Path.Combine(dev, "gpu_busy_percent")) is { } b ? b / 100.0 : null;
            var total = ReadLong(Path.Combine(dev, "mem_info_vram_total"));
            var used = ReadLong(Path.Combine(dev, "mem_info_vram_used"));
            double? temp = null, power = null;
            var hwmon = Path.Combine(dev, "hwmon");
            if (Directory.Exists(hwmon))
                foreach (var h in Directory.GetDirectories(hwmon))
                {
                    temp ??= ReadLong(Path.Combine(h, "temp1_input")) / 1000.0;
                    power ??= (ReadLong(Path.Combine(h, "power1_average")) ?? ReadLong(Path.Combine(h, "power1_input"))) / 1e6;
                }
            yield return new GpuInfo(GpuName(dev, vendor), vendor, busy, total, used, temp, power);
        }
    }

    private string GpuName(string dev, string vendor)
    {
        if (_gpuNames.TryGetValue(dev, out var name)) return name;
        name = ReadText(Path.Combine(dev, "product_name"));
        if (string.IsNullOrEmpty(name))
        {
            try
            {
                var slot = File.ReadLines(Path.Combine(dev, "uevent")).FirstOrDefault(l => l.StartsWith("PCI_SLOT_NAME=", StringComparison.Ordinal))?[14..];
                if (slot is not null)
                {
                    var psi = new ProcessStartInfo("lspci") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    foreach (var a in new[] { "-mm", "-s", slot }) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi)!;
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    // "00:02.0" "VGA compatible controller" "Intel Corporation" "Raptor Lake-S GT1 [UHD Graphics 770]" ...
                    var quoted = output.Split('"').Where((_, i) => i % 2 == 1).ToArray();
                    if (quoted.Length >= 4) name = quoted[3];
                }
            }
            catch { /* no lspci */ }
        }
        if (string.IsNullOrEmpty(name)) name = vendor + " GPU";
        else if (!name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) name = vendor + " " + name;
        return _gpuNames[dev] = name;
    }

    private static string? ReadText(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() : null; }
        catch { return null; }
    }

    private static long? ReadLong(string file) => long.TryParse(ReadText(file), CultureInfo.InvariantCulture, out var v) ? v : null;

    // ---- network and disk ----

    private RateInfo? Network()
    {
        long rx = 0, tx = 0;
        if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
            {
                var parts = line.Split(':', 2);
                if (parts.Length < 2) continue;
                var name = parts[0].Trim();
                if (name == "lo" || name.StartsWith("veth", StringComparison.Ordinal) || name.StartsWith("docker", StringComparison.Ordinal)
                    || name.StartsWith("br-", StringComparison.Ordinal) || name.StartsWith("virbr", StringComparison.Ordinal) || name.StartsWith("podman", StringComparison.Ordinal)) continue;
                var f = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                rx += long.Parse(f[0], CultureInfo.InvariantCulture);
                tx += long.Parse(f[8], CultureInfo.InvariantCulture);
            }
        }
        else
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.OperationalStatus != OperationalStatus.Up) continue;
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        var now = Stopwatch.GetTimestamp();
        var prev = _netPrev;
        _netPrev = (rx, tx, now);
        if (prev is not { } p) return new RateInfo(0, 0);
        var secs = Math.Max(0.001, (now - p.At) / (double)Stopwatch.Frequency);
        return new RateInfo(Math.Max(0, rx - p.Rx) / secs, Math.Max(0, tx - p.Tx) / secs);
    }

    private static readonly System.Text.RegularExpressions.Regex WholeDisk =
        new(@"^(nvme\d+n\d+|sd[a-z]+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|mmcblk\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private RateInfo? DiskIo()
    {
        long read = 0, write = 0;
        foreach (var line in File.ReadLines("/proc/diskstats"))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10 || !WholeDisk.IsMatch(f[2])) continue;
            read += long.Parse(f[5], CultureInfo.InvariantCulture) * 512;
            write += long.Parse(f[9], CultureInfo.InvariantCulture) * 512;
        }
        var now = Stopwatch.GetTimestamp();
        var prev = _ioPrev;
        _ioPrev = (read, write, now);
        if (prev is not { } p) return new RateInfo(0, 0);
        var secs = Math.Max(0.001, (now - p.At) / (double)Stopwatch.Frequency);
        return new RateInfo(Math.Max(0, read - p.Read) / secs, Math.Max(0, write - p.Write) / secs);
    }

    private static List<BatteryInfo> Batteries()
    {
        var list = new List<BatteryInfo>();
        const string root = "/sys/class/power_supply";
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (ReadText(Path.Combine(dir, "type")) != "Battery" || ReadLong(Path.Combine(dir, "capacity")) is not { } pct) continue;
            var device = ReadText(Path.Combine(dir, "scope")) == "Device";
            var status = ReadText(Path.Combine(dir, "status")) ?? "";
            if (device && pct == 0 && status is "" or "Unknown") continue; // a wireless device that is switched off
            var name = ReadText(Path.Combine(dir, "model_name")) is { Length: > 0 } model ? model : device ? Path.GetFileName(dir) : "Battery";
            list.Add(new BatteryInfo(name, (int)pct, status, device));
        }
        return list.OrderBy(b => b.Device).ToList();
    }

    // ---- native ----

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static class Mac
    {
        [DllImport("libSystem.dylib")] public static extern int mach_host_self();
        [DllImport("libSystem.dylib")] public static extern int host_statistics(int host, int flavor, int[] info, ref int count);
        [DllImport("libSystem.dylib")] public static extern int host_statistics64(int host, int flavor, int[] info, ref int count);
        [DllImport("libSystem.dylib")] private static extern int sysctlbyname(string name, byte[]? value, ref nint length, nint newValue, nint newLength);

        public static long? SysctlLong(string name)
        {
            var buf = new byte[8];
            nint len = 8;
            if (sysctlbyname(name, buf, ref len, 0, 0) != 0) return null;
            return len == 4 ? BitConverter.ToInt32(buf) : BitConverter.ToInt64(buf);
        }

        public static string? SysctlString(string name)
        {
            nint len = 0;
            if (sysctlbyname(name, null, ref len, 0, 0) != 0 || len <= 0) return null;
            var buf = new byte[len];
            if (sysctlbyname(name, buf, ref len, 0, 0) != 0) return null;
            return System.Text.Encoding.UTF8.GetString(buf, 0, (int)len).TrimEnd('\0');
        }
    }
}

/// <summary>The NVIDIA management library (libnvidia-ml / nvml.dll), loaded if it is there.</summary>
internal sealed unsafe class Nvml
{
    private readonly delegate* unmanaged<uint*, int> _count;
    private readonly delegate* unmanaged<uint, nint*, int> _handle;
    private readonly delegate* unmanaged<nint, byte*, uint, int> _name;
    private readonly delegate* unmanaged<nint, ulong*, int> _memory;
    private readonly delegate* unmanaged<nint, uint*, int> _utilization;
    private readonly delegate* unmanaged<nint, int, uint*, int> _temperature;
    private readonly delegate* unmanaged<nint, uint*, int> _power;
    private readonly Dictionary<nint, string> _names = new();

    public bool Available { get; }

    public Nvml()
    {
        try
        {
            var name = OperatingSystem.IsWindows() ? "nvml.dll" : OperatingSystem.IsLinux() ? "libnvidia-ml.so.1" : null;
            if (name is null || !NativeLibrary.TryLoad(name, out var lib)) return;
            nint F(string fn) => NativeLibrary.GetExport(lib, fn);
            var init = (delegate* unmanaged<int>)F("nvmlInit_v2");
            if (init() != 0) return;
            _count = (delegate* unmanaged<uint*, int>)F("nvmlDeviceGetCount_v2");
            _handle = (delegate* unmanaged<uint, nint*, int>)F("nvmlDeviceGetHandleByIndex_v2");
            _name = (delegate* unmanaged<nint, byte*, uint, int>)F("nvmlDeviceGetName");
            _memory = (delegate* unmanaged<nint, ulong*, int>)F("nvmlDeviceGetMemoryInfo");
            _utilization = (delegate* unmanaged<nint, uint*, int>)F("nvmlDeviceGetUtilizationRates");
            _temperature = (delegate* unmanaged<nint, int, uint*, int>)F("nvmlDeviceGetTemperature");
            _power = (delegate* unmanaged<nint, uint*, int>)F("nvmlDeviceGetPowerUsage");
            Available = true;
        }
        catch (Exception ex) { Logging.Log.Debug("system", "NVML: " + ex.Message); }
    }

    public List<GpuInfo> Read()
    {
        var list = new List<GpuInfo>();
        uint count;
        if (_count(&count) != 0) return list;
        var buf = stackalloc byte[96];
        var mem = stackalloc ulong[3];
        var util = stackalloc uint[2];
        for (uint i = 0; i < count; i++)
        {
            nint h;
            if (_handle(i, &h) != 0) continue;
            if (!_names.TryGetValue(h, out var name))
            {
                name = _name(h, buf, 96) == 0 ? new string((sbyte*)buf) : "NVIDIA GPU";
                _names[h] = name;
            }
            uint temp, power;
            var hasMem = _memory(h, mem) == 0;
            list.Add(new GpuInfo(name, "NVIDIA", _utilization(h, util) == 0 ? util[0] / 100.0 : null, hasMem ? (long)mem[0] : null, hasMem ? (long)mem[2] : null,
                _temperature(h, 0, &temp) == 0 ? temp : null, _power(h, &power) == 0 ? power / 1000.0 : null));
        }
        return list;
    }
}
