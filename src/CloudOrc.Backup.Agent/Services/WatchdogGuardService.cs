using CloudOrc.Backup.Agent.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudOrc.Backup.Agent.Services;

/// <summary>
/// The watchdog restarts the agent; this guard does the reverse. Every few minutes it checks the
/// CloudOrc Backup Watchdog service and, if it is installed but stopped, starts it again. A missing
/// watchdog service (not installed) is left alone. Only ever touches the Backup Watchdog's own service.
/// </summary>
public sealed class WatchdogGuardService(ILogger<WatchdogGuardService> logger) : BackgroundService
{
    /// <summary>Wait after the agent starts, so setup and updates can finish starting both services first.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    internal Func<ServiceControl.State> Query { get; init; } = ServiceControl.QueryWatchdog;

    internal Func<string?> Start { get; init; } = ServiceControl.StartWatchdog;

    private string? lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                // Service control calls block (up to 60 s); keep them off the host's startup path.
                await Task.Run(CheckOnce, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One check; returns true when it started the watchdog.</summary>
    internal bool CheckOnce()
    {
        if (Query() != ServiceControl.State.Stopped)
        {
            lastError = null;
            return false;
        }

        var error = Start();
        if (error is null)
        {
            lastError = null;
            logger.LogWarning("The CloudOrc Backup Watchdog service was stopped; it has been started again.");
            return true;
        }

        // Log a repeated failure (e.g. the service was disabled on purpose) only once.
        if (!string.Equals(error, lastError, StringComparison.Ordinal))
        {
            logger.LogWarning("The CloudOrc Backup Watchdog service is stopped and could not be started: {Error}", error);
        }

        lastError = error;
        return false;
    }
}
