using System.Diagnostics;

namespace CloudOrc.Backup.Agent.Platform;

/// <summary>
/// Starts / restarts / queries an OS service by its platform name: Windows Service Control Manager,
/// systemd (<c>systemctl</c>) or launchd (<c>launchctl</c>). Only ever used for the Backup Agent's own
/// services - never an arbitrary service name from the network.
/// </summary>
public static class ServiceControl
{
    public enum State
    {
        Unknown,
        NotInstalled,
        Stopped,
        Running
    }

    public static State Query(string windowsName, string systemdUnit, string launchdLabel)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var controller = new System.ServiceProcess.ServiceController(windowsName);
                return controller.Status == System.ServiceProcess.ServiceControllerStatus.Running ? State.Running : State.Stopped;
            }

            if (OperatingSystem.IsLinux())
            {
                var (code, output) = Run("systemctl", ["is-active", systemdUnit]);
                return output.Trim() switch
                {
                    "active" or "activating" or "reloading" => State.Running,
                    "inactive" or "failed" or "deactivating" => code == 4 ? State.NotInstalled : State.Stopped,
                    _ => code == 4 ? State.NotInstalled : State.Unknown
                };
            }

            if (OperatingSystem.IsMacOS())
            {
                var (code, output) = Run("launchctl", ["print", $"system/{launchdLabel}"]);
                if (code != 0)
                {
                    return State.NotInstalled;
                }

                return output.Contains("state = running", StringComparison.Ordinal) ? State.Running : State.Stopped;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return OperatingSystem.IsWindows() ? State.NotInstalled : State.Unknown;
        }

        return State.Unknown;
    }

    /// <summary>Restarts the service; returns null on success or an error message.</summary>
    public static string? Restart(string windowsName, string systemdUnit, string launchdLabel, TimeSpan timeout)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var controller = new System.ServiceProcess.ServiceController(windowsName);
                if (controller.Status is not System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    controller.Stop();
                    controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, timeout);
                }

                controller.Start();
                controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, timeout);
                return null;
            }

            if (OperatingSystem.IsLinux())
            {
                var (code, output) = Run("systemctl", ["restart", systemdUnit]);
                return code == 0 ? null : $"systemctl restart {systemdUnit} failed ({code}): {output.Trim()}";
            }

            if (OperatingSystem.IsMacOS())
            {
                var (code, output) = Run("launchctl", ["kickstart", "-k", $"system/{launchdLabel}"]);
                return code == 0 ? null : $"launchctl kickstart {launchdLabel} failed ({code}): {output.Trim()}";
            }

            return "Unsupported operating system.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            return ex.Message;
        }
    }

    public static State QueryAgent() =>
        Query(AgentPlatform.WindowsServiceName, AgentPlatform.SystemdUnit, AgentPlatform.LaunchdLabel);

    public static string? RestartAgent() =>
        Restart(AgentPlatform.WindowsServiceName, AgentPlatform.SystemdUnit, AgentPlatform.LaunchdLabel, TimeSpan.FromSeconds(60));

    private static (int ExitCode, string Output) Run(string tool, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (-1, "timed out");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
