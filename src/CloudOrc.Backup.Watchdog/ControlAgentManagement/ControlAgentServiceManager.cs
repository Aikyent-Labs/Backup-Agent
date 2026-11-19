using System.Diagnostics;
using CloudOrc.Backup.Watchdog.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudOrc.Backup.Watchdog.ControlAgentManagement;

public enum AgentServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Unknown
}

/// <summary>
/// Queries and restarts the Backup Agent service through the OS service manager only (Windows
/// Service Control Manager, systemctl, launchctl). It never runs scripts and only ever touches the
/// one configured service name.
/// </summary>
public sealed class ControlAgentServiceManager(WatchdogOptions options, ILogger<ControlAgentServiceManager> logger)
{
    public AgentServiceState TryGetStatus()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var controller = new System.ServiceProcess.ServiceController(options.ControlAgentServiceName);
                return controller.Status switch
                {
                    System.ServiceProcess.ServiceControllerStatus.Running => AgentServiceState.Running,
                    System.ServiceProcess.ServiceControllerStatus.Stopped => AgentServiceState.Stopped,
                    System.ServiceProcess.ServiceControllerStatus.StartPending => AgentServiceState.Starting,
                    System.ServiceProcess.ServiceControllerStatus.StopPending => AgentServiceState.Stopping,
                    _ => AgentServiceState.Unknown
                };
            }

            if (OperatingSystem.IsMacOS())
            {
                var (code, output) = Run("launchctl", ["print", $"system/{options.ControlAgentServiceName}"]);
                return code != 0 ? AgentServiceState.NotInstalled
                    : output.Contains("state = running", StringComparison.Ordinal) ? AgentServiceState.Running : AgentServiceState.Stopped;
            }

            var (exit, state) = Run("systemctl", ["is-active", options.ControlAgentServiceName]);
            return state.Trim() switch
            {
                "active" or "reloading" => AgentServiceState.Running,
                "activating" => AgentServiceState.Starting,
                "deactivating" => AgentServiceState.Stopping,
                "inactive" or "failed" => exit == 4 ? AgentServiceState.NotInstalled : AgentServiceState.Stopped,
                _ => exit == 4 ? AgentServiceState.NotInstalled : AgentServiceState.Unknown
            };
        }
        catch (InvalidOperationException)
        {
            return AgentServiceState.NotInstalled;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning(ex, "Transient error querying status of service {ServiceName}.", options.ControlAgentServiceName);
            return AgentServiceState.Unknown;
        }
    }

    /// <summary>Restarts the Backup Agent service. False (logged) when it is not installed or did not come back.</summary>
    public bool TryRestart()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var controller = new System.ServiceProcess.ServiceController(options.ControlAgentServiceName);
                controller.Refresh();
                if (controller.Status is not System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    logger.LogInformation("Stopping service {ServiceName} before restart.", options.ControlAgentServiceName);
                    controller.Stop();
                    controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }

                controller.Start();
                controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                return true;
            }

            var (code, output) = OperatingSystem.IsMacOS()
                ? Run("launchctl", ["kickstart", "-k", $"system/{options.ControlAgentServiceName}"])
                : Run("systemctl", ["restart", options.ControlAgentServiceName]);
            if (code != 0)
            {
                logger.LogWarning("Restart of {ServiceName} failed (exit {Code}): {Output}", options.ControlAgentServiceName, code, output.Trim());
                return false;
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Cannot restart service {ServiceName} - it is not installed.", options.ControlAgentServiceName);
            return false;
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            logger.LogError(ex, "Timed out waiting for service {ServiceName} during restart.", options.ControlAgentServiceName);
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogError(ex, "Service manager error while restarting {ServiceName}.", options.ControlAgentServiceName);
            return false;
        }
    }

    private static (int Code, string Output) Run(string tool, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (-1, "timed out");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
