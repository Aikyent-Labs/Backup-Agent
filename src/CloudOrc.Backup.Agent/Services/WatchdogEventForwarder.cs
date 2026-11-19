using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.DurableQueue;
using CloudOrc.Backup.Agent.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.Backup.Agent.Services;

/// <summary>
/// Forwards the Backup Watchdog's recovery events (watchdog-events.jsonl in the data directory) to
/// the portal as "agent.watchdog.*" lifecycle events through the durable outbox, then clears the
/// file. Runs at start (right after a watchdog restart) and every 5 minutes.
/// </summary>
public sealed class WatchdogEventForwarder(
    IOptions<ControlAgentOptions> options,
    OutboxStore outbox,
    OutboxDispatcherService dispatcher,
    AgentIdentity identity,
    TimeProvider time,
    ILogger<WatchdogEventForwarder> logger) : BackgroundService
{
    private string EventsFile => Path.Combine(options.Value.DataDirectory, "watchdog-events.jsonl");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                Forward();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Watchdog events could not be forwarded now.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal int Forward()
    {
        if (!File.Exists(EventsFile))
        {
            return 0;
        }

        var lines = File.ReadAllLines(EventsFile);
        var forwarded = 0;
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var eventId = root.GetProperty("eventId").GetString() ?? Guid.NewGuid().ToString("N");
                var eventType = root.GetProperty("eventType").GetString() ?? "agent.watchdog.event";
                if (!eventType.StartsWith("agent.watchdog.", StringComparison.Ordinal))
                {
                    continue;
                }

                var occurredAt = root.TryGetProperty("occurredAt", out var at) && at.TryGetDateTimeOffset(out var t) ? t : time.GetUtcNow();
                var data = new Dictionary<string, string?>
                {
                    ["message"] = root.TryGetProperty("message", out var m) ? m.GetString() : null,
                    ["source"] = "watchdog"
                };
                var evt = new AgentLifecycleEvent
                {
                    EventId = $"watchdog-{eventId}",
                    EventType = eventType,
                    EndpointId = identity.AgentId,
                    OccurredAt = occurredAt,
                    Data = data
                };

                outbox.Enqueue(new OutboxItem
                {
                    EventId = evt.EventId,
                    Kind = OutboxKinds.AgentEvent,
                    Route = ControlPlaneRoutes.AgentEvents,
                    Payload = JsonSerializer.Serialize(evt, BackendApiClient.SnakeCaseJson),
                    IsTerminal = true
                }, time.GetUtcNow());
                forwarded++;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Skipping a malformed watchdog event line.");
            }
        }

        File.WriteAllText(EventsFile, string.Empty);
        if (forwarded > 0)
        {
            dispatcher.Wake();
            logger.LogInformation("Forwarded {Count} watchdog event(s) to CloudOrc.", forwarded);
        }

        return forwarded;
    }
}
