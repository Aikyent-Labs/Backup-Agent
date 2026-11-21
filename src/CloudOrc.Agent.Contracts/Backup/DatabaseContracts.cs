using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// Database part of a <see cref="BackupSource"/> whose kind is "postgresql", "mssql" or "mysql". The
/// source "path" is a stable logical name only (e.g. "postgresql://127.0.0.1:5432",
/// "mssql://127.0.0.1/SQLEXPRESS", "mysql://127.0.0.1:3306"), used as the snapshot identity in the
/// repository; nothing is read from that path on disk.
/// <para>
/// Each database is backed up with the database's own native tool (pg_dump custom format, mysqldump,
/// SQL Server BACKUP DATABASE ... COPY_ONLY) - never by copying live data files (which would not be
/// restorable). Dumps are streamed straight into the backup engine where the tool allows it.
/// </para>
/// No password lives in the policy: <see cref="CredentialRef"/> names a secret the backend delivers in
/// the short-lived storage grant (<see cref="StorageGrant.SourceSecrets"/>) at job time only. On Linux a
/// local PostgreSQL instance can also be reached with "peer" authentication as the postgres OS user,
/// and SQL Server with Windows authentication as the agent's service account.
/// </summary>
public sealed class DatabaseSourceSpec
{
    [JsonPropertyName("host")] public string Host { get; init; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; init; } = 5432;

    /// <summary>Databases to back up; empty = every user database (PostgreSQL: except templates).</summary>
    [JsonPropertyName("databases")] public IReadOnlyList<string> Databases { get; init; } = [];

    /// <summary>Databases never to back up (applies when <see cref="Databases"/> is empty).</summary>
    [JsonPropertyName("excludeDatabases")] public IReadOnlyList<string> ExcludeDatabases { get; init; } = [];

    [JsonPropertyName("username")] public string Username { get; init; } = "postgres";

    /// <summary>Key into the grant's sourceSecrets holding the password; null = no password (peer / trust / Windows auth).</summary>
    [JsonPropertyName("credentialRef")] public string? CredentialRef { get; init; }

    /// <summary>Linux/macOS: run the tools as the "postgres" OS user (local peer authentication).</summary>
    [JsonPropertyName("usePeerAuth")] public bool UsePeerAuth { get; init; }

    /// <summary>Also dump roles/tablespaces (pg_dumpall --globals-only). Needs superuser.</summary>
    [JsonPropertyName("includeGlobals")] public bool IncludeGlobals { get; init; } = true;

    /// <summary>Folder holding the client tools (pg_dump / mysqldump ...); null = found automatically.</summary>
    [JsonPropertyName("binDirectory")] public string? BinDirectory { get; init; }

    /// <summary>"disable", "prefer" (default) or "require" - libpq sslmode.</summary>
    [JsonPropertyName("sslMode")] public string SslMode { get; init; } = "prefer";

    /// <summary>MS SQL Server named instance (e.g. "SQLEXPRESS"); null = default instance.</summary>
    [JsonPropertyName("instance")] public string? Instance { get; init; }

    /// <summary>MS SQL Server: integrated security as the agent's service account (LocalSystem on Windows).</summary>
    [JsonPropertyName("useWindowsAuth")] public bool UseWindowsAuth { get; init; }
}

/// <summary>Restore of a database recovery point (restore.execute with a "database" section).</summary>
public sealed class DatabaseRestoreSpec
{
    /// <summary>"postgresql" (default), "mssql" or "mysql" - the workload that made the recovery point.</summary>
    [JsonPropertyName("engine")] public string Engine { get; init; } = BackupSourceKinds.PostgreSql;

    /// <summary>
    /// "file": only put the dump/backup file in the target folder. "new-database": load it into a NEW
    /// database. "replace" (MS SQL Server only): overwrite the ORIGINAL database - needs <see cref="ConfirmReplace"/>.
    /// </summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = DatabaseRestoreModes.File;

    /// <summary>
    /// "new-database": name of the database to create - must not exist yet. "replace": the database to
    /// overwrite (null = the database name recorded in the backup).
    /// </summary>
    [JsonPropertyName("targetDatabase")] public string? TargetDatabase { get; init; }

    [JsonPropertyName("connection")] public DatabaseSourceSpec? Connection { get; init; }

    /// <summary>Explicit confirmation that "replace" may overwrite the existing database. Without it "replace" is refused.</summary>
    [JsonPropertyName("confirmReplace")] public bool ConfirmReplace { get; init; }
}

public static class DatabaseRestoreModes
{
    public const string File = "file";
    public const string NewDatabase = "new-database";

    /// <summary>Overwrite the ORIGINAL database (MS SQL Server only; requires confirmReplace = true).</summary>
    public const string Replace = "replace";

