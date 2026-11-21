using System.Globalization;
using System.Text;
using CloudOrc.Agent.Contracts.Backup;
using Microsoft.Data.SqlClient;

namespace CloudOrc.Backup.Shared;

/// <summary>
/// Microsoft SQL Server helpers shared by the agent (database.discover) and the worker (backup /
/// restore): connection strings, T-SQL text (every identifier [quoted]] and every literal N'quoted''),
/// server folder lookup and customer-friendly error messages. The T-SQL builders are pure functions so
/// they are unit-testable without a server.
/// </summary>
public static class MsSqlSupport
{
    public const int DefaultPort = 1433;

    /// <summary>
    /// "host,port" for a default instance; "host\instance" for a named instance (SQL Server Browser finds
    /// the port) or "host\instance,port" when a non-default port is given explicitly.
    /// </summary>
    public static string DataSource(string? host, int port, string? instance)
    {
        var h = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (!string.IsNullOrWhiteSpace(instance) && !string.Equals(instance.Trim(), "MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
        {
            var named = $@"{h}\{instance.Trim()}";
            return port is > 0 and <= 65535 && port != DefaultPort ? named + "," + port.ToString(CultureInfo.InvariantCulture) : named;
        }

        return port is > 0 and <= 65535 ? h + "," + port.ToString(CultureInfo.InvariantCulture) : h;
    }

    public static string DataSource(DatabaseSourceSpec spec) => DataSource(spec.Host, spec.Port, spec.Instance);

    public static string ConnectionString(DatabaseSourceSpec spec, string? password, string database = "master", int connectTimeoutSeconds = 15) =>
        ConnectionString(spec.Host, spec.Port, spec.Instance, spec.UseWindowsAuth, spec.Username, password, database, connectTimeoutSeconds);

    /// <summary>Integrated security (the agent's service account) or a SQL login; TrustServerCertificate, Encrypt=Optional.</summary>
    public static string ConnectionString(string? host, int port, string? instance, bool useWindowsAuth, string? username, string? password,
        string database = "master", int connectTimeoutSeconds = 15)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = DataSource(host, port, instance),
            InitialCatalog = database,
            TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Optional,
            ConnectTimeout = Math.Clamp(connectTimeoutSeconds, 1, 120),
            ApplicationName = "cloudorc-backup",
            Pooling = false
        };

        if (useWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = username ?? "";
            builder.Password = password ?? "";
        }

        return builder.ConnectionString;
    }

    // ------------------------------------------------------------------ T-SQL text

    /// <summary>[name] with every ']' doubled - safe for any SQL Server identifier.</summary>
    public static string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>N'text' with every quote doubled.</summary>
    public static string QuoteLiteral(string text) => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>COPY_ONLY never disturbs the customer's own backup chain; no SQL compression so the engine can deduplicate.</summary>
    public static string BackupDatabaseSql(string database, string file) =>
        $"BACKUP DATABASE {QuoteIdentifier(database)} TO DISK = {QuoteLiteral(file)} WITH COPY_ONLY, INIT, CHECKSUM, NO_COMPRESSION, STATS = 5";

    public static string FileListSql(string file) => $"RESTORE FILELISTONLY FROM DISK = {QuoteLiteral(file)}";

    public static string HeaderSql(string file) => $"RESTORE HEADERONLY FROM DISK = {QuoteLiteral(file)}";

    public static string RestoreAsNewDatabaseSql(string newDatabase, string file, IReadOnlyList<(string LogicalName, string TargetPath)> moves)
    {
        var sql = new StringBuilder();
        sql.Append("RESTORE DATABASE ").Append(QuoteIdentifier(newDatabase)).Append(" FROM DISK = ").Append(QuoteLiteral(file)).Append(" WITH ");
        foreach (var (logical, target) in moves)
        {
            sql.Append("MOVE ").Append(QuoteLiteral(logical)).Append(" TO ").Append(QuoteLiteral(target)).Append(", ");
        }

        sql.Append("RECOVERY, STATS = 5");
        return sql.ToString();
    }

    /// <summary>Single batch, so nobody can take the single-user slot between the ALTER and the RESTORE.</summary>
    public static string ReplaceDatabaseSql(string database, string file, bool databaseExists)
    {
        var q = QuoteIdentifier(database);
        var restore = $"RESTORE DATABASE {q} FROM DISK = {QuoteLiteral(file)} WITH REPLACE, RECOVERY, STATS = 5;";
        return databaseExists ? $"ALTER DATABASE {q} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\n{restore}" : restore;
    }

    public static string MultiUserSql(string database) => $"ALTER DATABASE {QuoteIdentifier(database)} SET MULTI_USER;";

    public const string DatabaseExistsSql = "SELECT DB_ID(@name)";

    public const string VersionSql =
        "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128))";

