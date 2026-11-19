using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace CloudOrc.Backup.Agent.ResourceGovernor;

/// <summary>
/// Linux / macOS counterpart of <see cref="WindowsJobObject"/>.
/// <para>
/// Linux with cgroup v2 (every current distribution: Ubuntu 22.04+, Debian 12+, RHEL/Rocky 9+):
/// each job gets its own child cgroup under the agent's systemd unit (the unit sets
/// <c>Delegate=yes</c>) with a hard <c>cpu.max</c> and <c>memory.max</c>/<c>memory.high</c>. The
/// worker and every process it starts (kopia, pg_dump) live in that cgroup, so the cap holds for the
/// whole tree, exactly like a Job Object. Disposing kills everything left in the cgroup.
/// </para>
/// <para>
/// No cgroup v2 (old kernels, containers without delegation) and macOS: there is no hard cap; the
/// worker still runs at low CPU/IO priority (see <see cref="WorkerLaunch"/>), the adaptive
/// governor still backs off under pressure, and the whole process tree is killed on dispose.
/// </para>
/// </summary>
public sealed class UnixJobLimiter : IJobObject
{
    private const long CpuPeriodMicros = 100_000;
    private static readonly Lock SetupLock = new();
    private static string? _delegatedRoot;
    private static bool _setupAttempted;

    private readonly ILogger _logger;
    private readonly string? _cgroup;
    private Process? _worker;
    private long _peakMemory;

    public UnixJobLimiter(ILogger logger, string cgroupMount = "/sys/fs/cgroup")
    {
        _logger = logger;
        if (OperatingSystem.IsLinux())
        {
            var root = EnsureDelegatedRoot(cgroupMount, logger);
            if (root is not null)
            {
                var path = Path.Combine(root, $"job-{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(path);
                    _cgroup = path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Could not create a job cgroup ({Error}); the worker runs at low priority without a hard cap.", ex.Message);
                }
            }
        }
    }

    /// <summary>True when hard CPU/memory caps are enforced by cgroup v2.</summary>
    public bool HardCapsActive => _cgroup is not null;

    public void Configure(ResourceBudget budget)
    {
        if (_cgroup is null)
        {
            return;
        }

        SetCpuRate(budget.MaxCpuPercent);
        // memory.high throttles (reclaims) before memory.max kills: the engine slows down instead of dying.
        var high = (long)(budget.MaxMemoryBytes * 0.9);
        TryWrite("memory.high", high.ToString(CultureInfo.InvariantCulture));
        TryWrite("memory.max", budget.MaxMemoryBytes.ToString(CultureInfo.InvariantCulture));
        TryWrite("memory.swap.max", "0");
    }

