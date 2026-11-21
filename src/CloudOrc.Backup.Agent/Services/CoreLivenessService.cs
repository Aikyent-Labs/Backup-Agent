using System.Text.Json;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Health;
using Microsoft.Extensions.Options;

namespace CloudOrc.Backup.Agent.Services;

/// <summary>
/// Proves to the Backup Watchdog that the agent core is not hung: touches both liveness markers of
/// the health state every few seconds. If the host's thread pool deadlocks, the markers go stale and
/// the health pipe reports "not alive", so the watchdog restarts the service.
///
/// The same loop also writes <c>agent-liveness.json</c> in the data directory. The watchdog uses it
/// as a second signal when it cannot reach the health pipe (e.g. a sandboxed /tmp on Linux): a
/// fresh file means the agent is alive, so a running backup is never killed by a needless restart.
/// A hung agent stops writing it, so the watchdog still recovers it.
/// </summary>
public sealed class CoreLivenessService(ControlAgentHealthState health, IOptions<ControlAgentOptions> options, ILogger<CoreLivenessService> logger) : BackgroundService
{
    public const string LivenessFileName = "agent-liveness.json";
    private static readonly TimeSpan FileEvery = TimeSpan.FromSeconds(15);
    private DateTimeOffset _lastFileWrite = DateTimeOffset.MinValue;
    private bool _warned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            health.TouchDetection();
            health.TouchProcessing();
            WriteLivenessFile();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void WriteLivenessFile()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastFileWrite < FileEvery)
        {
            return;
        }

        try
        {
            var dir = options.Value.DataDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, LivenessFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { at = now, pid = Environment.ProcessId }));
            File.Move(tmp, path, overwrite: true);
            _lastFileWrite = now;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                logger.LogWarning(ex, "Could not write the liveness file; the watchdog relies on the health pipe only.");
            }
        }
    }
}