    /// <summary>Online user databases (database_id &gt; 4 skips master, tempdb, model, msdb) with size when visible.</summary>
    public const string ListDatabasesSql =
        "SELECT d.name, d.recovery_model_desc, CAST(SUM(CAST(mf.size AS bigint)) * 8192 AS bigint) " +
        "FROM sys.databases d LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id " +
        "WHERE d.database_id > 4 AND d.state_desc = 'ONLINE' " +
        "GROUP BY d.name, d.recovery_model_desc ORDER BY d.name";

    /// <summary>
    /// MOVE targets for a restore under a new name: data files &lt;new&gt;_&lt;n&gt;.mdf (first) / .ndf, log files
    /// &lt;new&gt;_&lt;n&gt;.ldf, FILESTREAM / full-text containers &lt;new&gt;_&lt;n&gt; folders - all inside the
    /// instance's default data / log folders, never over an existing database's files.
    /// </summary>
    public static IReadOnlyList<(string LogicalName, string TargetPath)> PlanMoves(string newDatabase, IReadOnlyList<(string LogicalName, string Type)> files,
        string dataDirectory, string logDirectory)
    {
        var baseName = SafeFileSegment(newDatabase);
        var moves = new List<(string, string)>();
        var firstData = true;
        for (var i = 0; i < files.Count; i++)
        {
            var (logical, type) = files[i];
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            string target = type.ToUpperInvariant() switch
            {
                "L" => JoinServerPath(logDirectory, $"{baseName}_{n}.ldf"),
                "D" when firstData => JoinServerPath(dataDirectory, $"{baseName}_{n}.mdf"),
                "D" => JoinServerPath(dataDirectory, $"{baseName}_{n}.ndf"),
                _ => JoinServerPath(dataDirectory, $"{baseName}_{n}")
            };
            if (type.Equals("D", StringComparison.OrdinalIgnoreCase))
            {
                firstData = false;
            }

            moves.Add((logical, target));
        }

        return moves;
    }

    /// <summary>Joins a server-side folder and a name with the folder's own separator.</summary>
    public static string JoinServerPath(string directory, string name)
    {
        var separator = directory.Contains('\\', StringComparison.Ordinal) || !directory.Contains('/', StringComparison.Ordinal) && OperatingSystem.IsWindows() ? '\\' : '/';
        return directory.TrimEnd('\\', '/') + separator + name;
    }

