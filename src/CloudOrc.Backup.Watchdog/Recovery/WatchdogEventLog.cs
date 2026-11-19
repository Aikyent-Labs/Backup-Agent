using System.Text.Json;
using CloudOrc.Backup.Watchdog.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudOrc.Backup.Watchdog.Recovery;

/// <summary>
/// Appends recovery events (restart attempted / succeeded / failed, memory-leak restart) as JSON
/// lines to watchdog-events.jsonl in the agent's data directory. The Backup Agent forwards them to
/// the portal as "agent.watchdog.*" events on its next start, so partners and customers see the
/// device's crash/restart history without the watchdog needing its own backend credentials.
/// Bounded: the file is trimmed to the newest 500 lines.
/// </summary>
public sealed class WatchdogEventLog(WatchdogOptions options, TimeProvider time, ILogger<WatchdogEventLog> logger)
{
    private const int MaxLines = 500;
    private readonly Lock _sync = new();

    public void Record(string eventType, string message, IReadOnlyDictionary<string, string?>? data = null)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventId"] = Guid.NewGuid().ToString("N"),
            ["eventType"] = eventType,
            ["occurredAt"] = time.GetUtcNow(),
            ["message"] = message,
            ["data"] = data ?? new Dictionary<string, string?>()
        });

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(options.EventsFile)!);
                File.AppendAllLines(options.EventsFile, [line]);
                var lines = File.ReadAllLines(options.EventsFile);
                if (lines.Length > MaxLines)
                {
                    File.WriteAllLines(options.EventsFile, lines[^MaxLines..]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not record watchdog event {EventType}.", eventType);
            }
        }
    }
}