    /// <summary>File extension of the backup file each engine stores in a recovery point.</summary>
    public static string FileExtensionFor(string? engine) => engine switch
    {
        BackupSourceKinds.MsSql => ".bak",
        BackupSourceKinds.MySql => ".sql",
        _ => ".dump"
    };

    /// <summary>Checks a database restore request; null = acceptable, otherwise a customer-friendly refusal.</summary>
    public static string? Validate(DatabaseRestoreSpec? spec)
    {
        if (spec is null)
        {
            return null;
        }

        if (!BackupSourceKinds.IsDatabase(spec.Engine))
        {
            return $"Unknown database engine '{spec.Engine}' (expected postgresql, mssql or mysql).";
        }

        switch (spec.Mode)
        {
            case File:
                return null;

            case NewDatabase:
                if (string.IsNullOrWhiteSpace(spec.TargetDatabase))
                {
                    return "A name for the new database is required.";
                }

                return DatabaseNameRules.IsValid(spec.Engine, spec.TargetDatabase)
                    ? null
                    : $"'{spec.TargetDatabase}' is not a valid {DatabaseNameRules.DisplayName(spec.Engine)} database name.";

            case Replace:
                if (spec.Engine != BackupSourceKinds.MsSql)
                {
                    return $"Replacing the original database is not supported for {DatabaseNameRules.DisplayName(spec.Engine)}; restore into a new database or as a file instead.";
                }

                if (!spec.ConfirmReplace)
                {
                    return "Replacing the original database overwrites all of its current data, so it must be explicitly confirmed (confirmReplace). The restore was refused.";
                }

                return spec.TargetDatabase is null || DatabaseNameRules.IsValid(spec.Engine, spec.TargetDatabase)
                    ? null
                    : $"'{spec.TargetDatabase}' is not a valid SQL Server database name.";

            default:
                return $"Unknown database restore mode '{spec.Mode}' (expected file, new-database or replace).";
        }
    }
}

/// <summary>Database names each engine accepts in a policy / restore (agent-side safety net).</summary>
public static class DatabaseNameRules
{
    public static string DisplayName(string? engine) => engine switch
    {
        BackupSourceKinds.MsSql => "SQL Server",
        BackupSourceKinds.MySql => "MySQL/MariaDB",
        _ => "PostgreSQL"
    };

    public static bool IsValid(string? engine, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        switch (engine)
        {
            case BackupSourceKinds.MsSql:
                // Any SQL Server identifier (always used [quoted]] in T-SQL): only blank names, surrounding
                // spaces and control characters are refused.
                return name.Length <= 128 && !string.IsNullOrWhiteSpace(name) && name.Trim() == name && !name.Any(char.IsControl);

            case BackupSourceKinds.MySql:
                // Passed to mysqldump as an argument: may never start with '-' (it would be read as an option).
                return name.Length <= 64 && (char.IsAsciiLetterOrDigit(name[0]) || name[0] is '_' or '$')
                       && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '-');

            default:
                return name.Length <= 63 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
                       && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        }
    }
}

/// <summary>
/// database.discover parameters: test a connection and list the databases for the portal's picker.
/// The password is transient - used for this one connection only, never logged, stored or echoed.
/// </summary>
public sealed class DatabaseDiscoverParameters
{
    /// <summary>"postgresql", "mssql" or "mysql".</summary>
    [JsonPropertyName("engine")] public string? Engine { get; init; }
    [JsonPropertyName("host")] public string? Host { get; init; }
    [JsonPropertyName("port")] public int? Port { get; init; }
    [JsonPropertyName("instance")] public string? Instance { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }

    /// <summary>SECRET, transient.</summary>
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonPropertyName("useWindowsAuth")] public bool UseWindowsAuth { get; init; }
    [JsonPropertyName("usePeerAuth")] public bool UsePeerAuth { get; init; }
    [JsonPropertyName("binDirectory")] public string? BinDirectory { get; init; }

    public override string ToString() => $"DatabaseDiscoverParameters(engine={Engine}, host={Host}, port={Port}, instance={Instance}, password=REDACTED)";
}

/// <summary>database.discover result (returned as TypedCommandAcceptance.data).</summary>
public sealed class DatabaseDiscoverResult
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("engine")] public string? Engine { get; init; }
    [JsonPropertyName("serverVersion")] public string? ServerVersion { get; init; }
    [JsonPropertyName("toolsFound")] public bool ToolsFound { get; init; }
    [JsonPropertyName("databases")] public IReadOnlyList<DiscoveredDatabase> Databases { get; init; } = [];
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed class DiscoveredDatabase
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; init; }

    /// <summary>MS SQL recovery model ("FULL", "SIMPLE", "BULK_LOGGED"); null for other engines.</summary>
    [JsonPropertyName("recoveryModel")] public string? RecoveryModel { get; init; }
}
