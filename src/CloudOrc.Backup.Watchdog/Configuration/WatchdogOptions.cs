namespace CloudOrc.Backup.Watchdog.Configuration;

/// <summary>
/// "Watchdog" section of appsettings.json for the CloudOrc Backup Watchdog. It watches ONE thing:
/// the Backup Agent service on the same machine (Windows Service, systemd unit or launchd job).
/// Every default is correct for the installed product on each OS.
/// </summary>
public sealed class WatchdogOptions
{
    public const string SectionName = "Watchdog";

    /// <summary>Root data directory for the watchdog's own logs and event file.</summary>
    public string DataDirectory { get; set; } =
        OperatingSystem.IsWindows() ? @"C:\ProgramData\CloudOrc\Backup\watchdog"
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/CloudOrcBackup/watchdog"
        : "/var/lib/cloudorc-backup/watchdog";

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Recovery events (restarts) for the agent to forward to the portal. Lives in the AGENT's data
    /// directory root so the agent picks it up on its next start.
    /// </summary>
    public string EventsFile => Path.Combine(Path.GetDirectoryName(DataDirectory.TrimEnd(Path.DirectorySeparatorChar))!, "watchdog-events.jsonl");

    /// <summary>The monitored service: Windows service name, systemd unit or launchd label (per OS).</summary>
    public string ControlAgentServiceName { get; set; } =
        OperatingSystem.IsWindows() ? "CloudOrcBackupAgent"
        : OperatingSystem.IsMacOS() ? "com.cloudorc.backup.agent"
        : "cloudorc-backup-agent.service";

    /// <summary>Process name of the agent (for CPU/memory sampling).</summary>
    public string ControlAgentProcessName { get; set; } = "cloudorc-backup";

    /// <summary>Local IPC name the agent's health server listens on.</summary>
    public string HealthPipeName { get; set; } = "CloudOrc.Backup.Agent.Health";

    public int HealthCheckIntervalSeconds { get; set; } = 10;

    public int HealthCheckTimeoutSeconds { get; set; } = 5;

    /// <summary>Consecutive failed checks before a restart. A single transient failure never restarts.</summary>
    public int ConsecutiveFailureThreshold { get; set; } = 3;

    public int RecoveryWaitSeconds { get; set; } = 15;

    public int MaxRestartAttemptsPerWindow { get; set; } = 3;

    public int RestartRateLimitWindowMinutes { get; set; } = 15;

    public int InitialBackoffSeconds { get; set; } = 30;

    public int MaxBackoffSeconds { get; set; } = 600;

    /// <summary>Restart the agent when its working set stays above this many MB (leak guard); 0 = off.</summary>
    public int MaxAgentWorkingSetMb { get; set; } = 1024;

    /// <summary>Consecutive samples above <see cref="MaxAgentWorkingSetMb"/> before a restart.</summary>
    public int MemoryBreachSamples { get; set; } = 30;
}