    public void SetCpuRate(double percentOfMachine)
    {
        if (_cgroup is null)
        {
            return;
        }

        var cpus = Math.Max(1, Environment.ProcessorCount);
        var quota = (long)Math.Max(1000, percentOfMachine / 100.0 * cpus * CpuPeriodMicros);
        TryWrite("cpu.max", $"{quota.ToString(CultureInfo.InvariantCulture)} {CpuPeriodMicros.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Per-volume byte caps need block-device numbers; the low IO priority class is used instead.</summary>
    public bool SetDiskBandwidth(IReadOnlyCollection<string> volumeRoots, long? bytesPerSecond) => false;

    public void Assign(Process process)
    {
        _worker = process;
        if (_cgroup is null)
        {
            return;
        }

        if (!TryWrite("cgroup.procs", process.Id.ToString(CultureInfo.InvariantCulture)))
        {
            // Never run a job outside its budget once a cgroup exists (same rule as the Job Object).
            throw new InvalidOperationException("Could not move the backup worker into its cgroup.");
        }
    }

    public JobUsage QueryUsage()
    {
        if (_cgroup is not null)
        {
            var cpuMicros = ReadKeyed("cpu.stat", "usage_usec") ?? 0;
            var memory = ReadLong("memory.current") ?? 0;
            var peak = ReadLong("memory.peak") ?? Math.Max(_peakMemory, memory);
            _peakMemory = Math.Max(_peakMemory, peak);
            var procs = ReadLines("cgroup.procs").Length;
            return new JobUsage(cpuMicros * 10, memory, _peakMemory, procs);
        }

        // Fallback: the worker process itself (children are not visible without a cgroup).
        try
        {
            if (_worker is { HasExited: false } worker)
            {
                worker.Refresh();
                _peakMemory = Math.Max(_peakMemory, worker.WorkingSet64);
                return new JobUsage(worker.TotalProcessorTime.Ticks, worker.WorkingSet64, _peakMemory, 1);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        return new JobUsage(0, 0, _peakMemory, 0);
    }

    public void Dispose()
    {
        // Kill-on-close: nothing may outlive the job (an engine left running would keep using a grant).
        try
        {
            if (_worker is { HasExited: false })
            {
                _worker.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        if (_cgroup is null)
        {
            return;
        }

        if (!TryWrite("cgroup.kill", "1"))
        {
            foreach (var line in ReadLines("cgroup.procs"))
            {
                if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        p.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(_cgroup);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100); // processes still exiting
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// cgroup v2 forbids processes in a cgroup whose children use controllers ("no internal
    /// processes"). So, once per agent process: move the agent itself into "{unit}/core", then enable
    /// cpu + memory for the unit's children. Job cgroups are created next to "core".
    /// </summary>
    internal static string? EnsureDelegatedRoot(string cgroupMount, ILogger logger)
    {
        lock (SetupLock)
        {
            if (_setupAttempted)
            {
                return _delegatedRoot;
            }

            _setupAttempted = true;
            try
            {
                if (!File.Exists(Path.Combine(cgroupMount, "cgroup.controllers")))
                {
                    logger.LogInformation("cgroup v2 is not available; backup jobs run at low priority without hard CPU/memory caps.");
                    return null;
                }

                var own = File.ReadAllLines("/proc/self/cgroup").FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal));
                if (own is null)
                {
                    return null;
                }

                var relative = own[3..].Trim('/');
                if (relative.EndsWith("/core", StringComparison.Ordinal) || relative == "core")
                {
                    relative = relative.Length == 4 ? "" : relative[..^5];
                }

                if (relative.Length == 0)
                {
                    // Running in the root cgroup (e.g. started by hand): do not touch the machine's root.
                    logger.LogInformation("The agent is not running inside its own service cgroup; hard CPU/memory caps are disabled.");
                    return null;
                }

                var unit = Path.Combine(cgroupMount, relative);
                var core = Path.Combine(unit, "core");
                Directory.CreateDirectory(core);
                File.WriteAllText(Path.Combine(core, "cgroup.procs"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

                var enabled = new List<string>();
                foreach (var controller in new[] { "cpu", "memory" })
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(unit, "cgroup.subtree_control"), "+" + controller);
                        enabled.Add(controller);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning("cgroup controller '{Controller}' could not be delegated: {Error}", controller, ex.Message);
                    }
                }

                if (enabled.Count == 0)
                {
                    return null;
                }

                logger.LogInformation("Backup jobs are capped by cgroup v2 ({Controllers}) under {Unit}.", string.Join(", ", enabled), unit);
                _delegatedRoot = unit;
                return unit;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("cgroup v2 setup failed ({Error}); backup jobs run at low priority without hard caps.", ex.Message);
                return null;
            }
        }
    }

    private bool TryWrite(string file, string value)
    {
        try
        {
            File.WriteAllText(Path.Combine(_cgroup!, file), value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("cgroup write {File}={Value} failed: {Error}", file, value, ex.Message);
            return false;
        }
    }

    private string[] ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(Path.Combine(_cgroup!, file)).Where(l => l.Length > 0).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private long? ReadLong(string file)
    {
        var line = ReadLines(file).FirstOrDefault();
        return long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private long? ReadKeyed(string file, string key)
    {
        foreach (var line in ReadLines(file))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && parts[0] == key && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }
}
