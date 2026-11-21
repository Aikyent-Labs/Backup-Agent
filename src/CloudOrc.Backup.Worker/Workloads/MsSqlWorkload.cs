using System.Data;
using System.Diagnostics;
using System.Globalization;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Shared;
using CloudOrc.Backup.Worker.Engines;
using Microsoft.Data.SqlClient;

namespace CloudOrc.Backup.Worker.Workloads;

/// <summary>
/// Microsoft SQL Server backup and restore with SQL Server's own BACKUP / RESTORE.
/// <list type="bullet">
/// <item>Backup: <c>BACKUP DATABASE ... WITH COPY_ONLY</c> (the customer's own backup chain - log backups,
/// differentials - is never disturbed) and NO_COMPRESSION (so the engine deduplicates across runs).
/// SQL Server writes the .bak into its own default backup folder (its service account can write there;
/// the agent's data folder is locked down) under CloudOrcBackup\&lt;jobId&gt;; the worker streams that file
/// into the engine as "&lt;db&gt;.bak" and deletes it again - always, also on failure or cancel.</item>
/// <item>Restore: "file" (the .bak in a folder), "new-database" (RESTORE ... WITH MOVE into new files; fails
/// if the database exists) or "replace" (overwrite the original database - only when confirmed).</item>
/// <item>Login: Windows authentication as the agent service account (NT AUTHORITY\SYSTEM) or a SQL login
/// whose password arrives in the job's grant only.</item>
/// </list>
/// The SQL Server must run on this machine: the .bak is written by the server and read by the worker.
/// </summary>
public sealed class MsSqlWorkload(DatabaseSourceSpec spec, string? password, string jobId, BackupRetention retention, Action<string, string> log)
    : IDatabaseWorkload
{
    private const string TempFolderName = "CloudOrcBackup";

    public string DisplayName => "SQL Server";

    public static string VirtualRoot(DatabaseSourceSpec spec)
    {
        var instance = string.IsNullOrWhiteSpace(spec.Instance)
            ? spec.Port.ToString(CultureInfo.InvariantCulture)
            : spec.Instance.Trim();
        return DatabaseVirtualPaths.Root(BackupSourceKinds.MsSql, $"{DatabaseVirtualPaths.SafeSegment(spec.Host)}_{DatabaseVirtualPaths.SafeSegment(instance)}");
    }

    public static string VirtualPathFor(DatabaseSourceSpec spec, string database) =>
        DatabaseVirtualPaths.Child(VirtualRoot(spec), DatabaseVirtualPaths.FileStem(database));

    string IDatabaseWorkload.VirtualPathFor(string database) => VirtualPathFor(spec, database);

    public string FileNameFor(string database) => DatabaseVirtualPaths.FileStem(database) + ".bak";

    private string DataSource => MsSqlSupport.DataSource(spec);

    private string Friendly(Exception ex, string? database = null) => MsSqlSupport.FriendlyError(ex, DataSource, spec.UseWindowsAuth, spec.Username, database);

    private Task<SqlConnection> OpenAsync(CancellationToken ct) => MsSqlSupport.OpenAsync(MsSqlSupport.ConnectionString(spec, password), ct);

    // ------------------------------------------------------------------ backup

    public async Task<(IReadOnlyList<string> Databases, string? Error)> ResolveDatabasesAsync(CancellationToken ct)
    {
        if (spec.Databases.Count > 0)
        {
            var invalid = spec.Databases.FirstOrDefault(d => !DatabaseNameRules.IsValid(BackupSourceKinds.MsSql, d));
            return invalid is null ? (spec.Databases, null) : ([], $"Invalid database name in policy: {invalid}");
        }

        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            var list = (await MsSqlSupport.ListDatabasesAsync(connection, ct).ConfigureAwait(false))
                .Select(d => d.Name)
                .Where(d => DatabaseNameRules.IsValid(BackupSourceKinds.MsSql, d))
                .Where(d => !spec.ExcludeDatabases.Contains(d, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return list.Count == 0 ? ([], "No user databases to back up were found on this SQL Server instance.") : (list, null);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return ([], $"Could not list databases: {Friendly(ex)}");
        }
    }

    public Task<DumpOutcome?> DumpGlobalsAsync(IBackupEngine engine, EngineContext ctx, CancellationToken ct) => Task.FromResult<DumpOutcome?>(null);

    public async Task<DumpOutcome> DumpDatabaseAsync(IBackupEngine engine, EngineContext ctx, string database, Action<long> onBytes, CancellationToken ct)
    {
        string? tempDir = null;
        try
        {
            string bakFile;
            await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
            {
                var (dir, error) = await PrepareTempFolderAsync(connection, SafeName(jobId), ct).ConfigureAwait(false);
                if (dir is null)
                {
                    return Fail(database, BackupReasonCodes.SourceNotFound, error!);
                }

                tempDir = dir;
                bakFile = Path.Combine(tempDir, MsSqlSupport.SafeFileSegment(database) + ".bak");
                var lastPercent = -1;
                connection.InfoMessage += (_, e) =>
                {
                    // "10 percent processed." - log every 25 %.
                    var text = e.Message;
                    var i = text.IndexOf(" percent", StringComparison.OrdinalIgnoreCase);
                    if (i > 0 && int.TryParse(text[..i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p / 25 > lastPercent / 25)
                    {
                        lastPercent = p;
                        log("info", $"SQL Server backup of '{database}': {p}%");
                    }
                };

                await using var command = new SqlCommand(MsSqlSupport.BackupDatabaseSql(database, bakFile), connection) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (!File.Exists(bakFile))
            {
                return Fail(database, BackupReasonCodes.SourceReadErrors,
                    $"SQL Server reported the backup of '{database}' as done, but the file is not on this machine - the SQL Server must run on the same machine as the Backup Agent.");
            }

            return await DatabaseStreaming.StreamAsync(engine, ctx, retention, database, VirtualPathFor(spec, database), FileNameFor(database),
                async (input, counted, token) =>
                {
                    await using var file = new FileStream(bakFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                    await DatabaseStreaming.CopyAsync(file, input, counted, token).ConfigureAwait(false);
                    return (true, null);
                }, "SQL Server backup", onBytes, log, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("SQL Server backup cancelled.", ex, ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            var message = ex is SqlException or InvalidOperationException ? Friendly(ex, database) : ex.Message;
            log("warning", $"SQL Server backup of '{database}' failed: {message}");
            return Fail(database, BackupReasonCodes.SourceReadErrors, $"SQL Server backup of '{database}' failed: {message}");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ------------------------------------------------------------------ restore

    /// <summary>RESTORE ... WITH MOVE into new files under the instance's data / log folders. Fails if the database exists.</summary>
    public async Task<(bool Ok, string Message)> RestoreAsNewDatabaseAsync(string bakFile, string newDatabase, CancellationToken ct)
    {
        if (!DatabaseNameRules.IsValid(BackupSourceKinds.MsSql, newDatabase))
        {
            return (false, $"'{newDatabase}' is not a valid SQL Server database name.");
        }

        string? tempDir = null;
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            if (await MsSqlSupport.DatabaseExistsAsync(connection, newDatabase, ct).ConfigureAwait(false))
            {
                return (false, $"database '{newDatabase}' already exists - choose another name (a restore into a new database never overwrites an existing one).");
            }

            var (dir, error) = await PrepareTempFolderAsync(connection, "restore-" + SafeName(jobId), ct).ConfigureAwait(false);
            if (dir is null)
            {
                return (false, error!);
            }

            tempDir = dir;
            var serverBak = await CopyIntoAsync(bakFile, tempDir, ct).ConfigureAwait(false);
            var files = await ReadFileListAsync(connection, serverBak, ct).ConfigureAwait(false);
            var (dataDir, logDir) = await MsSqlSupport.DefaultDataLogDirectoriesAsync(connection, ct).ConfigureAwait(false);
            if (dataDir is null || logDir is null)
            {
                return (false, "the SQL Server default data folder could not be determined.");
            }

            var moves = MsSqlSupport.PlanMoves(newDatabase, files, dataDir, logDir);
            await using var command = new SqlCommand(MsSqlSupport.RestoreAsNewDatabaseSql(newDatabase, serverBak, moves), connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return (true, $"Database restored into new database '{newDatabase}'.");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (false, ex is SqlException or InvalidOperationException ? Friendly(ex, newDatabase) : ex.Message);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    /// <summary>
    /// Overwrites the ORIGINAL database (the caller has checked confirmReplace): SINGLE_USER WITH ROLLBACK
    /// IMMEDIATE, RESTORE WITH REPLACE, then MULTI_USER again - also when the restore fails.
    /// </summary>
    public async Task<(bool Ok, string Message)> ReplaceDatabaseAsync(string bakFile, string? targetDatabase, CancellationToken ct)
    {
        string? tempDir = null;
        string? database = targetDatabase;
        var exists = false;
        SqlConnection? connection = null;
        try
        {
            connection = await OpenAsync(ct).ConfigureAwait(false);
            var (dir, error) = await PrepareTempFolderAsync(connection, "restore-" + SafeName(jobId), ct).ConfigureAwait(false);
            if (dir is null)
            {
                return (false, error!);
            }

            tempDir = dir;
            var serverBak = await CopyIntoAsync(bakFile, tempDir, ct).ConfigureAwait(false);
            database ??= await ReadDatabaseNameAsync(connection, serverBak, ct).ConfigureAwait(false);
            if (!DatabaseNameRules.IsValid(BackupSourceKinds.MsSql, database))
            {
                return (false, "the name of the database to replace could not be determined.");
            }

            exists = await MsSqlSupport.DatabaseExistsAsync(connection, database!, ct).ConfigureAwait(false);
            log("warning", $"Replacing SQL Server database '{database}' (confirmed) - open connections are rolled back.");
            await using (var command = new SqlCommand(MsSqlSupport.ReplaceDatabaseSql(database!, serverBak, exists), connection) { CommandTimeout = 0 })
            {
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return (true, $"Database '{database}' was replaced with the backup.");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (false, ex is SqlException or InvalidOperationException ? Friendly(ex, database) : ex.Message);
        }
        finally
        {
            if (exists && database is not null && connection is not null)
            {
                await TrySetMultiUserAsync(connection, database).ConfigureAwait(false);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            Cleanup(tempDir);
        }
    }

    private async Task TrySetMultiUserAsync(SqlConnection connection, string database)
    {
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await using var command = new SqlCommand(MsSqlSupport.MultiUserSql(database), connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            log("warning", $"Could not set database '{database}' back to MULTI_USER: {Friendly(ex, database)}. Run: {MsSqlSupport.MultiUserSql(database)}");
        }
    }

    private static async Task<List<(string LogicalName, string Type)>> ReadFileListAsync(SqlConnection connection, string serverBak, CancellationToken ct)
    {
        var files = new List<(string, string)>();
        await using var command = new SqlCommand(MsSqlSupport.FileListSql(serverBak), connection) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var logical = reader.GetOrdinal("LogicalName");
        var type = reader.GetOrdinal("Type");
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            files.Add((reader.GetString(logical), Convert.ToString(reader.GetValue(type), CultureInfo.InvariantCulture) ?? "D"));
        }

        return files;
    }

    private static async Task<string?> ReadDatabaseNameAsync(SqlConnection connection, string serverBak, CancellationToken ct)
    {
        await using var command = new SqlCommand(MsSqlSupport.HeaderSql(serverBak), connection) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetString(reader.GetOrdinal("DatabaseName")) : null;
    }

    // ------------------------------------------------------------------ temp folder in the instance's backup folder

    private async Task<(string? Directory, string? Error)> PrepareTempFolderAsync(SqlConnection connection, string leaf, CancellationToken ct)
    {
        var backupDir = await MsSqlSupport.DefaultBackupDirectoryAsync(connection, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(backupDir))
        {
            return (null, "SQL Server's default backup folder could not be determined (the login may lack permission to read it).");
        }

        if (!Directory.Exists(backupDir))
        {
            return (null, $"SQL Server's backup folder '{backupDir}' is not on this machine. The Backup Agent must run on the SQL Server machine.");
        }

        var dir = Path.Combine(backupDir, TempFolderName, leaf);
        Directory.CreateDirectory(dir);
        GiveToSqlServiceAccount(Path.Combine(backupDir, TempFolderName), backupDir);
        GiveToSqlServiceAccount(dir, backupDir);
        return (dir, null);
    }

    /// <summary>
    /// Windows: the new folder inherits the backup folder's ACL (SQL Server's service SID has full control).
    /// Linux: the worker runs as root, so hand the folder to the backup folder's owner (mssql).
    /// </summary>
    private static void GiveToSqlServiceAccount(string dir, string referenceDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var chown = Process.Start(new ProcessStartInfo("chown", ["--reference=" + referenceDir, dir]) { UseShellExecute = false, CreateNoWindow = true });
            chown?.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static async Task<string> CopyIntoAsync(string bakFile, string tempDir, CancellationToken ct)
    {
        var destination = Path.Combine(tempDir, "restore.bak");
        await using (var source = new FileStream(bakFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await source.CopyToAsync(target, 1 << 20, ct).ConfigureAwait(false);
        }

        return destination;
    }

    /// <summary>Deletes the job's temp folder (and CloudOrcBackup when it is empty). Never throws.</summary>
    private void Cleanup(string? tempDir)
    {
        if (tempDir is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            var parent = Path.GetDirectoryName(tempDir);
            if (parent is not null && Path.GetFileName(parent) == TempFolderName && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("warning", $"Could not delete the temporary SQL Server backup folder {tempDir}: {ex.Message}");
        }
    }

    private static string SafeName(string value) => DatabaseVirtualPaths.SafeSegment(value);

    private static DumpOutcome Fail(string database, string reason, string message) =>
        new(database, new SnapshotOutcome(false, reason, message), 0);
}
