using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Backup;

/// <summary>
/// Database part of a <see cref="BackupSource"/> whose kind is "postgresql". The source "path" is a
/// stable logical name only (e.g. "postgresql://127.0.0.1:5432"), used as the snapshot identity in the
/// repository; nothing is read from that path on disk.
/// <para>
/// Each database is dumped with the database's own native tool (pg_dump, custom format) and streamed
/// straight into the backup engine - no temporary dump file on the server's disk, no copying of live
/// data files (which would not be restorable). Roles and tablespaces are captured once per instance
/// with pg_dumpall --globals-only.
/// </para>
/// No password lives in the policy: <see cref="CredentialRef"/> names a secret the backend delivers in
/// the short-lived storage grant (<see cref="StorageGrant.SourceSecrets"/>) at job time only. On Linux a
/// local instance can also be reached with "peer" authentication as the postgres OS user, which needs
/// no password at all (<see cref="UsePeerAuth"/>).
/// </summary>
public sealed class DatabaseSourceSpec
{
    [JsonPropertyName("host")] public string Host { get; init; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; init; } = 5432;

    /// <summary>Databases to back up; empty = every database except templates.</summary>
    [JsonPropertyName("databases")] public IReadOnlyList<string> Databases { get; init; } = [];

    /// <summary>Databases never to back up (applies when <see cref="Databases"/> is empty).</summary>
    [JsonPropertyName("excludeDatabases")] public IReadOnlyList<string> ExcludeDatabases { get; init; } = [];

    [JsonPropertyName("username")] public string Username { get; init; } = "postgres";

    /// <summary>Key into the grant's sourceSecrets holding the password; null = no password (peer / trust).</summary>
    [JsonPropertyName("credentialRef")] public string? CredentialRef { get; init; }

    /// <summary>Linux/macOS: run the tools as the "postgres" OS user (local peer authentication).</summary>
    [JsonPropertyName("usePeerAuth")] public bool UsePeerAuth { get; init; }

    /// <summary>Also dump roles/tablespaces (pg_dumpall --globals-only). Needs superuser.</summary>
    [JsonPropertyName("includeGlobals")] public bool IncludeGlobals { get; init; } = true;

    /// <summary>Folder holding pg_dump / pg_restore; null = newest client found on the machine.</summary>
    [JsonPropertyName("binDirectory")] public string? BinDirectory { get; init; }

    /// <summary>"disable", "prefer" (default) or "require" - libpq sslmode.</summary>
    [JsonPropertyName("sslMode")] public string SslMode { get; init; } = "prefer";
}

/// <summary>Restore of a PostgreSQL database dump (restore.execute with a "database" section).</summary>
public sealed class DatabaseRestoreSpec
{
    /// <summary>"file": only put the .dump file in the target folder. "new-database": pg_restore into a NEW database.</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = DatabaseRestoreModes.File;

    /// <summary>Name of the database to create for "new-database". Must not exist yet - production is never overwritten.</summary>
    [JsonPropertyName("targetDatabase")] public string? TargetDatabase { get; init; }

    [JsonPropertyName("connection")] public DatabaseSourceSpec? Connection { get; init; }
}

public static class DatabaseRestoreModes
{
    public const string File = "file";
    public const string NewDatabase = "new-database";
}
