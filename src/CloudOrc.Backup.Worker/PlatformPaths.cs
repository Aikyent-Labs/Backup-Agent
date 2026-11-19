namespace CloudOrc.Backup.Worker;

/// <summary>
/// Path rules that differ per operating system. Windows and macOS (APFS default) compare paths
/// case-insensitively; Linux file systems are case-sensitive, so "/data/A" and "/data/a" are two
/// different folders there.
/// </summary>
public static class PlatformPaths
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static char Separator => Path.DirectorySeparatorChar;

    /// <summary>True when the agent can take an OS snapshot of a volume (Windows VSS only today).</summary>
    public static bool SnapshotsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// System locations an ALTERNATE restore target may never point into. Restoring to the original
    /// location is still allowed (the policy chose that path when it was backed up).
    /// </summary>
    public static IReadOnlyList<string> ForbiddenRestoreRoots { get; } = BuildForbiddenRoots();

    private static string[] BuildForbiddenRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            }.Where(r => !string.IsNullOrEmpty(r)).ToArray();
        }

        if (OperatingSystem.IsMacOS())
        {
            return ["/System", "/bin", "/sbin", "/usr", "/private/etc", "/private/var/db", "/Library/Application Support/CloudOrcBackup", "/dev"];
        }

        return ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/proc", "/run", "/sbin", "/sys", "/usr", "/var/lib/cloudorc-backup", "/opt/cloudorc-backup"];
    }
}
