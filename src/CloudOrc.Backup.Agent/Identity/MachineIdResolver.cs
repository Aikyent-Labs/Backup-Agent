using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudOrc.Backup.Agent.Identity;

/// <summary>
/// Resolves this machine's stable identifier (used for duplicate / reimage detection at
/// activation):
/// <list type="bullet">
/// <item>Windows: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</item>
/// <item>Linux: /etc/machine-id (systemd) or /var/lib/dbus/machine-id</item>
/// <item>macOS: IOPlatformUUID (hardware UUID)</item>
/// </list>
/// Falls back to a random id persisted in the data directory.
/// </summary>
public static class MachineIdResolver
{
    public static string Resolve(string dataDirectory, ILogger logger)
    {
        try
        {
            var id = OperatingSystem.IsWindows() ? ReadWindows()
                : OperatingSystem.IsLinux() ? ReadLinux()
                : OperatingSystem.IsMacOS() ? ReadMac()
                : null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id.Trim();
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not read the operating system machine id; falling back to a locally generated id.");
        }

        return ResolveOrCreateFallbackMachineId(dataDirectory, logger);
    }

    private static string? ReadWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }

    private static string? ReadLinux()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                if (value.Length >= 16)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadMac()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/sbin/ioreg", ["-rd1", "-c", "IOPlatformExpertDevice"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("IOPlatformUUID", StringComparison.Ordinal))
            {
                var parts = line.Split('"');
                if (parts.Length >= 4)
                {
                    return parts[3];
                }
            }
        }

        return null;
    }

    private static string ResolveOrCreateFallbackMachineId(string dataDirectory, ILogger logger)
    {
        var fallbackFilePath = Path.Combine(dataDirectory, "machine-id.txt");

        try
        {
            if (File.Exists(fallbackFilePath))
            {
                var existing = File.ReadAllText(fallbackFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }

            var generated = Guid.NewGuid().ToString("D");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(fallbackFilePath, generated);
            return generated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not persist a fallback machine id; using a per-process value instead (it will change on restart).");
            return Guid.NewGuid().ToString("D");
        }
    }
}
