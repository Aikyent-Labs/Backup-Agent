using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Transport;
using Microsoft.Extensions.Options;

namespace CloudOrc.Backup.Agent.Backup;

/// <summary>
/// HELLO capability profile (§3 "capability profiles"). "backup.v1" is only advertised when the
/// worker AND at least one engine are actually installed, so the backend never sends a backup
/// command this endpoint cannot execute.
/// </summary>
public sealed class AgentCapabilitiesProvider : IAgentCapabilitiesProvider
{
    public AgentCapabilitiesProvider(IOptions<BackupOptions> options, BackupPaths paths)
    {
        var caps = new List<string>
        {
            AgentCapabilities.DurableEventsV1,
            AgentCapabilities.ActivationCodeV1,
            AgentCapabilities.SessionRenewV1,
            AgentCapabilities.InventoryV1,
            AgentCapabilities.BackupSchemesV2,
            AgentCapabilities.BackupSchedulesV2,
            AgentCapabilities.StandaloneBackupAgent
        };

        if (File.Exists(Path.Combine(paths.UpdaterDirectory, "cloudorc-backup-updater" + Platform.AgentPlatform.ExeSuffix)))
        {
            caps.Add(AgentCapabilities.SignedUpdateV1);
        }

        if (options.Value.Enabled)
        {
            // discovery.browse is registered with the backup handlers (folder picker).
            caps.Add(AgentCapabilities.DiscoveryV1);
        }

        EngineVersions = ReadEngineVersions(paths.EngineDirectory);
        if (options.Value.Enabled && File.Exists(paths.WorkerExecutable) && EngineVersions is { Count: > 0 })
        {
            caps.Add(AgentCapabilities.BackupV1);
            caps.Add(AgentCapabilities.RestoreV1);
        }

        if (options.Value.Enabled && caps.Contains(AgentCapabilities.BackupV1) && Shared.PostgresLocator.FindNewest() is not null)
        {
            // pg_dump is installed: PostgreSQL databases can be protected on this machine.
            caps.Add(AgentCapabilities.PostgreSqlV1);
        }

        Capabilities = caps;
    }

    public IReadOnlyList<string> Capabilities { get; }

    public IReadOnlyDictionary<string, string>? EngineVersions { get; }

    /// <summary>
    /// engines.manifest.json (shipped by the signed installer) - pins every engine binary by
    /// SHA-256 so the worker can fail closed on a tampered binary (§15).
    /// <code>{"engines":[{"name":"kopia","version":"0.21.1","file":"kopia.exe","sha256":"..."}]}</code>
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadEngineVersions(string engineDirectory)
    {
        var manifest = Path.Combine(engineDirectory, "engines.manifest.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var engine in doc.RootElement.GetProperty("engines").EnumerateArray())
            {
                var name = engine.GetProperty("name").GetString();
                var version = engine.TryGetProperty("version", out var v) ? v.GetString() : null;
                var file = engine.TryGetProperty("file", out var f) ? f.GetString() : null;
                if (name is BackupEngineNames.Kopia
                    && version is not null && file is not null && File.Exists(Path.Combine(engineDirectory, file)))
                {
                    result[name] = version;
                }
            }

            return result.ContainsKey(BackupEngineNames.Kopia) ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
