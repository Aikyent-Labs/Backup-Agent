using CloudOrc.Backup.Watchdog.Configuration;
using CloudOrc.Backup.Watchdog.ControlAgentManagement;
using CloudOrc.Backup.Watchdog.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.Backup.Watchdog.Services;

/// <summary>
/// Monitors the Control Agent on a fixed interval and performs controlled, rate-limited
/// recovery when it becomes unhealthy. This is the Watchdog's only job - it never
/// executes arbitrary PowerShell and never accepts command files.
///
/// The relationship is intentionally one-directional (Watchdog -> monitors -> Control
/// Agent): the Watchdog keeps running its monitoring loop regardless of whether the
/// Control Agent is stopped, crashing, or repeatedly failing to recover; the Control
/// Agent has no awareness of the Watchdog at all.
/// </summary>
public sealed class WatchdogMonitorService(
    IOptions<WatchdogOptions> options,
    ControlAgentServiceManager serviceManager,
    ControlAgentHealthClient healthClient,
    ConsecutiveFailureTracker failureTracker,
    RecoveryRateLimiter rateLimiter,
    WatchdogResourceMonitors resourceMonitors,
    AgentMetricsFile metricsFile,
    WatchdogEventLog events,
    ILogger<WatchdogMonitorService> logger) : BackgroundService
{
    private readonly WatchdogOptions _options = options.Value;
    private int _memoryBreaches;
    private bool _pipeFallbackLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Watchdog monitor starting. Watching service '{ServiceName}', checking every {IntervalSeconds}s, failure threshold {Threshold}.",
            _options.ControlAgentServiceName, _options.HealthCheckIntervalSeconds, _options.ConsecutiveFailureThreshold);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single bad monitoring cycle (e.g. a transient WMI/service query error)
                // must never stop the Watchdog from continuing to watch.
                logger.LogError(ex, "Unexpected error during a monitoring cycle; will retry next cycle.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Watchdog monitor stopped.");
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var serviceStatus = serviceManager.TryGetStatus();
        logger.LogDebug("Backup Agent service '{ServiceName}' status: {Status}.", _options.ControlAgentServiceName, serviceStatus);
        if (serviceStatus == AgentServiceState.NotInstalled)
        {
            // Nothing to watch (uninstalled, or a developer run). Never try to "recover" a missing service.
            failureTracker.RecordSuccess();
            return;
        }

        var health = await healthClient.TryGetHealthAsync(stoppingToken).ConfigureAwait(false);
        var healthy = health is { Status: "HEALTHY" } || AliveWithoutPipe(health);

        if (health is not null)
        {
            // backendConnectionState is logged for visibility only - it is never a
            // factor in the healthy/unhealthy decision below. A disconnected backend is
            // not a Control Agent failure; see docs/ARCHITECTURE.md.
            logger.LogDebug(
                "Health check response: status={Status}, coreAlive={CoreAlive}, backendConnectionState={BackendState}.",
                health.Status, health.DetectionWorkerAlive && health.ProcessingWorkerAlive, health.BackendConnectionState);
        }
        else
        {
            logger.LogWarning("Health check did not get a response from the Control Agent.");
        }

        var memoryLeak = CheckMemory(serviceStatus.ToString(), health?.Status);
        if (memoryLeak)
        {
            healthy = false;
        }

        if (healthy)
        {
            var previousFailures = failureTracker.RecordSuccess();
            if (previousFailures > 0)
            {
                logger.LogInformation("Control Agent is healthy again; consecutive failure count reset from {Previous} to 0.", previousFailures);
            }
            return;
        }

        var failureCount = failureTracker.RecordFailure();
        while (memoryLeak && !failureTracker.HasReachedThreshold(_options.ConsecutiveFailureThreshold))
        {
            failureCount = failureTracker.RecordFailure();
        }

        logger.LogWarning(
            "Control Agent health check failed ({Count}/{Threshold}). Reason: {Reason}",
            failureCount, _options.ConsecutiveFailureThreshold,
            memoryLeak ? $"agent memory above {_options.MaxAgentWorkingSetMb} MB for {_options.MemoryBreachSamples} checks"
            : health is null ? "no response from health pipe" : $"reported status '{health.Status}'");

        if (!failureTracker.HasReachedThreshold(_options.ConsecutiveFailureThreshold))
        {
            return;
        }

        await AttemptRecoveryAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Logged every monitoring cycle (same cadence as the health check above, so tailing
    /// the Watchdog's log file is a live view) - live CPU/memory usage for both the
    /// Control Agent it watches and the Watchdog itself, independent of the HEALTHY/
    /// DEGRADED health check: a process can be alive and responding on its worker loops
    /// while still using an unexpectedly large or growing amount of memory, which this
    /// line is what would surface.
    /// </summary>
    private bool CheckMemory(string serviceState, string? health)
    {
        var agent = resourceMonitors.ControlAgent.Sample();
        var self = resourceMonitors.Self.Sample();
        metricsFile.Write(serviceState, health, agent, self);
        logger.LogDebug("Resource usage: BackupAgent [{AgentUsage}]; Watchdog (self) [{WatchdogUsage}].", Describe(agent), Describe(self));

        if (_options.MaxAgentWorkingSetMb <= 0 || agent.WorkingSetBytes is not { } ws)
        {
            _memoryBreaches = 0;
            return false;
        }

        _memoryBreaches = ws > _options.MaxAgentWorkingSetMb * 1024L * 1024L ? _memoryBreaches + 1 : 0;
        if (_memoryBreaches < Math.Max(1, _options.MemoryBreachSamples))
        {
            return false;
        }

        _memoryBreaches = 0;
        logger.LogWarning("Backup Agent working set {Mb:F0} MB stayed above {Limit} MB; restarting it (leak guard).", ws / 1048576.0, _options.MaxAgentWorkingSetMb);
        events.Record("agent.watchdog.memory_restart", $"The Backup Agent used more than {_options.MaxAgentWorkingSetMb} MB for too long and was restarted.");
        return true;
    }

    private static string Describe(ProcessResourceSnapshot snapshot)
    {
        if (!snapshot.IsRunning)
        {
            return "not running";
        }

        var cpu = snapshot.CpuPercent is { } cpuPercent ? $"{cpuPercent:F1}%" : "n/a (first sample)";
        var workingSetMb = snapshot.WorkingSetBytes is { } workingSet ? $"{workingSet / 1024.0 / 1024.0:F1} MB" : "n/a";
        var privateMemoryMb = snapshot.PrivateMemoryBytes is { } privateMemory ? $"{privateMemory / 1024.0 / 1024.0:F1} MB" : "n/a";

        return $"pid={snapshot.ProcessId}, cpu={cpu}, workingSet={workingSetMb}, privateMemory={privateMemoryMb}";
    }

    /// <summary>
    /// The health pipe gave no answer, but the agent's liveness file is fresh: the agent is alive (the
    /// pipe is just unreachable, e.g. a sandboxed /tmp). Never restart it then - that would kill a
    /// running backup. A hung agent stops writing the file and is still recovered.
    /// </summary>
    private bool AliveWithoutPipe(object? health)
    {
        if (health is not null)
        {
            return false;
        }

        var alive = AgentLivenessFile.IsFresh(_options.LivenessFile, TimeSpan.FromSeconds(Math.Max(20, _options.LivenessMaxAgeSeconds)));
        if (alive && !_pipeFallbackLogged)
        {
            _pipeFallbackLogged = true;
            logger.LogWarning("The health pipe '{PipeName}' is not reachable, but the agent's liveness file is fresh: treating the agent as healthy.", _options.HealthPipeName);
        }

        return alive;
    }

    private async Task AttemptRecoveryAsync(CancellationToken stoppingToken)
    {
        var decision = rateLimiter.Evaluate();
        if (!decision.Allowed)
        {
            logger.LogWarning("Recovery is needed but was suppressed: {Reason}", decision.Reason);
            return;
        }

        logger.LogWarning(
            "Consecutive failure threshold reached; attempting recovery of service '{ServiceName}'.",
            _options.ControlAgentServiceName);

        rateLimiter.RecordAttempt();
        var restartIssued = serviceManager.TryRestart();

        if (!restartIssued)
        {
            rateLimiter.RecordOutcome(succeeded: false);
            events.Record("agent.watchdog.restart_failed", "The watchdog could not restart the Backup Agent service.");
            logger.LogError("Recovery attempt failed: the service could not be restarted (see the warning above for why).");
            return;
        }

        logger.LogInformation("Restart issued; waiting {WaitSeconds}s before re-checking health.", _options.RecoveryWaitSeconds);
        await Task.Delay(TimeSpan.FromSeconds(_options.RecoveryWaitSeconds), stoppingToken).ConfigureAwait(false);

        var postRecoveryHealth = await healthClient.TryGetHealthAsync(stoppingToken).ConfigureAwait(false);
        var postRecoveryHealthy = postRecoveryHealth is { Status: "HEALTHY" } || AliveWithoutPipe(postRecoveryHealth);
        rateLimiter.RecordOutcome(postRecoveryHealthy);

        if (postRecoveryHealthy)
        {
            logger.LogInformation("Recovery succeeded; the Backup Agent is healthy again.");
            events.Record("agent.watchdog.recovered", "The watchdog restarted the Backup Agent and it is healthy again.");
            failureTracker.RecordSuccess();
        }
        else
        {
            events.Record("agent.watchdog.recovery_failed", "The Backup Agent was restarted but is still not healthy.");
            logger.LogError("Recovery attempt did not restore health; the next attempt will be subject to backoff.");
        }
    }
}
