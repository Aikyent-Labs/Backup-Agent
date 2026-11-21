using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudOrc.Backup.Shared;

/// <summary>
/// Finds the MySQL / MariaDB client tools (mysqldump + mysql, or MariaDB's mariadb-dump + mariadb) on
/// Windows, Linux and macOS, and builds their connection arguments. Shared by the agent (inventory,
/// database.discover) and the worker (backup / restore). Passwords are never put on a command line -
/// they reach the tools only through the MYSQL_PWD environment variable.
/// </summary>
public static partial class MySqlLocator
{
    /// <summary>System schemas never backed up or offered in the picker.</summary>
    public static readonly IReadOnlySet<string> SystemDatabases =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "information_schema", "performance_schema", "mysql", "sys" };

    public sealed record ClientTools(string BinDirectory, string DumpPath, string? ClientPath, Version? Version, bool IsMariaDb);

    /// <summary>Candidate bin folders after PATH. Overridable for tests.</summary>
    public static Func<IEnumerable<string>> CandidateDirectories { get; set; } = DefaultCandidates;

    private static string Exe(string name) => name + (OperatingSystem.IsWindows() ? ".exe" : "");

    /// <summary>The first folder (preferred, then PATH, then well-known install folders) that holds a dump tool.</summary>
    public static ClientTools? Find(string? preferredBinDirectory = null)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredBinDirectory))
        {
            dirs.Add(preferredBinDirectory);
        }

        dirs.AddRange(PathDirectories());
        dirs.AddRange(CandidateDirectories());
        foreach (var dir in dirs.Distinct(StringComparer.Ordinal))
        {
            if (TryDirectory(dir) is { } tools)
            {
                return tools;
            }
        }

        return null;
    }

    public static ClientTools? TryDirectory(string dir)
    {
        try
        {
            foreach (var (dump, client) in new[] { ("mysqldump", "mysql"), ("mariadb-dump", "mariadb") })
            {
                var dumpPath = Path.Combine(dir, Exe(dump));
                if (!File.Exists(dumpPath))
                {
                    continue;
                }

                string? clientPath = Path.Combine(dir, Exe(client));
                if (!File.Exists(clientPath))
                {
                    var other = Path.Combine(dir, Exe(client == "mysql" ? "mariadb" : "mysql"));
                    clientPath = File.Exists(other) ? other : null;
                }

                var versionText = ReadVersionText(dumpPath);
                if (versionText is null)
                {
                    continue; // present but not runnable
                }

                return new ClientTools(dir, dumpPath, clientPath, ParseVersion(versionText),
                    versionText.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) || dump.StartsWith("mariadb", StringComparison.Ordinal));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// "mysqldump  Ver 8.0.36 for Win64" → 8.0; "mysqldump  Ver 10.19 Distrib 10.6.16-MariaDB" → 10.6;
    /// "mysqldump from 11.4.2-MariaDB, client 10.19" → 11.4. Null when unrecognised.
    /// </summary>
    public static Version? ParseVersion(string text)
    {
        foreach (var pattern in new[] { DistribPattern(), FromPattern(), VerPattern() })
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                return new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    /// <summary>Connection options (no password - that is MYSQL_PWD only).</summary>
    public static List<string> ConnectionArgs(string host, int port, string username)
    {
        var args = new List<string>
        {
            "--host=" + (string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host),
            "--port=" + (port is > 0 and <= 65535 ? port : 3306).ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(username))
        {
            args.Add("--user=" + username);
        }

        return args;
    }

    /// <summary>Environment for a tool run: MYSQL_PWD carries the password; an inherited one is removed when none is given.</summary>
    public static void ApplyPassword(IDictionary<string, string?> environment, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            environment.Remove("MYSQL_PWD");
        }
        else
        {
            environment["MYSQL_PWD"] = password;
        }
    }

    /// <summary>Turns mysql/mysqldump stderr into a customer-friendly message.</summary>
    public static string FriendlyError(string? stderr, string host, int port, string username)
    {
        var text = stderr ?? "";
        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => !l.Contains("[Warning]", StringComparison.Ordinal)) ?? "no details";
        if (text.Contains("Access denied for user", StringComparison.OrdinalIgnoreCase) && text.Contains("using password", StringComparison.OrdinalIgnoreCase))
        {
            return $"MySQL login failed for user '{username}' - check the username and password. The account needs SELECT, SHOW VIEW, TRIGGER, EVENT and LOCK TABLES privileges on the databases to back up.";
        }

        if (text.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"MySQL user '{username}' is missing a privilege: {last}. Backups need SELECT, SHOW VIEW, TRIGGER, EVENT and LOCK TABLES on the databases.";
        }

        if (text.Contains("Can't connect", StringComparison.OrdinalIgnoreCase) || text.Contains("Unknown MySQL server host", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Lost connection", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not reach MySQL/MariaDB at {host}:{port} - check that the server is running and the host/port are correct ({last}).";
        }

        return last;
    }

    private static string? ReadVersionText(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, ["--version"])
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

            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> PathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        return string.IsNullOrEmpty(path)
            ? []
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => Path.IsPathFullyQualified(p.Trim('"'))).Select(p => p.Trim('"'));
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
                // C:\Program Files\MySQL\MySQL Server 8.0\bin (newest first)
                foreach (var dir in SafeSubdirectories(Path.Combine(root, "MySQL")).Where(d => Path.GetFileName(d).StartsWith("MySQL Server", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return Path.Combine(dir, "bin");
                }

                // C:\Program Files\MariaDB 10.11\bin
                foreach (var dir in SafeSubdirectories(root).Where(d => Path.GetFileName(d).StartsWith("MariaDB", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return Path.Combine(dir, "bin");
                }
            }

            yield break;
        }

        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/usr/local/mysql/bin";
        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin";
            foreach (var prefix in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
            {
                foreach (var dir in SafeSubdirectories(prefix).Where(d => Path.GetFileName(d).StartsWith("mysql", StringComparison.Ordinal) || Path.GetFileName(d).StartsWith("mariadb", StringComparison.Ordinal)))
                {
                    yield return Path.Combine(dir, "bin");
                }
            }
        }
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

    [GeneratedRegex(@"Distrib\s+(\d+)\.(\d+)")]
    private static partial Regex DistribPattern();

    [GeneratedRegex(@"from\s+(\d+)\.(\d+)")]
    private static partial Regex FromPattern();

    [GeneratedRegex(@"Ver\s+(\d+)\.(\d+)")]
    private static partial Regex VerPattern();
}
