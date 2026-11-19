using System.Diagnostics;
using System.Globalization;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.Backup.Agent.Platform;
using CloudOrc.Backup.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudOrc.Backup.Agent.Discovery;

/// <summary>
/// "Analyse this machine" (Acronis-style device discovery): OS, hardware, disks, databases and
/// applications, plus folders worth protecting. Read-only, bounded (a few seconds), never reads file
/// contents and never needs credentials. Every probe is optional - one failing probe only leaves its
/// part empty.
/// </summary>
public sealed class MachineInventoryCollector(ILogger<MachineInventoryCollector> logger)
{
    private static readonly string[] UnixSkipMounts = ["/proc", "/sys", "/dev", "/run", "/snap", "/var/lib/docker", "/System/Volumes", "/boot"];

    public MachineInventory Collect()
    {
        var volumes = SafeProbe("volumes", CollectVolumes, []);
        var databases = SafeProbe("databases", CollectDatabases, []);
        var applications = SafeProbe("applications", CollectApplications, []);

        var workloads = new List<string> { WorkloadNames.Files };
        if (databases.Any(d => d.Engine == WorkloadNames.PostgreSql && d.BackupSupported))
        {
            workloads.Add(WorkloadNames.PostgreSql);
        }

        if (OperatingSystem.IsWindows())
        {
            workloads.Add(WorkloadNames.Tally);
        }

        return new MachineInventory
        {
            CollectedAt = DateTimeOffset.UtcNow,
            AgentVersion = AgentVersionInfo.GetVersion(typeof(MachineInventoryCollector).Assembly),
            Os = SafeProbe("os", CollectOs, new InventoryOs
            {
                Family = AgentPlatform.OsName,
                Description = AgentIdentityInfo.OsDescription,
                Architecture = AgentIdentityInfo.Architecture,
                Hostname = Environment.MachineName
            }),
            Hardware = SafeProbe("hardware", CollectHardware, new InventoryHardware { CpuCores = Environment.ProcessorCount }),
            Volumes = volumes,
            Databases = databases,
            Applications = applications,
            SuggestedFolders = SafeProbe("suggestions", () => SuggestFolders(applications), []),
            SupportedWorkloads = workloads,
            SnapshotTechnologies = OperatingSystem.IsWindows() ? ["vss"] : []
        };
    }

