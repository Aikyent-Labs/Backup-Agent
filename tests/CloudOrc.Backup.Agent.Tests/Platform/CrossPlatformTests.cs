using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Backup.Agent.Backup;
using CloudOrc.Backup.Agent.Commands;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Discovery;
using CloudOrc.Backup.Agent.DurableQueue;
using CloudOrc.Backup.Agent.Platform;
using CloudOrc.Backup.Agent.ResourceGovernor;
using CloudOrc.Backup.Agent.Services;
using CloudOrc.Backup.Agent.Transport;
using CloudOrc.Backup.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.Backup.Agent.Tests.Platform;

public class WorkerLaunchTests
{
    [Fact]
    public void Windows_OrNormalPriority_StartsTheWorkerDirectly()
    {
        var plan = WorkerLaunch.Build("/opt/cloudorc-backup/worker/cloudorc-backup-worker", JobPriority.Normal);
        Assert.Equal("/opt/cloudorc-backup/worker/cloudorc-backup-worker", plan.FileName);
        Assert.Empty(plan.PrefixArguments);
    }

    [UnixFact]
    public void Unix_LowPriority_UsesTheOsToolsWhenPresent_AndKeepsTheWorkerLast()
    {
        var dir = Path.Combine(Path.GetTempPath(), "launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var nice = Path.Combine(dir, "nice");
            var ionice = Path.Combine(dir, "ionice");
            var taskpolicy = Path.Combine(dir, "taskpolicy");
            File.WriteAllText(nice, "");
            File.WriteAllText(ionice, "");
            File.WriteAllText(taskpolicy, "");
            WorkerLaunch.NicePaths = [nice];
            WorkerLaunch.IonicePaths = [ionice];
            WorkerLaunch.TaskpolicyPaths = [taskpolicy];

            var plan = WorkerLaunch.Build("/w", JobPriority.BelowNormal);
            if (OperatingSystem.IsMacOS())
            {
                Assert.Equal(taskpolicy, plan.FileName);
                Assert.Equal(["-b", "/w"], plan.PrefixArguments);
            }
            else
            {
                Assert.Equal(nice, plan.FileName);
                Assert.Equal(["-n", "10", ionice, "-c", "2", "-n", "7", "/w"], plan.PrefixArguments);
                var idle = WorkerLaunch.Build("/w", JobPriority.Idle);
                Assert.Equal(["-n", "19", ionice, "-c", "3", "/w"], idle.PrefixArguments);
            }

            var start = new System.Diagnostics.ProcessStartInfo("/w");
            start.ArgumentList.Add("--job-id");
            start.ArgumentList.Add("j1");
            WorkerLaunch.Apply(start, plan);
            Assert.Equal("j1", start.ArgumentList[^1]);
            Assert.Equal("/w", start.ArgumentList[^3]);
        }
        finally
        {
            WorkerLaunch.NicePaths = ["/usr/bin/nice", "/bin/nice"];
            WorkerLaunch.IonicePaths = ["/usr/bin/ionice", "/bin/ionice"];
            WorkerLaunch.TaskpolicyPaths = ["/usr/sbin/taskpolicy"];
            Directory.Delete(dir, true);
        }
    }
}

public class UnixPressureMonitorTests
{
    [Theory]
    [InlineData("sda", true)]
    [InlineData("sda1", false)]
    [InlineData("vdb", true)]
    [InlineData("xvda", true)]
    [InlineData("nvme0n1", true)]
    [InlineData("nvme0n1p2", false)]
    [InlineData("mmcblk0", true)]
    [InlineData("mmcblk0p1", false)]
    [InlineData("loop3", false)]
    [InlineData("dm-0", false)]
    public void WholeDiskDetection(string name, bool expected) => Assert.Equal(expected, UnixPressureMonitor.IsWholeDisk(name));

