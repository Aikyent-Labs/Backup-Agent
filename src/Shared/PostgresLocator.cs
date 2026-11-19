using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CloudOrc.Backup.Shared;

/// <summary>
/// Finds PostgreSQL client tools (pg_dump, pg_dumpall, pg_restore, psql, createdb) on Windows, Linux
/// and macOS, and whether a server is listening locally. Shared by the agent (inventory) and the
/// worker (backup / restore). pg_dump must be at least as new as the server, so the NEWEST client
/// found is preferred.
/// </summary>
public static partial class PostgresLocator
{
    public sealed record ClientTools(string BinDirectory, Version Version)
    {
        public string Tool(string name) => Path.Combine(BinDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }

    /// <summary>Candidate bin folders, most specific first. Overridable for tests.</summary>
    public static Func<IEnumerable<string>> CandidateDirectories { get; set; } = DefaultCandidates;

    public static IReadOnlyList<ClientTools> FindAll()
    {
        var found = new List<ClientTools>();
        foreach (var dir in CandidateDirectories().Distinct(StringComparer.Ordinal))
        {
            var pgDump = Path.Combine(dir, "pg_dump" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (!File.Exists(pgDump) || found.Any(f => SamePath(f.BinDirectory, dir)))
            {
                continue;
            }

            if (ReadVersion(pgDump) is { } version)
            {
                found.Add(new ClientTools(dir, version));
            }
        }

        return found.OrderByDescending(f => f.Version).ToList();
    }

    public static ClientTools? FindNewest(string? preferredBinDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredBinDirectory))
        {
            var pgDump = Path.Combine(preferredBinDirectory, "pg_dump" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(pgDump) && ReadVersion(pgDump) is { } v)
            {
                return new ClientTools(preferredBinDirectory, v);
            }
        }

        return FindAll().FirstOrDefault();
    }

    /// <summary>"pg_dump (PostgreSQL) 16.4" → 16.4. Null when the tool does not run.</summary>
    public static Version? ReadVersion(string pgDumpPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(pgDumpPath, ["--version"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }

            return ParseVersion(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static Version? ParseVersion(string text)
    {
        var match = VersionPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(major, minor);
    }

    /// <summary>True when something accepts TCP connections on host:port within the timeout.</summary>
    public static bool IsListening(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(timeout) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Linux/macOS: the local "postgres" OS account exists (peer authentication possible).</summary>
    public static bool PostgresOsUserExists()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return File.Exists("/etc/passwd") && File.ReadLines("/etc/passwd").Any(l => l.StartsWith("postgres:", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> DefaultCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     }.Where(r => !string.IsNullOrEmpty(r)))
            {
                foreach (var dir in SafeSubdirectories(Path.Combine(root, "PostgreSQL")))
                {
                    yield return Path.Combine(dir, "bin");
                }
            }

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (var dir in SafeSubdirectories("/Applications/Postgres.app/Contents/Versions"))
            {
                yield return Path.Combine(dir, "bin");
            }

            foreach (var dir in SafeSubdirectories("/Library/PostgreSQL"))
            {
                yield return Path.Combine(dir, "bin");
            }

            foreach (var prefix in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
            {
                foreach (var dir in SafeSubdirectories(prefix).Where(d => Path.GetFileName(d).StartsWith("postgresql", StringComparison.Ordinal) || Path.GetFileName(d) == "libpq"))
                {
                    yield return Path.Combine(dir, "bin");
                }
            }

            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield break;
        }

        // Debian / Ubuntu (postgresql-common): /usr/lib/postgresql/<major>/bin
        foreach (var dir in SafeSubdirectories("/usr/lib/postgresql"))
        {
            yield return Path.Combine(dir, "bin");
        }

        // RHEL / Rocky / Alma (PGDG): /usr/pgsql-<major>/bin
        foreach (var dir in SafeSubdirectories("/usr").Where(d => Path.GetFileName(d).StartsWith("pgsql-", StringComparison.Ordinal)))
        {
            yield return Path.Combine(dir, "bin");
        }

        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/usr/local/pgsql/bin";
    }

    private static IEnumerable<string> SafeSubdirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path).OrderByDescending(d => d, StringComparer.Ordinal).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\(PostgreSQL\)\s+(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
