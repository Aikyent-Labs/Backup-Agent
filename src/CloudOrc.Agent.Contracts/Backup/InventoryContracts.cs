using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// "Analyse this machine": what the Backup Agent reports after activation and whenever the portal
/// asks (inventory.refresh). Lets the portal show the device, recommend what to protect and size the
/// first backup against the customer's storage quota. Contains no file contents and no secrets.
/// Sent as the data of an "agent.inventory" lifecycle event and as the inventory.refresh result.
/// </summary>
public sealed class MachineInventory
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "1.0";
    [JsonPropertyName("collectedAt")] public required DateTimeOffset CollectedAt { get; init; }
    [JsonPropertyName("agentKind")] public string AgentKind { get; init; } = "backup";
    [JsonPropertyName("agentVersion")] public required string AgentVersion { get; init; }
    [JsonPropertyName("os")] public required InventoryOs Os { get; init; }
    [JsonPropertyName("hardware")] public required InventoryHardware Hardware { get; init; }
    [JsonPropertyName("volumes")] public IReadOnlyList<InventoryVolume> Volumes { get; init; } = [];
    [JsonPropertyName("databases")] public IReadOnlyList<InventoryDatabase> Databases { get; init; } = [];
    [JsonPropertyName("applications")] public IReadOnlyList<InventoryApplication> Applications { get; init; } = [];

    /// <summary>Folders worth protecting on this machine (portal pre-selects them as suggestions).</summary>
    [JsonPropertyName("suggestedFolders")] public IReadOnlyList<InventorySuggestedFolder> SuggestedFolders { get; init; } = [];

    /// <summary>What this agent build can back up on this OS ("files", "postgresql", "tally").</summary>
    [JsonPropertyName("supportedWorkloads")] public IReadOnlyList<string> SupportedWorkloads { get; init; } = [];

    /// <summary>"vss" on Windows; empty where files are read live.</summary>
    [JsonPropertyName("snapshotTechnologies")] public IReadOnlyList<string> SnapshotTechnologies { get; init; } = [];
}

public sealed class InventoryOs
{
    [JsonPropertyName("family")] public required string Family { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("architecture")] public required string Architecture { get; init; }
    [JsonPropertyName("hostname")] public required string Hostname { get; init; }
    [JsonPropertyName("timeZone")] public string? TimeZone { get; init; }
    [JsonPropertyName("uptimeSeconds")] public long? UptimeSeconds { get; init; }
    [JsonPropertyName("isServer")] public bool? IsServer { get; init; }
}

public sealed class InventoryHardware
{
    [JsonPropertyName("cpuModel")] public string? CpuModel { get; init; }
    [JsonPropertyName("cpuCores")] public int CpuCores { get; init; }
    [JsonPropertyName("memoryTotalBytes")] public long? MemoryTotalBytes { get; init; }
    [JsonPropertyName("memoryAvailableBytes")] public long? MemoryAvailableBytes { get; init; }
    [JsonPropertyName("isVirtualMachine")] public bool? IsVirtualMachine { get; init; }
}

public sealed class InventoryVolume
{
    [JsonPropertyName("mountPoint")] public required string MountPoint { get; init; }
    [JsonPropertyName("fileSystem")] public string? FileSystem { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("freeBytes")] public long FreeBytes { get; init; }
    [JsonPropertyName("isSystem")] public bool IsSystem { get; init; }
}

public sealed class InventoryDatabase
{
    /// <summary>"postgresql", "mysql", "mssql", "mongodb".</summary>
    [JsonPropertyName("engine")] public required string Engine { get; init; }
    [JsonPropertyName("host")] public string Host { get; init; } = "127.0.0.1";
    [JsonPropertyName("port")] public int? Port { get; init; }
    [JsonPropertyName("listening")] public bool Listening { get; init; }

    /// <summary>Client tool version found on this machine (e.g. pg_dump 16.4).</summary>
    [JsonPropertyName("clientVersion")] public string? ClientVersion { get; init; }

    /// <summary>True when this agent build can back it up (PostgreSQL today; others are detected only).</summary>
    [JsonPropertyName("backupSupported")] public bool BackupSupported { get; init; }

    /// <summary>Linux: local "peer" login as the postgres OS user is possible (no password needed).</summary>
    [JsonPropertyName("localPeerAuth")] public bool? LocalPeerAuth { get; init; }
}

public sealed class InventoryApplication
{
    /// <summary>"tally", "iis", "nginx", "apache".</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("running")] public bool? Running { get; init; }
}

public sealed class InventorySuggestedFolder
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }

    /// <summary>"folder" or "tally".</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = BackupSourceKinds.Folder;
}

public static class WorkloadNames
{
    public const string Files = "files";
    public const string PostgreSql = "postgresql";
    public const string Tally = "tally";
}
