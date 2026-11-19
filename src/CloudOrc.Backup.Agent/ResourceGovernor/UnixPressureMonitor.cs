using System.Diagnostics;
using System.Globalization;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Platform;

namespace CloudOrc.Backup.Agent.ResourceGovernor;

/// <summary>
/// Whole-machine pressure on Linux and macOS, the input of the adaptive throttle (the backup slows
/// down when the machine is busy, so users and applications never feel it).
/// <list type="bullet">
/// <item>Linux CPU: /proc/stat deltas. Disk latency: /proc/diskstats (ms per completed IO on whole
/// disks). Memory: MemAvailable from /proc/meminfo.</item>
/// <item>macOS: CPU from the 1-minute load average; disk latency and memory are not sampled (the
/// background task policy already yields to interactive work).</item>
/// </list>
/// Every metric is optional: a value that cannot be read is null and simply not used.
/// </summary>
public sealed class UnixPressureMonitor : IPressureMonitor
{
    private readonly IReadOnlyList<string> _protectedProcessNames;
    private readonly string _procRoot;
    private (long Busy, long Total)? _lastCpu;
    private (long Ios, long Ticks)? _lastDisk;

    public UnixPressureMonitor(ResourceGovernorOptions options, string procRoot = "/proc")
    {
        _protectedProcessNames = options.ProtectedProcessNames;
        _procRoot = procRoot;
        // Baseline for the first delta (missing files simply yield null, e.g. on macOS).
        _lastCpu = ReadCpu();
        _lastDisk = ReadDisk();
    }

    public PressureSample Sample()
    {
        if (OperatingSystem.IsLinux())
        {
            return new PressureSample(CpuPercent(), DiskLatencyMs(), AvailableMemoryMb(), IsProtectedAppRunning());
        }

        double? cpu = null;
        if (NativeUnix.LoadAverage1() is { } load)
        {
            cpu = Math.Clamp(load / Math.Max(1, Environment.ProcessorCount) * 100, 0, 100);
        }

        return new PressureSample(cpu, null, null, IsProtectedAppRunning());
    }

    public void Dispose()
    {
    }

    internal double? CpuPercent()
    {
        var now = ReadCpu();
        var last = _lastCpu;
        _lastCpu = now;
        if (now is not { } n || last is not { } l || n.Total <= l.Total)
        {
            return null;
        }

        return Math.Round((n.Busy - l.Busy) * 100.0 / (n.Total - l.Total), 1);
    }

    internal double? DiskLatencyMs()
    {
        var now = ReadDisk();
        var last = _lastDisk;
        _lastDisk = now;
        if (now is not { } n || last is not { } l || n.Ios <= l.Ios)
        {
            return null;
        }

        return Math.Round((double)(n.Ticks - l.Ticks) / (n.Ios - l.Ios), 2);
    }

    internal double? AvailableMemoryMb()
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(_procRoot, "meminfo")))
            {
                if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    var kb = line["MemAvailable:".Length..].Trim().Split(' ')[0];
                    return long.TryParse(kb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v / 1024.0 : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private (long Busy, long Total)? ReadCpu()
    {
        try
        {
            var line = File.ReadLines(Path.Combine(_procRoot, "stat")).FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            var values = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
            if (values.Length < 5)
            {
                return null;
            }

            // user nice system idle iowait irq softirq steal ...; idle + iowait = not busy.
            var total = values.Take(Math.Min(values.Length, 8)).Sum();
            var idle = values[3] + values[4];
            return (total - idle, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private (long Ios, long Ticks)? ReadDisk()
    {
        try
        {
            long ios = 0, ticks = 0;
            foreach (var line in File.ReadLines(Path.Combine(_procRoot, "diskstats")))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 14 || !IsWholeDisk(f[2]))
                {
                    continue;
                }

                // 3 reads completed, 6 ms reading, 7 writes completed, 10 ms writing
                ios += Parse(f[3]) + Parse(f[7]);
                ticks += Parse(f[6]) + Parse(f[10]);
            }

            return ios > 0 ? (ios, ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static long Parse(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>sda, vdb, xvda, nvme0n1, mmcblk0 - not partitions (sda1, nvme0n1p1), loop or ram devices.</summary>
    internal static bool IsWholeDisk(string name)
    {
        if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)
            || name.StartsWith("dm-", StringComparison.Ordinal) || name.StartsWith("zram", StringComparison.Ordinal))
        {
            return false;
        }

        if (name.StartsWith("nvme", StringComparison.Ordinal) || name.StartsWith("mmcblk", StringComparison.Ordinal))
        {
            return !name.Contains('p', StringComparison.Ordinal) || name.LastIndexOf('p') < name.LastIndexOf('n');
        }

        return (name.StartsWith("sd", StringComparison.Ordinal) || name.StartsWith("vd", StringComparison.Ordinal)
                || name.StartsWith("xvd", StringComparison.Ordinal) || name.StartsWith("hd", StringComparison.Ordinal))
               && !char.IsAsciiDigit(name[^1]);
    }

    private bool IsProtectedAppRunning()
    {
        foreach (var name in _protectedProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }

        return false;
    }
}