    [Fact]
    public void ReadsCpuDiskAndMemory_FromProcFiles()
    {
        var proc = Path.Combine(Path.GetTempPath(), "proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(proc);
        try
        {
            File.WriteAllText(Path.Combine(proc, "stat"), "cpu  100 0 100 800 0 0 0 0 0 0\n");
            File.WriteAllText(Path.Combine(proc, "diskstats"), "   8       0 sda 100 0 0 200 100 0 0 300 0 0 0\n   8       1 sda1 5 0 0 5 5 0 0 5 0 0 0\n");
            File.WriteAllText(Path.Combine(proc, "meminfo"), "MemTotal:  4000000 kB\nMemAvailable:  1048576 kB\n");
            var monitor = new UnixPressureMonitor(new ResourceGovernorOptions { ProtectedProcessNames = [] }, proc);

            File.WriteAllText(Path.Combine(proc, "stat"), "cpu  150 0 150 900 0 0 0 0 0 0\n"); // +100 busy of +200
            File.WriteAllText(Path.Combine(proc, "diskstats"), "   8       0 sda 150 0 0 400 150 0 0 600 0 0 0\n"); // +100 ios, +500 ms

            Assert.Equal(50.0, monitor.CpuPercent());
            Assert.Equal(5.0, monitor.DiskLatencyMs());
            Assert.Equal(1024.0, monitor.AvailableMemoryMb());
        }
        finally
        {
            Directory.Delete(proc, true);
        }
    }
}

public class PolicyPathRulesTests
{
    [WindowsTheory]
    [InlineData(@"D:\Tally\Data", true)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"C:\a\..\b", false)]
    [InlineData("/home/user", false)]
    public void Windows_LocalDrivePathsOnly(string path, bool allowed) => Assert.Equal(allowed, BackupPolicyValidator.IsAllowedLocalPath(path));

    [UnixFact]
    public void Unix_AbsolutePaths_ButNeverPseudoFileSystems()
    {
        Assert.True(BackupPolicyValidator.IsAllowedLocalPath("/home/user/docs"));
        Assert.True(BackupPolicyValidator.IsAllowedLocalPath("/var/www"));
        Assert.False(BackupPolicyValidator.IsAllowedLocalPath("relative/path"));
        Assert.False(BackupPolicyValidator.IsAllowedLocalPath("/home/../etc"));
        Assert.False(BackupPolicyValidator.IsAllowedLocalPath("/proc/1"));
        Assert.False(BackupPolicyValidator.IsAllowedLocalPath("/sys"));
        Assert.False(BackupPolicyValidator.IsAllowedLocalPath("/dev/sda"));
    }

    [UnixFact]
    public void Unix_FolderPicker_RefusesPseudoFileSystems_AndListsRoot()
    {
        Assert.True(DiscoveryBrowseHandler.TryNormalizeLocalPath("/var/www/", out var normalized));
        Assert.Equal("/var/www", normalized);
        Assert.False(DiscoveryBrowseHandler.TryNormalizeLocalPath("/proc/self", out _));
        Assert.False(DiscoveryBrowseHandler.TryNormalizeLocalPath("C:\\Windows", out _));
        Assert.Contains(DiscoveryBrowseHandler.LocalRoots(), r => r.Path == "/");
        Assert.True(DiscoveryBrowseHandler.IsSameOrUnder("/data/a", "/data"));
        Assert.False(DiscoveryBrowseHandler.IsSameOrUnder("/database", "/data"));
    }
}

public class DatabasePolicyTests
{
    private static BackupPolicyDocument Policy(BackupSource source) => new()
    {
        PolicyId = "p1",
        PolicyVersionId = "v1",
        EffectiveHash = "h",
        Sources = [source],
        Schedule = new BackupSchedule(),
        Repository = new BackupRepositoryRef { RepositoryId = "r1", StoragePoolId = "pool" }
    };

