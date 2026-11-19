using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Platform;

namespace CloudOrc.Backup.Agent.Startup;

/// <summary>
/// Ensures the data directory tree exists before any hosted service starts. On Linux/macOS the
/// data directory is restricted to its owner (root, 0700): it holds the credential and device key.
/// </summary>
public static class DirectoryBootstrapper
{
    public static void EnsureDirectories(ControlAgentOptions options)
    {
        if (options.HardenDataDirectoryAcl && !OperatingSystem.IsWindows())
        {
            SecureFile.EnsurePrivateDirectory(options.DataDirectory);
        }
        else
        {
            Directory.CreateDirectory(options.DataDirectory);
        }

        Directory.CreateDirectory(options.LogsDirectory);
    }
}
