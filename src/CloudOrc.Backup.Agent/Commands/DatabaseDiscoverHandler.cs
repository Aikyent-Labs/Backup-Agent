using System.Diagnostics;
using System.Globalization;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Shared;
using Microsoft.Data.SqlClient;

namespace CloudOrc.Backup.Agent.Commands;

/// <summary>
/// database.discover - "Test connection" for the portal's database setup: logs in with what the customer
/// typed and lists the user databases (with size, and the recovery model on SQL Server). Read-only and
/// time-boxed (~20 s). The password is transient: it reaches the server driver / client tool for this one
/// connection (PGPASSWORD / MYSQL_PWD environment, never a command line), is never logged, stored or
/// echoed - the durable inbox only keeps the result, which never contains it.
/// <para>
/// Result (TypedCommandAcceptance.data): { ok, engine, serverVersion, toolsFound, databases: [{ name,
/// sizeBytes, recoveryModel }], error }. A failed login is still an accepted command with ok=false and a
/// friendly error; only malformed parameters are rejected (also with that data shape).
/// </para>
/// </summary>
public sealed class DatabaseDiscoverHandler : ITypedCommandHandler
{
    public static readonly TimeSpan TimeBox = TimeSpan.FromSeconds(20);

    public string CommandType => BackupCommandTypes.DatabaseDiscover;

    public async Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<DatabaseDiscoverParameters>();
        var (normalized, error) = Validate(p);
        if (normalized is null)
        {
            return new TypedCommandAcceptance
            {
                Accepted = false,
                ReasonCode = "INVALID_PARAMETERS",
                Message = error,
                Data = new DatabaseDiscoverResult { Ok = false, Engine = p?.Engine, Error = error }
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeBox);
        DatabaseDiscoverResult result;
        try
        {
            result = await DiscoverAsync(normalized, p!.Password, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new DatabaseDiscoverResult
            {
                Ok = false,
                Engine = normalized.Engine,
                ToolsFound = ToolsFound(normalized),
                Error = $"No answer from the database server within {TimeBox.TotalSeconds:F0} seconds. Check the host, port and that the server is running."
            };
        }

        return new TypedCommandAcceptance { Accepted = true, Message = result.Ok ? "Connection succeeded." : result.Error, Data = result };
    }

    /// <summary>Connection parameters after validation and defaults (no password).</summary>
    public sealed record DiscoverTarget(string Engine, string Host, int Port, string? Instance, string Username, bool UseWindowsAuth, bool UsePeerAuth, string? BinDirectory);

    /// <summary>Validates and normalizes the parameters (defaults: port per engine, postgres/root/sa user names).</summary>
    public static (DiscoverTarget? Target, string? Error) Validate(DatabaseDiscoverParameters? p)
    {
        if (p is null)
        {
            return (null, "Parameters are required.");
        }

        var engine = (p.Engine ?? "").Trim().ToLowerInvariant();
        if (!BackupSourceKinds.IsDatabase(engine))
        {
            return (null, "engine must be postgresql, mssql or mysql.");
        }

        var host = string.IsNullOrWhiteSpace(p.Host) ? "127.0.0.1" : p.Host.Trim();
        if (host.Length > 253 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']')))
        {
            return (null, "Enter a valid host name or IP address.");
        }

        var port = p.Port ?? engine switch { BackupSourceKinds.MsSql => 1433, BackupSourceKinds.MySql => 3306, _ => 5432 };
        if (port is < 1 or > 65535)
        {
            return (null, "Port must be between 1 and 65535.");
        }

