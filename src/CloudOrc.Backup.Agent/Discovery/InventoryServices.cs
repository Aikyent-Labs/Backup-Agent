using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.Backup.Agent.Commands;
using CloudOrc.Backup.Agent.DurableQueue;
using CloudOrc.Backup.Agent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.Backup.Agent.Discovery;

/// <summary>inventory.refresh - the portal's "Analyse machine" button. Returns the inventory in the command result.</summary>
public sealed class InventoryRefreshHandler(MachineInventoryCollector collector, InventoryPublisherService publisher) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.InventoryRefresh;

    public async Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var inventory = await Task.Run(collector.Collect, cancellationToken).ConfigureAwait(false);
        publisher.Publish(inventory, force: true);
        return new TypedCommandAcceptance { Accepted = true, Data = inventory };
    }
}

/// <summary>
/// Sends the machine inventory as an "agent.inventory" lifecycle event through the durable outbox:
/// shortly after the agent is connected, then every 12 hours, and only when something changed
/// (disks, databases, applications...) so a fleet never floods the control plane.
/// </summary>
public sealed class InventoryPublisherService(
    MachineInventoryCollector collector,
    OutboxStore outbox,
    OutboxDispatcherService dispatcher,
    AgentIdentity identity,
    BackendLinkState link,
    TimeProvider time,
    ILogger<InventoryPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private string? _lastFingerprint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the first successful connection (activation just happened, or the service restarted).
        while (!link.IsConnected && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }

        // Spread a fleet restart: up to 60 s jitter before the first inventory.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 60)), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var inventory = await Task.Run(collector.Collect, stoppingToken).ConfigureAwait(false);
                Publish(inventory, force: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Machine inventory could not be collected; will retry later.");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    public void Publish(MachineInventory inventory, bool force)
    {
        var json = JsonSerializer.Serialize(inventory, ProtocolJson.Options);
        var fingerprint = Fingerprint(inventory);
        if (!force && fingerprint == _lastFingerprint)
        {
            return;
        }

        _lastFingerprint = fingerprint;
        var now = time.GetUtcNow();
        var evt = new AgentLifecycleEvent
        {
            EventId = $"inventory-{now:yyyyMMddHHmmss}-{fingerprint[..12]}",
            EventType = "agent.inventory",
            EndpointId = identity.AgentId,
            OccurredAt = now,
            Data = new Dictionary<string, string?> { ["inventory"] = json }
        };

        outbox.Enqueue(new OutboxItem
        {
            EventId = evt.EventId,
            Kind = OutboxKinds.AgentEvent,
            Route = ControlPlaneRoutes.AgentEvents,
            Payload = JsonSerializer.Serialize(evt, BackendApiClient.SnakeCaseJson),
            IsTerminal = false
        }, now);
        dispatcher.Wake();
        logger.LogInformation("Machine inventory queued ({Volumes} volume(s), {Databases} database engine(s), workloads: {Workloads}).",
            inventory.Volumes.Count, inventory.Databases.Count, string.Join(", ", inventory.SupportedWorkloads));
    }

    /// <summary>Stable parts only (free space and uptime change constantly).</summary>
    private static string Fingerprint(MachineInventory inventory)
    {
        var stable = string.Join('|',
            inventory.AgentVersion,
            inventory.Os.Description,
            string.Join(',', inventory.Volumes.Select(v => $"{v.MountPoint}:{v.FileSystem}:{v.TotalBytes / (1L << 30)}")),
            string.Join(',', inventory.Databases.Select(d => $"{d.Engine}:{d.ClientVersion}:{d.Listening}")),
            string.Join(',', inventory.Applications.Select(a => $"{a.Name}:{a.Path}")),
            string.Join(',', inventory.SupportedWorkloads));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable))).ToLowerInvariant();
    }
}
