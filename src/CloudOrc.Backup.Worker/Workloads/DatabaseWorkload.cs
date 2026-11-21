using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Worker.Engines;

namespace CloudOrc.Backup.Worker.Workloads;

/// <summary>Result of backing up one database into the engine.</summary>
public sealed record DumpOutcome(string Database, SnapshotOutcome Snapshot, long DumpBytes);

/// <summary>
/// One database engine (PostgreSQL, MS SQL Server, MySQL/MariaDB) as seen by the job executor: which
/// databases to back up, where each lands in the repository (a stable virtual path) and how one
/// database is backed up into the engine.
/// </summary>
public interface IDatabaseWorkload
{
    /// <summary>"PostgreSQL", "SQL Server", "MySQL/MariaDB" - for log lines.</summary>
    string DisplayName { get; }

    string VirtualPathFor(string database);

    /// <summary>Name of the single file stored per database ("sales.dump", "sales.bak", "sales.sql").</summary>
    string FileNameFor(string database);

    Task<(IReadOnlyList<string> Databases, string? Error)> ResolveDatabasesAsync(CancellationToken ct);

    Task<DumpOutcome> DumpDatabaseAsync(IBackupEngine engine, EngineContext ctx, string database, Action<long> onBytes, CancellationToken ct);

    /// <summary>Instance-wide extras (PostgreSQL roles/tablespaces); null when the engine has none.</summary>
    Task<DumpOutcome?> DumpGlobalsAsync(IBackupEngine engine, EngineContext ctx, CancellationToken ct);
}

/// <summary>Stable repository identities of database backups: OS-native absolute paths that never exist on disk.</summary>
public static class DatabaseVirtualPaths
{
    public static string Root(string engine, string instanceSegment) =>
        OperatingSystem.IsWindows()
            ? $@"C:\CloudOrc-Database\{engine}\{instanceSegment}"
            : $"/cloudorc-database/{engine}/{instanceSegment}";

    public static string Child(string root, string segment) => root + (OperatingSystem.IsWindows() ? "\\" : "/") + segment;

    /// <summary>
    /// File name stem of a database's backup file (and the leaf of its virtual path). The backend gets
    /// the original database name back by stripping the extension, so the stem is the database name
    /// itself whenever that is a valid file name; only names with characters no file system accepts
    /// fall back to a sanitised, hash-suffixed stem.
    /// </summary>
    public static string FileStem(string database)
    {
        var invalid = database.Length == 0 || database is "." or ".." || database.EndsWith('.') || database.EndsWith(' ')
                      || database.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|');
        return invalid ? CloudOrc.Backup.Shared.MsSqlSupport.SafeFileSegment(database) : database;
    }

    /// <summary>"postgresql://host:port", "mysql://host:port", "mssql://host:port" or "mssql://host/INSTANCE" (backend logical source paths).</summary>
    public static bool IsLogicalDatabasePath(string? path) =>
        path is not null && (path.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("mssql://", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("mysql://", StringComparison.OrdinalIgnoreCase));

    public static string SafeSegment(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }
}

/// <summary>
/// Streams a producer (a dump tool's stdout, or a finished backup file) into one engine snapshot.
/// The rule shared by every database workload: when the producer did not finish cleanly the snapshot
/// is deleted again - a partial dump is never reported as a recovery point.
/// </summary>
public static class DatabaseStreaming
{
    /// <param name="produce">Writes the data into the engine stream, calling the counter for every chunk; returns (ok, error).</param>
    public static async Task<DumpOutcome> StreamAsync(IBackupEngine engine, EngineContext ctx, BackupRetention retention, string database, string virtualPath, string fileName,
        Func<Stream, Action<int>, CancellationToken, Task<(bool Ok, string? Error)>> produce, string what, Action<long> onBytes, Action<string, string> log, CancellationToken ct)
    {
        long bytes = 0;
        (bool Ok, string? Error) produced = (false, "the backup data was not produced");

        async Task Producer(Stream engineInput, CancellationToken token)
        {
            try
            {
                produced = await produce(engineInput, n =>
                {
                    bytes += n;
                    onBytes(bytes);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                produced = (false, "cancelled");
                throw;
            }
            catch (IOException ex)
            {
                // The engine stopped reading (it reports its own error) or the source broke off.
                produced = (false, ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                produced = (false, ex.Message);
            }
        }

        var prepared = await engine.PrepareStreamSourceAsync(ctx, virtualPath, retention, ct).ConfigureAwait(false);
        if (!prepared.Ok)
        {
            return new DumpOutcome(database, new SnapshotOutcome(false, prepared.ReasonCode, prepared.Message), 0);
        }

        var snapshot = await engine.BackupStreamAsync(ctx, virtualPath, fileName, Producer, ct).ConfigureAwait(false);
        if (snapshot.Ok && !produced.Ok)
        {
            if (snapshot.SnapshotId is { } id)
            {
                await engine.DeleteSnapshotAsync(ctx, id, CancellationToken.None).ConfigureAwait(false);
            }

            log("warning", $"{what} of '{database}' failed: {produced.Error}");
            return new DumpOutcome(database,
                new SnapshotOutcome(false, BackupReasonCodes.SourceReadErrors, $"{what} of '{database}' failed: {produced.Error}"), bytes);
        }

        return new DumpOutcome(database, snapshot with { Bytes = snapshot.Bytes ?? bytes, Files = 1 }, bytes);
    }

    /// <summary>Copies a source stream in 1 MiB chunks, reporting every chunk.</summary>
    public static async Task CopyAsync(Stream source, Stream destination, Action<int> counted, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            counted(read);
        }
    }
}
