using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.Backup.Agent.Commands;

/// <summary>One entry of a folder listing returned to the portal's folder picker.</summary>
public sealed record BrowseEntry(string Name, string Path, string Type, long? Size, DateTimeOffset? Mtime);

public sealed record BrowseListing(string? Path, IReadOnlyList<BrowseEntry> Roots, IReadOnlyList<BrowseEntry> Entries, bool Truncated);

/// <summary>
/// discovery.browse - read-only, one folder level at a time, for the Protection Plan folder picker
/// (screens F02 / W01 "what to back up"). Security:
/// <list type="bullet">
/// <item>Only absolute local paths on fixed drives; UNC / device paths are refused.</item>
/// <item>Never follows junctions or symlinks (neither in the requested path nor as listed entries),
/// so a link cannot be used to escape the allowed roots.</item>
/// <item>When the backend signs allowedRoots (shared servers: the customer's own folder) nothing
/// outside them is listed.</item>
/// </list>
/// </summary>
public sealed class DiscoveryBrowseHandler : ITypedCommandHandler
{
    public const int DefaultMaxEntries = 500;
    public const int HardMaxEntries = 2000;

    public string CommandType => BackupCommandTypes.DiscoveryBrowse;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<DiscoveryBrowseParameters>() ?? new DiscoveryBrowseParameters();
        return Task.Run(() =>
        {
            var (listing, error) = Browse(p.Path, p.AllowedRoots ?? [], Math.Clamp(p.MaxEntries ?? DefaultMaxEntries, 1, HardMaxEntries));
            return error is null
                ? new TypedCommandAcceptance { Accepted = true, Data = listing }
                : TypedCommandDispatcher.Rejected(error.Value.Code, error.Value.Message);
        }, cancellationToken);
    }

    /// <summary>Pure browse logic (unit-testable). Returns a listing or a (code, message) refusal.</summary>
    public static (BrowseListing? Listing, (string Code, string Message)? Error) Browse(string? requestedPath, IReadOnlyList<string> allowedRoots, int maxEntries)
    {
        var roots = NormalizeRoots(allowedRoots);
        var rootEntries = roots.Count > 0
            ? roots.Where(Directory.Exists).Select(r => new BrowseEntry(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(r)) is { Length: > 0 } n ? n : r, r, "dir", null, null)).ToList()
            : LocalRoots();

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return (new BrowseListing(null, rootEntries, [], false), null);
        }

        if (!TryNormalizeLocalPath(requestedPath, out var path))
        {
            return (null, ("PATH_NOT_ALLOWED", "Only folders on this computer's local drives can be browsed."));
        }

        if (roots.Count > 0 && !roots.Any(r => IsSameOrUnder(path, r)))
        {
            return (null, ("PATH_NOT_ALLOWED", "That folder is outside the folders you can protect."));
        }

        if (!Directory.Exists(path))
        {
            return (null, ("SOURCE_NOT_FOUND", "That folder no longer exists."));
        }

        if (PassesThroughLink(path))
        {
            return (null, ("PATH_NOT_ALLOWED", "Linked folders (junctions/shortcuts) cannot be browsed."));
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false
        };

        var entries = new List<BrowseEntry>();
        var truncated = false;
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if (entries.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            var isLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            var isDir = info is DirectoryInfo;
            entries.Add(new BrowseEntry(
                info.Name,
                info.FullName,
                isLink ? "link" : isDir ? "dir" : "file",
                !isDir && !isLink && info is FileInfo f ? f.Length : null,
                SafeTime(info)));
        }

        var ordered = entries
            .OrderBy(e => e.Type == "file" ? 1 : 0)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (new BrowseListing(path, rootEntries, ordered, truncated), null);
    }

    internal static List<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var r in roots)
        {
            if (TryNormalizeLocalPath(r, out var n) && !result.Contains(n, PathComparer))
            {
                result.Add(n);
            }
        }

        return result;
    }

    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Kernel / runtime pseudo file systems: never browsable or backed up.</summary>
    internal static readonly string[] UnixPseudoRoots = ["/proc", "/sys", "/dev", "/run"];

    /// <summary>
    /// Top-level choices of the folder picker: local fixed drives (Windows: C:, D: ...; Linux/macOS: "/"
    /// plus separately mounted disks such as /home or /data), never network or pseudo file systems.
    /// </summary>
    internal static List<BrowseEntry> LocalRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => new BrowseEntry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, "drive", null, null)).ToList();
        }

        var result = new List<BrowseEntry> { new("/", "/", "drive", null, null) };
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                string mount;
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady)
                    {
                        continue;
                    }

                    mount = d.RootDirectory.FullName;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (mount == "/" || UnixPseudoRoots.Any(r => IsSameOrUnder(mount, r))
                    || mount.StartsWith("/snap/", StringComparison.Ordinal) || mount.StartsWith("/var/lib/docker/", StringComparison.Ordinal)
                    || mount.StartsWith("/System/Volumes/", StringComparison.Ordinal) || mount.StartsWith("/boot", StringComparison.Ordinal))
                {
                    continue;
                }

                if (result.All(e => e.Path != mount))
                {
                    result.Add(new BrowseEntry(mount, mount, "drive", null, null));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>
    /// Windows: absolute "X:\..." path, no UNC/device prefixes. Linux/macOS: absolute "/..." path,
    /// never inside /proc, /sys, /dev or /run. No traversal left after normalization.
    /// </summary>
    internal static bool TryNormalizeLocalPath(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            var u = value.Trim();
            if (!u.StartsWith('/') || u.Contains('\0'))
            {
                return false;
            }

            try
            {
                var fullUnix = System.IO.Path.GetFullPath(u);
                fullUnix = fullUnix.Length > 1 ? fullUnix.TrimEnd('/') : fullUnix;
                if (UnixPseudoRoots.Any(r => IsSameOrUnder(fullUnix, r)))
                {
                    return false;
                }

                path = fullUnix;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        var v = value.Trim().Replace('/', '\\');
        if (v.StartsWith(@"\\", StringComparison.Ordinal) || v.Length < 3 || !char.IsLetter(v[0]) || v[1] != ':' || v[2] != '\\')
        {
            return false;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(v);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            path = full.Length > 3 ? full.TrimEnd('\\') : full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsSameOrUnder(string path, string root)
    {
        var sep = OperatingSystem.IsWindows() ? '\\' : '/';
        var minRootLength = OperatingSystem.IsWindows() ? 3 : 1;
        var r = root.Length > minRootLength ? root.TrimEnd(sep) : root;
        if (string.Equals(path, r, PathComparison))
        {
            return true;
        }

        var prefix = r.EndsWith(sep) ? r : r + sep;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>True when the path itself or any parent below the drive root is a junction/symlink.</summary>
    internal static bool PassesThroughLink(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null && current.Parent is not null)
        {
            try
            {
                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static DateTimeOffset? SafeTime(FileSystemInfo info)
    {
        try
        {
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
