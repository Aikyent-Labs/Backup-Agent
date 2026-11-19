using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace CloudOrc.Backup.Updater;

/// <summary>Service names / agent path / package handling of the Backup Agent per operating system.</summary>
public static class UpdatePlatform
{
    /// <summary>The services an update stops, replaces and health-checks (agent + its watchdog).</summary>
    public static IReadOnlyList<string> ServiceNames { get; } =
        OperatingSystem.IsWindows() ? ["CloudOrcBackupAgent", "CloudOrcBackupWatchdog"]
        : OperatingSystem.IsMacOS() ? ["com.cloudorc.backup.agent", "com.cloudorc.backup.watchdog"]
        : ["cloudorc-backup-agent.service", "cloudorc-backup-watchdog.service"];

    public static string AgentExecutable(string installDirectory) =>
        Path.Combine(installDirectory, "bin", "cloudorc-backup" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    /// <summary>Windows: signed Inno Setup installer (.exe). Linux / macOS: .tar.gz of the install tree.</summary>
    public static IInstallerRunner CreateInstallerRunner(string installDirectory, IServiceControl services, IUpdateLog log) =>
        OperatingSystem.IsWindows() ? new InnoInstallerRunner(log) : new TarballInstallerRunner(installDirectory, services, log);

    public static IServiceControl CreateServiceControl(IUpdateLog log) =>
        OperatingSystem.IsWindows() ? new WindowsServiceControl(log) : new UnixServiceControl(log);

    /// <summary>
    /// Windows: Authenticode (WinVerifyTrust) on the installer. Linux / macOS have no OS-level code
    /// signature for a tarball: integrity rests on the SHA-256 that arrived inside the backend-SIGNED
    /// agent.update command (verified by the agent before this updater ever runs) and is re-checked here.
    /// </summary>
    public static IAuthenticodeVerifier CreateSignatureVerifier() =>
        OperatingSystem.IsWindows() ? new AuthenticodeVerifier() : new HashChainOnlyVerifier();
}

public sealed class HashChainOnlyVerifier : IAuthenticodeVerifier
{
    public VerificationResult Verify(string filePath) => VerificationResult.Valid();
}

/// <summary>systemd (systemctl) / launchd (launchctl) service control for the updater.</summary>
public sealed class UnixServiceControl(IUpdateLog log) : IServiceControl
{
    public bool IsRunning(string serviceName)
    {
        if (OperatingSystem.IsMacOS())
        {
            var (code, output) = Run("launchctl", ["print", $"system/{serviceName}"]);
            return code == 0 && output.Contains("state = running", StringComparison.Ordinal);
        }

        var (_, state) = Run("systemctl", ["is-active", serviceName]);
        return state.Trim() == "active";
    }

    public bool Stop(string serviceName, TimeSpan timeout)
    {
        var (code, output) = OperatingSystem.IsMacOS()
            ? Run("launchctl", ["bootout", $"system/{serviceName}"])
            : Run("systemctl", ["stop", serviceName]);
        if (code != 0 && !OperatingSystem.IsMacOS())
        {
            log.Warn($"systemctl stop {serviceName} failed ({code}): {output.Trim()}");
        }

        return WaitFor(serviceName, running: false, timeout);
    }

    public bool Start(string serviceName, TimeSpan timeout)
    {
        var (code, output) = OperatingSystem.IsMacOS()
            ? Run("launchctl", ["bootstrap", "system", $"/Library/LaunchDaemons/{serviceName}.plist"])
            : Run("systemctl", ["start", serviceName]);
        if (code != 0 && !IsRunning(serviceName))
        {
            log.Warn($"start {serviceName} failed ({code}): {output.Trim()}");
        }

        return WaitFor(serviceName, running: true, timeout);
    }

    private bool WaitFor(string serviceName, bool running, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning(serviceName) == running)
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return false;
    }

    private static (int Code, string Output) Run(string tool, IReadOnlyList<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
            {
                start.ArgumentList.Add(a);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return (-1, "timed out");
            }

            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}

/// <summary>
/// Linux / macOS "installer": the update package is a .tar.gz of the install tree (bin/, engines/). Stops both services, extracts over the install directory (file
/// modes from the archive are applied), starts both services. The orchestrator has already backed up
/// the install directory and rolls back on any failure.
/// </summary>
public sealed class TarballInstallerRunner(string installDirectory, IServiceControl services, IUpdateLog log) : IInstallerRunner
{
    public Task<int?> RunAsync(string installerPath, string installerLogPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (var service in UpdatePlatform.ServiceNames.Reverse())
        {
            if (!services.Stop(service, TimeSpan.FromSeconds(60)))
            {
                log.Warn($"{service} did not stop within 60 s; extracting anyway.");
            }
        }

        try
        {
            using var file = File.OpenRead(installerPath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            ExtractSafely(gzip, installDirectory);
            log.Info($"Package extracted into '{installDirectory}'.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Error($"Package extraction failed: {ex.Message}");
            return Task.FromResult<int?>(1);
        }

        foreach (var service in UpdatePlatform.ServiceNames)
        {
            if (!services.Start(service, TimeSpan.FromSeconds(60)))
            {
                log.Warn($"{service} did not start within 60 s.");
            }
        }

        return Task.FromResult<int?>(0);
    }

    /// <summary>Extracts regular files and folders only; refuses links and any path escaping the target.</summary>
    internal static void ExtractSafely(Stream tar, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var reader = new TarReader(tar);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/').TrimStart('/');
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            if (name.Length == 0)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(target, name));
            if (!destination.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Package entry escapes the install directory: {entry.Name}");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temp = destination + ".new";
                    entry.ExtractToFile(temp, overwrite: true);
                    File.Move(temp, destination, overwrite: true);
                    break;
                default:
                    throw new InvalidOperationException($"Package entry type {entry.EntryType} is not allowed: {entry.Name}");
            }
        }
    }
}