        string? instance = null;
        if (!string.IsNullOrWhiteSpace(p.Instance))
        {
            instance = p.Instance.Trim();
            if (engine != BackupSourceKinds.MsSql || instance.Length > 64 || instance.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '#' or '-')))
            {
                return (null, "instance must be a valid SQL Server instance name (MS SQL Server only).");
            }
        }

        if (p.UseWindowsAuth && engine != BackupSourceKinds.MsSql)
        {
            return (null, "Windows authentication is only available for MS SQL Server.");
        }

        if (p.UsePeerAuth && engine != BackupSourceKinds.PostgreSql)
        {
            return (null, "Peer authentication is only available for PostgreSQL.");
        }

        var username = string.IsNullOrWhiteSpace(p.Username)
            ? engine switch { BackupSourceKinds.MsSql => "sa", BackupSourceKinds.MySql => "root", _ => "postgres" }
            : p.Username.Trim();
        if (username.Length > 128 || username.Any(c => char.IsControl(c) || c is ';' or '\'' or '"'))
        {
            return (null, "The user name is invalid.");
        }

        if (p.Password is { Length: > 1024 } || p.Password?.Any(c => c == '\0') == true)
        {
            return (null, "The password is invalid.");
        }

        if (p.BinDirectory is not null && !Path.IsPathFullyQualified(p.BinDirectory))
        {
            return (null, "binDirectory must be an absolute path.");
        }

        return (new DiscoverTarget(engine, host, port, instance, username, p.UseWindowsAuth, p.UsePeerAuth, p.BinDirectory), null);
    }

    private static bool ToolsFound(DiscoverTarget t) => t.Engine switch
    {
        BackupSourceKinds.MsSql => true, // built-in driver
        BackupSourceKinds.MySql => MySqlLocator.Find(t.BinDirectory) is not null,
        _ => PostgresLocator.FindNewest(t.BinDirectory) is not null
    };

    private static Task<DatabaseDiscoverResult> DiscoverAsync(DiscoverTarget t, string? password, CancellationToken ct) => t.Engine switch
    {
        BackupSourceKinds.MsSql => DiscoverMsSqlAsync(t, password, ct),
        BackupSourceKinds.MySql => DiscoverMySqlAsync(t, password, ct),
        _ => DiscoverPostgresAsync(t, password, ct)
    };

    // ------------------------------------------------------------------ MS SQL Server

    private static async Task<DatabaseDiscoverResult> DiscoverMsSqlAsync(DiscoverTarget t, string? password, CancellationToken ct)
    {
        var dataSource = MsSqlSupport.DataSource(t.Host, t.Port, t.Instance);
        try
        {
            var connectionString = MsSqlSupport.ConnectionString(t.Host, t.Port, t.Instance, t.UseWindowsAuth, t.Username, password, "master", 15);
            await using var connection = await MsSqlSupport.OpenAsync(connectionString, ct).ConfigureAwait(false);
            var version = await MsSqlSupport.ServerVersionAsync(connection, ct).ConfigureAwait(false);
            var databases = await MsSqlSupport.ListDatabasesAsync(connection, ct).ConfigureAwait(false);
            return new DatabaseDiscoverResult { Ok = true, Engine = t.Engine, ServerVersion = version, ToolsFound = true, Databases = databases };
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            return new DatabaseDiscoverResult { Ok = false, Engine = t.Engine, ToolsFound = true, Error = MsSqlSupport.FriendlyError(ex, dataSource, t.UseWindowsAuth, t.Username) };
        }
    }

    // ------------------------------------------------------------------ MySQL / MariaDB

    public const string MySqlDiscoverSql =
        "SELECT VERSION(); " +
        "SELECT s.SCHEMA_NAME, (SELECT SUM(t.DATA_LENGTH + t.INDEX_LENGTH) FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = s.SCHEMA_NAME) " +
        "FROM information_schema.SCHEMATA s ORDER BY s.SCHEMA_NAME";

    public static List<string> MySqlDiscoverArguments(DiscoverTarget t)
    {
        var args = MySqlLocator.ConnectionArgs(t.Host, t.Port, t.Username);
        args.AddRange(["--connect-timeout=15", "-N", "-B", "-e", MySqlDiscoverSql]);
        return args;
    }

    private static async Task<DatabaseDiscoverResult> DiscoverMySqlAsync(DiscoverTarget t, string? password, CancellationToken ct)
    {
        var tools = MySqlLocator.Find(t.BinDirectory);
        if (tools?.ClientPath is null)
        {
            return new DatabaseDiscoverResult
            {
                Ok = false, Engine = t.Engine, ToolsFound = false,
                Error = "The MySQL/MariaDB client tools (mysql, mysqldump) were not found on this machine. Install them (or the server's bin folder) and try again."
            };
        }

        var env = new Dictionary<string, string?>();
        MySqlLocator.ApplyPassword(env, password);
        var (code, stdout, stderr) = await RunAsync(tools.ClientPath, MySqlDiscoverArguments(t), env, null, ct).ConfigureAwait(false);
        if (code != 0)
        {
            return new DatabaseDiscoverResult { Ok = false, Engine = t.Engine, ToolsFound = true, Error = MySqlLocator.FriendlyError(stderr, t.Host, t.Port, t.Username) };
        }

        var (version, databases) = ParseMySqlDiscoverOutput(stdout);
        return new DatabaseDiscoverResult
        {
            Ok = true, Engine = t.Engine, ToolsFound = true,
            ServerVersion = version is null ? null : (version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "MariaDB " : "MySQL ") + version,
            Databases = databases
        };
    }

    /// <summary>First line = VERSION(); then "name\tsize" rows (size "NULL" when unknown). System schemas are dropped.</summary>
    public static (string? Version, List<DiscoveredDatabase> Databases) ParseMySqlDiscoverOutput(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0)
        {
            return (null, []);
        }

        var databases = new List<DiscoveredDatabase>();
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split('\t');
            var name = cols[0];
            if (name.Length == 0 || MySqlLocator.SystemDatabases.Contains(name))
            {
                continue;
            }

            long? size = cols.Length > 1 && long.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            databases.Add(new DiscoveredDatabase { Name = name, SizeBytes = size });
        }

        return (lines[0].Trim(), databases);
    }

    // ------------------------------------------------------------------ PostgreSQL

    private const char FieldSeparator = '\u001f';

    public const string PostgresDatabasesSql =
        "SELECT datname, CASE WHEN has_database_privilege(datname, 'CONNECT') THEN pg_database_size(datname) END " +
        "FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname";

    public static List<string> PostgresDiscoverArguments(DiscoverTarget t)
    {
        var args = new List<string>();
        if (!t.UsePeerAuth)
        {
            args.AddRange(["-h", t.Host]);
        }

        args.AddRange(["-p", t.Port.ToString(CultureInfo.InvariantCulture), "-U", t.Username, "-d", "postgres",
            "-X", "-A", "-t", "-F", FieldSeparator.ToString(), "--no-password",
            "-c", "SELECT version()", "-c", PostgresDatabasesSql]);
        return args;
    }

    private static async Task<DatabaseDiscoverResult> DiscoverPostgresAsync(DiscoverTarget t, string? password, CancellationToken ct)
    {
        var tools = PostgresLocator.FindNewest(t.BinDirectory);
        var psql = tools?.Tool("psql");
        if (psql is null || !File.Exists(psql))
        {
            return new DatabaseDiscoverResult
            {
                Ok = false, Engine = t.Engine, ToolsFound = false,
                Error = "The PostgreSQL client tools (psql, pg_dump) were not found on this machine. Install the PostgreSQL client matching your server version."
            };
        }

        var env = new Dictionary<string, string?>
        {
            ["PGPASSWORD"] = string.IsNullOrEmpty(password) ? null : password,
            ["PGCONNECT_TIMEOUT"] = "15",
            ["PGAPPNAME"] = "cloudorc-backup"
        };

        var (file, prefix) = PeerPrefix(psql, t.UsePeerAuth);
        var args = prefix.Concat(PostgresDiscoverArguments(t)).ToList();
        var (code, stdout, stderr) = await RunAsync(file, args, env, OperatingSystem.IsWindows() ? tools!.BinDirectory : "/", ct).ConfigureAwait(false);
        if (code != 0)
        {
            return new DatabaseDiscoverResult { Ok = false, Engine = t.Engine, ToolsFound = true, Error = PostgresFriendlyError(stderr, t) };
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var databases = lines.Skip(1).Select(l => l.Split(FieldSeparator)).Where(c => c[0].Length > 0)
            .Select(c => new DiscoveredDatabase
            {
                Name = c[0],
                SizeBytes = c.Length > 1 && long.TryParse(c[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null
            }).ToList();
        var version = lines.FirstOrDefault()?.Trim();
        return new DatabaseDiscoverResult
        {
            Ok = true, Engine = t.Engine, ToolsFound = true,
            ServerVersion = version is null ? null : version.Split(',')[0], // "PostgreSQL 16.4 on x86_64..., compiled by ..." → "PostgreSQL 16.4 on x86_64..."
            Databases = databases
        };
    }

    private static string PostgresFriendlyError(string stderr, DiscoverTarget t)
    {
        var last = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no details";
        if (stderr.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) || stderr.Contains("no password supplied", StringComparison.OrdinalIgnoreCase))
        {
            return $"PostgreSQL login failed for user '{t.Username}' - check the user name and password.";
        }

        if (stderr.Contains("Peer authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return $"Peer authentication failed: the PostgreSQL user '{t.Username}' must match the local postgres OS account.";
        }

        if (stderr.Contains("could not connect", StringComparison.OrdinalIgnoreCase) || stderr.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("could not translate host name", StringComparison.OrdinalIgnoreCase) || stderr.Contains("timeout expired", StringComparison.OrdinalIgnoreCase))
        {
            return $"Could not reach PostgreSQL at {t.Host}:{t.Port} - check that the server is running and accepts connections ({last}).";
        }

        return last;
    }

    private static (string File, string[] Prefix) PeerPrefix(string tool, bool usePeerAuth)
    {
        if (!usePeerAuth || OperatingSystem.IsWindows())
        {
            return (tool, []);
        }

        foreach (var runuser in new[] { "/usr/sbin/runuser", "/sbin/runuser" })
        {
            if (File.Exists(runuser))
            {
                return (runuser, ["-u", "postgres", "--", tool]);
            }
        }

        return ("sudo", ["-n", "-u", "postgres", "--", tool]);
    }

    // ------------------------------------------------------------------ process

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string file, IReadOnlyList<string> args, IDictionary<string, string?> env, string? workingDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env)
        {
            if (v is null)
            {
                start.Environment.Remove(k);
            }
            else
            {
                start.Environment[k] = v;
            }
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} could not be started.");
            process.StandardInput.Close(); // never wait for an interactive password prompt
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            return (process.ExitCode, await stdout.ConfigureAwait(false), SecretRedactor.Redact(await stderr.ConfigureAwait(false)) ?? "");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, "", $"{Path.GetFileName(file)} could not be started: {ex.Message}");
        }
    }
}