    /// <summary>File-system-safe name; names that had to be changed get a short hash so two databases never collide.</summary>
    public static string SafeFileSegment(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var safe = chars.Length == 0 ? "_" : new string(chars);
        if (safe == name)
        {
            return safe;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return safe + "_" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ server queries

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<string> ServerVersionAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(VersionSql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return "SQL Server";
        }

        string? Get(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return $"Microsoft SQL Server {Get(0)} {Get(2)} ({Get(1)})".Replace("  ", " ", StringComparison.Ordinal);
    }

    public static async Task<List<DiscoveredDatabase>> ListDatabasesAsync(SqlConnection connection, CancellationToken ct)
    {
        var result = new List<DiscoveredDatabase>();
        await using var command = new SqlCommand(ListDatabasesSql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DiscoveredDatabase
            {
                Name = reader.GetString(0),
                RecoveryModel = reader.IsDBNull(1) ? null : reader.GetString(1),
                SizeBytes = reader.IsDBNull(2) ? null : reader.GetInt64(2)
            });
        }

        return result;
    }

    public static async Task<bool> DatabaseExistsAsync(SqlConnection connection, string database, CancellationToken ct)
    {
        await using var command = new SqlCommand(DatabaseExistsSql, connection) { CommandTimeout = 30 };
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = database });
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is not null && value is not DBNull;
    }

    /// <summary>
    /// The instance's default backup folder: SERVERPROPERTY('InstanceDefaultBackupPath') (SQL Server 2019+),
    /// else the BackupDirectory registry value, else the "Backup" folder next to the error log's "Log" folder.
    /// </summary>
    public static async Task<string?> DefaultBackupDirectoryAsync(SqlConnection connection, CancellationToken ct)
    {
        var path = await ScalarStringAsync(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        path = await ScalarStringAsync(connection,
            "DECLARE @d nvarchar(4000); EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\\Microsoft\\MSSQLServer\\MSSQLServer', N'BackupDirectory', @d OUTPUT; SELECT @d;", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var errorLog = await ScalarStringAsync(connection, "SELECT CAST(SERVERPROPERTY('ErrorLogFileName') AS nvarchar(4000))", ct).ConfigureAwait(false);
        return BackupDirectoryFromErrorLog(errorLog);
    }

    /// <summary>...\MSSQL\Log\ERRORLOG → ...\MSSQL\Backup.</summary>
    public static string? BackupDirectoryFromErrorLog(string? errorLog)
    {
        if (string.IsNullOrWhiteSpace(errorLog))
        {
            return null;
        }

        var separator = errorLog.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        var logDir = errorLog[..Math.Max(0, errorLog.LastIndexOf(separator))];
        var instanceDir = logDir.Contains(separator, StringComparison.Ordinal) ? logDir[..logDir.LastIndexOf(separator)] : logDir;
        return instanceDir.Length == 0 ? null : instanceDir + separator + "Backup";
    }

    /// <summary>Default data and log folders (SQL Server 2012+), else the folder of master's files.</summary>
    public static async Task<(string? Data, string? Log)> DefaultDataLogDirectoriesAsync(SqlConnection connection, CancellationToken ct)
    {
        var data = await ScalarStringAsync(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000))", ct).ConfigureAwait(false);
        var log = await ScalarStringAsync(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(log))
        {
            var masterData = await ScalarStringAsync(connection, "SELECT TOP (1) physical_name FROM sys.master_files WHERE database_id = 1 AND type = 0", ct).ConfigureAwait(false);
            var masterLog = await ScalarStringAsync(connection, "SELECT TOP (1) physical_name FROM sys.master_files WHERE database_id = 1 AND type = 1", ct).ConfigureAwait(false);
            data = string.IsNullOrWhiteSpace(data) ? ParentOf(masterData) : data;
            log = string.IsNullOrWhiteSpace(log) ? ParentOf(masterLog) ?? data : log;
        }

        return (data, log);
    }

    private static string? ParentOf(string? serverPath)
    {
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            return null;
        }

        var i = serverPath.LastIndexOfAny(['\\', '/']);
        return i > 0 ? serverPath[..i] : null;
    }

    private static async Task<string?> ScalarStringAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value as string;
        }
        catch (SqlException)
        {
            return null; // optional probe (older version / no permission)
        }
    }

    // ------------------------------------------------------------------ errors

    /// <summary>The account Windows authentication uses (the agent service: NT AUTHORITY\SYSTEM).</summary>
    public static string AgentWindowsAccount()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return Environment.UserName;
    }

    public static string FriendlyError(Exception ex, string dataSource, bool useWindowsAuth, string? username, string? database = null)
    {
        if (ex is SqlException sql)
        {
            // BACKUP/RESTORE add a generic "terminating abnormally" (3013) after the real cause.
            var errors = sql.Errors.Cast<SqlError>().ToList();
            var cause = errors.FirstOrDefault(e => e.Number is not (3013 or 3271 or 0)) ?? errors.FirstOrDefault();
            return FriendlyError(cause?.Number ?? sql.Number, cause?.Message ?? sql.Message, dataSource, useWindowsAuth, username, database);
        }

        return ex switch
        {
            OperationCanceledException => "The SQL Server operation timed out or was cancelled.",
            InvalidOperationException or ArgumentException => $"SQL Server connection problem: {ex.Message}",
            _ => ex.Message
        };
    }

    public static string FriendlyError(int number, string message, string dataSource, bool useWindowsAuth, string? username, string? database = null)
    {
        var who = useWindowsAuth ? AgentWindowsAccount() : username ?? "";
        var onDb = database is null ? "" : $" on database '{database}'";
        switch (number)
        {
            case 18456 when useWindowsAuth:
                return $"SQL Server at {dataSource} rejected the Windows login of the backup agent ({who}). The agent runs as NT AUTHORITY\\SYSTEM and needs a SQL login with backup rights - for example run in SQL Server Management Studio: "
                       + "CREATE LOGIN [NT AUTHORITY\\SYSTEM] FROM WINDOWS; ALTER SERVER ROLE sysadmin ADD MEMBER [NT AUTHORITY\\SYSTEM];";
            case 18456:
                return $"SQL Server login '{who}' failed at {dataSource}. Check the password and that SQL Server allows SQL Server authentication (mixed mode). "
                       + "The login needs the db_backupoperator role in every database to back up (or the sysadmin role).";
            case 18452:
                return $"SQL Server at {dataSource} only accepts Windows authentication. Enable 'SQL Server and Windows Authentication mode' or use Windows authentication.";
            case 262 or 229 or 230 or 15247 or 3110:
                return $"Login '{who}' does not have permission for this operation{onDb}. Add it to the db_backupoperator role in that database (backups) or to dbcreator / sysadmin (restores). SQL Server said: {message}";
            case 4060:
                return $"Login '{who}' cannot open database{onDb}: {message}";
            case 1801:
                return $"Database{onDb} already exists - choose another name. A restore into a new database never overwrites an existing one.";
            case 3201 or 3203 or 3241:
                return $"SQL Server could not use the backup file - its service account needs access to the backup folder. SQL Server said: {message}";
            case 3101:
                return $"The database{onDb} is in use and could not be taken over for the restore: {message}";
            case -2:
                return $"SQL Server at {dataSource} did not answer in time.";
            case 2 or 53 or 40 or 26 or 35 or 258 or 1225 or 10060 or 10061 or 11001 or 11004:
                return $"Could not reach SQL Server at {dataSource}. Check the host / instance / port, that the SQL Server service is running and TCP/IP is enabled"
                       + " (named instances also need the SQL Server Browser service).";
            default:
                return $"SQL Server error {number}{onDb}: {message}";
        }
    }
}
