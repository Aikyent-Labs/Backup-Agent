namespace CloudOrc.Backup.Agent.Platform;

/// <summary>
/// Everything about where and how the Backup Agent lives on each operating system. The same
/// binaries ship to Windows, Linux and macOS; only these values differ.
/// <code>
///             install root                       data root                                   service
/// Windows     C:\Program Files\CloudOrc\Backup   C:\ProgramData\CloudOrc\Backup              CloudOrcBackupAgent (Windows Service)
/// Linux       /opt/cloudorc-backup               /var/lib/cloudorc-backup                    cloudorc-backup-agent.service (systemd)
/// macOS       /Library/CloudOrc/Backup           /Library/Application Support/CloudOrcBackup com.cloudorc.backup.agent (launchd)
///
/// {install}/bin/       cloudorc-backup[.exe]           service + CLI
///                      cloudorc-backup-worker[.exe]    per-job isolated worker
///                      cloudorc-backup-watchdog[.exe]  watchdog service
///                      cloudorc-backup-updater[.exe]   signed self-updater
///                      + ONE shared self-contained .NET runtime (keeps the download ~35 MB)
/// {install}/engines/   kopia[.exe] + engines.manifest.json
/// </code>
/// </summary>
public static class AgentPlatform
{
    public const string WindowsServiceName = "CloudOrcBackupAgent";
    public const string WindowsWatchdogServiceName = "CloudOrcBackupWatchdog";
    public const string SystemdUnit = "cloudorc-backup-agent.service";
    public const string SystemdWatchdogUnit = "cloudorc-backup-watchdog.service";
    public const string LaunchdLabel = "com.cloudorc.backup.agent";
    public const string LaunchdWatchdogLabel = "com.cloudorc.backup.watchdog";

    /// <summary>Local IPC names (named pipe on Windows, Unix domain socket elsewhere).</summary>
    public const string HealthPipeName = "CloudOrc.Backup.Agent.Health";
    public const string StatusPipeName = "CloudOrc.Backup.Agent.Status";

    public static string OsName =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown";

    public static string ExeSuffix => OperatingSystem.IsWindows() ? ".exe" : "";

    public static string DefaultDataDirectory =>
        OperatingSystem.IsWindows() ? @"C:\ProgramData\CloudOrc\Backup"
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/CloudOrcBackup"
        : "/var/lib/cloudorc-backup";

    public static string DefaultInstallRoot =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CloudOrc", "Backup")
        : OperatingSystem.IsMacOS() ? "/Library/CloudOrc/Backup"
        : "/opt/cloudorc-backup";

    /// <summary>
    /// The install root as seen from the running binary ({install}/bin/cloudorc-backup).
    /// </summary>
    public static string InstallRootFromBaseDirectory(string baseDirectory) =>
        Path.GetFullPath(Path.Combine(baseDirectory, ".."));

    public static string ServiceDisplayName => OperatingSystem.IsWindows() ? WindowsServiceName
        : OperatingSystem.IsMacOS() ? LaunchdLabel
        : SystemdUnit;

    /// <summary>True when running with full machine rights (root / LocalSystem / Administrator).</summary>
    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                   || identity.IsSystem;
        }

        return Environment.UserName == "root" || NativeUnix.GetEffectiveUserId() == 0;
    }
}
