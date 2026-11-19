using CloudOrc.Backup.Agent.Platform;

namespace CloudOrc.Backup.Agent.Configuration;

/// <summary>
/// Root configuration section ("Agent" in appsettings.json) for the Backup Agent core.
/// (The class keeps its historical name so the shared code ported from the Server Agent stays
/// recognisable.)
/// </summary>
public sealed class ControlAgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Root data directory (state, keys, logs, caches). Defaults per operating system.</summary>
    public string DataDirectory { get; set; } = AgentPlatform.DefaultDataDirectory;

    /// <summary>
    /// Require a backend signature on every typed command even before a signing key was delivered
    /// at enrollment. Signatures are enforced automatically once the key is known.
    /// </summary>
    public bool RequireSignedTypedCommands { get; set; }

    /// <summary>Restrict the data directory to SYSTEM + Administrators (Windows) / root 0700 (Unix) at startup.</summary>
    public bool HardenDataDirectoryAcl { get; set; } = true;

    /// <summary>Local IPC name the Backup Watchdog checks health on.</summary>
    public string HealthPipeName { get; set; } = AgentPlatform.HealthPipeName;

    /// <summary>
    /// If a core loop has not recorded activity within this many seconds, the health snapshot
    /// reports it as not alive even though the process is still running.
    /// </summary>
    public int WorkerHeartbeatTimeoutSeconds { get; set; } = 30;

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
}