    private T SafeProbe<T>(string name, Func<T> probe, T fallback)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Inventory probe '{Probe}' failed.", name);
            return fallback;
        }
    }

    // ------------------------------------------------------------------ OS / hardware

    private static InventoryOs CollectOs()
    {
        string? version = null;
        bool? isServer = null;
        if (OperatingSystem.IsLinux() && File.Exists("/etc/os-release"))
        {
            var fields = ReadKeyValues("/etc/os-release");
            version = fields.GetValueOrDefault("VERSION_ID");
        }
        else if (OperatingSystem.IsWindows())
        {
            version = Environment.OSVersion.Version.ToString();
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            isServer = (key?.GetValue("InstallationType") as string)?.Contains("Server", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            version = Environment.OSVersion.Version.ToString();
        }

        string description = AgentIdentityInfo.OsDescription;
        if (OperatingSystem.IsLinux() && File.Exists("/etc/os-release"))
        {
            description = ReadKeyValues("/etc/os-release").GetValueOrDefault("PRETTY_NAME") ?? description;
        }

        return new InventoryOs
        {
            Family = AgentPlatform.OsName,
            Description = description,
            Version = version,
            Architecture = AgentIdentityInfo.Architecture,
            Hostname = Environment.MachineName,
            TimeZone = TimeZoneInfo.Local.Id,
            UptimeSeconds = Environment.TickCount64 / 1000,
            IsServer = isServer ?? (OperatingSystem.IsLinux() ? !File.Exists("/usr/bin/gnome-shell") && !File.Exists("/usr/bin/plasmashell") : null)
        };
    }

    private static InventoryHardware CollectHardware()
    {
        string? cpu = null;
        long? total = null, available = null;
        bool? vm = null;

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = $"{bios?.GetValue("SystemManufacturer")} {bios?.GetValue("SystemProductName")}";
            vm = LooksVirtual(maker);
            total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        else if (OperatingSystem.IsLinux())
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
                vm = File.ReadLines("/proc/cpuinfo").Any(l => l.StartsWith("flags", StringComparison.Ordinal) && l.Contains(" hypervisor", StringComparison.Ordinal));
            }

            if (File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        total = ParseKb(line);
                    }
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        available = ParseKb(line);
                    }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            cpu = RunTool("/usr/sbin/sysctl", ["-n", "machdep.cpu.brand_string"])?.Trim();
            if (long.TryParse(RunTool("/usr/sbin/sysctl", ["-n", "hw.memsize"])?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mem))
            {
                total = mem;
            }

            vm = RunTool("/usr/sbin/sysctl", ["-n", "kern.hv_vmm_present"])?.Trim() == "1";
        }

        return new InventoryHardware
        {
            CpuModel = string.IsNullOrWhiteSpace(cpu) ? null : cpu,
            CpuCores = Environment.ProcessorCount,
            MemoryTotalBytes = total,
            MemoryAvailableBytes = available,
            IsVirtualMachine = vm
        };

        static long? ParseKb(string line) =>
            long.TryParse(line.Split(':', 2)[1].Trim().Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
    }

    private static bool LooksVirtual(string text) =>
        new[] { "VMware", "VirtualBox", "KVM", "QEMU", "Virtual Machine", "Xen", "Hyper-V", "Amazon EC2", "Google Compute", "OpenStack" }
            .Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ volumes

    private static List<InventoryVolume> CollectVolumes()
    {
        var systemRoot = OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory) : "/";
        var result = new List<InventoryVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var mount = drive.RootDirectory.FullName;
                if (!OperatingSystem.IsWindows() && UnixSkipMounts.Any(s => mount == s || mount.StartsWith(s + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                result.Add(new InventoryVolume
                {
                    MountPoint = mount,
                    FileSystem = drive.DriveFormat,
                    Label = OperatingSystem.IsWindows() ? NullIfEmpty(drive.VolumeLabel) : null,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsSystem = string.Equals(mount, systemRoot, StringComparison.OrdinalIgnoreCase)
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ databases

    private static List<InventoryDatabase> CollectDatabases()
    {
        var result = new List<InventoryDatabase>();
        var pg = PostgresLocator.FindNewest();
        var pgListening = PostgresLocator.IsListening("127.0.0.1", 5432, TimeSpan.FromSeconds(1));
        if (pg is not null || pgListening || ProcessRunning("postgres"))
        {
            result.Add(new InventoryDatabase
            {
                Engine = WorkloadNames.PostgreSql,
                Port = 5432,
                Listening = pgListening,
                ClientVersion = pg?.Version.ToString(),
                BackupSupported = pg is not null,
                LocalPeerAuth = OperatingSystem.IsWindows() ? null : PostgresLocator.PostgresOsUserExists()
            });
        }

        // Detected only (backup support comes later) - shown in the portal as "not yet supported".
        if (PostgresLocator.IsListening("127.0.0.1", 3306, TimeSpan.FromMilliseconds(500)) || ProcessRunning("mysqld") || ProcessRunning("mariadbd"))
        {
            result.Add(new InventoryDatabase { Engine = "mysql", Port = 3306, Listening = true, BackupSupported = false });
        }

        if (OperatingSystem.IsWindows() && ProcessRunning("sqlservr"))
        {
            result.Add(new InventoryDatabase { Engine = "mssql", Port = 1433, Listening = PostgresLocator.IsListening("127.0.0.1", 1433, TimeSpan.FromMilliseconds(500)), BackupSupported = false });
        }

        if (ProcessRunning("mongod"))
        {
            result.Add(new InventoryDatabase { Engine = "mongodb", Port = 27017, Listening = true, BackupSupported = false });
        }

        return result;
    }

    // ------------------------------------------------------------------ applications

    private static List<InventoryApplication> CollectApplications()
    {
        var apps = new List<InventoryApplication>();
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var candidate in new[] { "TallyPrime", "Tally.ERP9", "Tally" })
            {
                var path = Path.Combine(programFiles, candidate);
                if (Directory.Exists(path))
                {
                    apps.Add(new InventoryApplication { Name = "tally", Path = path, Running = ProcessRunning("tally") });
                    break;
                }
            }

            if (Directory.Exists(@"C:\inetpub\wwwroot"))
            {
                apps.Add(new InventoryApplication { Name = "iis", Path = @"C:\inetpub\wwwroot", Running = ProcessRunning("w3wp") });
            }
        }
        else
        {
            if (Directory.Exists("/etc/nginx"))
            {
                apps.Add(new InventoryApplication { Name = "nginx", Path = "/etc/nginx", Running = ProcessRunning("nginx") });
            }

            foreach (var apache in new[] { "/etc/apache2", "/etc/httpd" })
            {
                if (Directory.Exists(apache))
                {
                    apps.Add(new InventoryApplication { Name = "apache", Path = apache, Running = ProcessRunning("apache2") || ProcessRunning("httpd") });
                    break;
                }
            }
        }

        return apps;
    }

    // ------------------------------------------------------------------ suggestions

    private static List<InventorySuggestedFolder> SuggestFolders(IReadOnlyList<InventoryApplication> applications)
    {
        var result = new List<InventorySuggestedFolder>();
        void Add(string path, string reason, string kind = BackupSourceKinds.Folder)
        {
            if (Directory.Exists(path) && result.All(r => r.Path != path))
            {
                result.Add(new InventorySuggestedFolder { Path = path, Reason = reason, Kind = kind });
            }
        }

        if (OperatingSystem.IsWindows())
        {
            Add(Path.GetPathRoot(Environment.SystemDirectory) + "Users", "User documents, desktops and profiles");
            foreach (var tally in applications.Where(a => a.Name == "tally" && a.Path is not null))
            {
                Add(Path.Combine(tally.Path!, "Data"), "Tally company data", BackupSourceKinds.Tally);
            }

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)))
            {
                Add(drive.RootDirectory.FullName, "Data drive");
            }

            Add(@"C:\inetpub\wwwroot", "Web sites (IIS)");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Add("/Users", "User documents, desktops and profiles");
        }
        else
        {
            Add("/home", "User home folders");
            Add("/var/www", "Web sites");
            Add("/srv", "Service data");
            Add("/etc", "System and application configuration");
            Add("/opt", "Installed applications and their data");
            Add("/root", "Administrator home folder");
        }

        return result;
    }

    // ------------------------------------------------------------------ helpers

    private static bool ProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static Dictionary<string, string> ReadKeyValues(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var i = line.IndexOf('=');
            if (i > 0)
            {
                result[line[..i]] = line[(i + 1)..].Trim().Trim('"');
            }
        }

        return result;
    }

    private static string? RunTool(string tool, string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(5000) ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