    [Fact]
    public void PostgresSource_IsValidatedByItsConnection_NotByAFolderPath()
    {
        var ok = BackupPolicyValidator.Validate(Policy(new BackupSource
        {
            Path = "postgresql://127.0.0.1:5432",
            Kind = BackupSourceKinds.PostgreSql,
            Database = new DatabaseSourceSpec { Databases = ["app_db", "crm-prod"] }
        }));
        Assert.Empty(ok);

        var bad = BackupPolicyValidator.Validate(Policy(new BackupSource
        {
            Path = "postgresql://127.0.0.1:5432",
            Kind = BackupSourceKinds.PostgreSql,
            Database = new DatabaseSourceSpec { Databases = ["x; DROP TABLE y"], Port = 70000 }
        }));
        Assert.Contains(bad, e => e.Contains("database names", StringComparison.Ordinal));
        Assert.Contains(bad, e => e.Contains("database.port", StringComparison.Ordinal));

        var missing = BackupPolicyValidator.Validate(Policy(new BackupSource { Path = "postgresql://x", Kind = BackupSourceKinds.PostgreSql }));
        Assert.Contains(missing, e => e.Contains("no database connection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pg_dump (PostgreSQL) 16.4", 16, 4)]
    [InlineData("pg_dump (PostgreSQL) 17.0 (Ubuntu 17.0-1.pgdg24.04+1)", 17, 0)]
    [InlineData("pg_dump (PostgreSQL) 12", 12, 0)]
    public void PgDumpVersionParsing(string text, int major, int minor) =>
        Assert.Equal(new Version(major, minor), PostgresLocator.ParseVersion(text));

    [Fact]
    public void PgDumpVersionParsing_Garbage_IsNull() => Assert.Null(PostgresLocator.ParseVersion("command not found"));
}

public class SecureFileTests
{
    [UnixFact]
    public void Unix_SecretsAreOwnerOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "secure-" + Guid.NewGuid().ToString("N"), "keys");
        try
        {
            SecureFile.WriteAllText(Path.Combine(dir, "secret.txt"), "s3cr3t");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "secret.txt")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            Assert.Equal("s3cr3t", File.ReadAllText(Path.Combine(dir, "secret.txt")));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, true);
        }
    }

    [UnixFact]
    public void Unix_DeviceKey_IsCreatedOnce_AndSignsVerifiably()
    {
        var dir = Path.Combine(Path.GetTempPath(), "devkey-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DeviceKeyStore(keyDirectory: dir);
            var spki = store.GetPublicKeySpki();
            Assert.Equal(spki, new DeviceKeyStore(keyDirectory: dir).GetPublicKeySpki());

            var signature = Convert.FromBase64String(store.Sign("payload"));
            using var verifier = System.Security.Cryptography.ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            Assert.True(verifier.VerifyData("payload"u8, signature, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
            Assert.Contains("BEGIN CERTIFICATE REQUEST", store.CreateCsrPem("host-1"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class AgentPlatformTests
{
    [Fact]
    public void Defaults_MatchTheInstalledLayout()
    {
        Assert.Equal("backup", AgentIdentityInfo.AgentKind);
        Assert.Contains(AgentPlatform.OsName, new[] { "windows", "linux", "macos" });
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\ProgramData\CloudOrc\Backup", AgentPlatform.DefaultDataDirectory);
            Assert.Equal(".exe", AgentPlatform.ExeSuffix);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/var/lib/cloudorc-backup", AgentPlatform.DefaultDataDirectory);
            Assert.Equal("/opt/cloudorc-backup", AgentPlatform.DefaultInstallRoot);
        }
    }
}

public sealed class WatchdogEventForwarderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdfwd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void WatchdogEvents_AreQueuedOnce_AndTheFileIsCleared()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, "watchdog-events.jsonl"),
        [
            """{"eventId":"e1","eventType":"agent.watchdog.recovered","occurredAt":"2026-11-19T10:00:00Z","message":"restarted"}""",
            """{"eventId":"e2","eventType":"not.a.watchdog.event","message":"ignored"}""",
            "garbage"
        ]);

        using var db = new AgentStateDatabase(Path.Combine(_dir, "state.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
        var outbox = new OutboxStore(db);
        var identity = new AgentIdentity { AgentId = "agent-1", ServerId = "srv", MachineId = "m", MachineName = "n", AgentVersion = "1.0.0" };
        var dispatcher = new OutboxDispatcherService(outbox, null!, new BackendLinkState(), TimeProvider.System, NullLogger<OutboxDispatcherService>.Instance);
        var forwarder = new WatchdogEventForwarder(
            Options.Create(new ControlAgentOptions { DataDirectory = _dir }), outbox, dispatcher, identity, TimeProvider.System, NullLogger<WatchdogEventForwarder>.Instance);

        Assert.Equal(1, forwarder.Forward());
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(_dir, "watchdog-events.jsonl")));
        Assert.Equal(0, forwarder.Forward());
    }
}
