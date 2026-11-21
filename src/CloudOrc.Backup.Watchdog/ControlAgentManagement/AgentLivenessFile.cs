using System.Text.Json;

namespace CloudOrc.Backup.Watchdog.ControlAgentManagement;

/// <summary>
/// Reads the agent's <c>agent-liveness.json</c> (written every 15 s by its core liveness loop).
/// A fresh file proves the agent is alive even when the health pipe cannot be reached, so the
/// watchdog does not restart it (which would kill a running backup).
/// </summary>
public static class AgentLivenessFile
{
    public const string FileName = "agent-liveness.json";

    /// <summary>True when the file exists and its timestamp is younger than <paramref name="maxAge"/>.</summary>
    public static bool IsFresh(string path, TimeSpan maxAge, DateTimeOffset? now = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("at", out var at) || !at.TryGetDateTimeOffset(out var written))
            {
                return false;
            }

            var age = (now ?? DateTimeOffset.UtcNow) - written;
            return age >= TimeSpan.FromSeconds(-30) && age <= maxAge;
        }
        catch
        {
            return false;
        }
    }
}
