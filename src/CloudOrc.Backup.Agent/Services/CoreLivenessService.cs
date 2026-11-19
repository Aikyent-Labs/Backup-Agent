using CloudOrc.Backup.Agent.Health;

namespace CloudOrc.Backup.Agent.Services;

/// <summary>
/// Proves to the Backup Watchdog that the agent core is not hung: touches both liveness markers of
/// the health state every few seconds. If the host's thread pool deadlocks, the markers go stale and
/// the health pipe reports "not alive", so the watchdog restarts the service.
/// </summary>
public sealed class CoreLivenessService(ControlAgentHealthState health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            health.TouchDetection();
            health.TouchProcessing();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
