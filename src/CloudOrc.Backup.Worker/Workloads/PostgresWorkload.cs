using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Shared;
using CloudOrc.Backup.Worker.Engines;

namespace CloudOrc.Backup.Worker.Workloads;

/// <summary>
/// PostgreSQL backup and restore with PostgreSQL's own tools - never by copying live data files
/// (a copy of a running cluster is not restorable).
/// <list type="bullet">
/// <item>Backup: every selected database is dumped with <c>pg_dump --format=custom</c> and streamed
/// straight into the backup engine as "&lt;db&gt;.dump" (no temporary file on the server). Roles and
/// tablespaces go into "globals.sql" (pg_dumpall --globals-only) when the account may read them.
/// A dump that fails part-way is deleted from the repository - a partial dump is never reported as
/// a recovery point.</item>
/// <item>Restore: the .dump file is restored into a folder; optionally pg_restore loads it into a NEW
/// database (createdb fails if the name exists - a production database is never overwritten).</item>
/// <item>Secrets: the password arrives in the job's grant and reaches the tools only as PGPASSWORD
/// in their environment. Linux "peer" authentication runs the tools as the postgres OS user.</item>
/// </list>
/// </summary>
public sealed partial class PostgresWorkload(PostgresLocator.ClientTools tools, DatabaseSourceSpec spec, string? password, BackupRetention retention, Action<string, string> log)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

    public sealed record DumpTarget(string Database, string VirtualPath, string FileName);

    public sealed record DumpOutcome(string Database, SnapshotOutcome Snapshot, long DumpBytes);

    /// <summary>Stable repository identity of one database: OS-native absolute path that never exists on disk.</summary>
    public static string VirtualRoot(DatabaseSourceSpec spec)
    {
        var instance = $"{SafeSegment(spec.Host)}_{spec.Port.ToString(CultureInfo.InvariantCulture)}";
        return OperatingSystem.IsWindows()
            ? $@"C:\CloudOrc-Database\postgresql\{instance}"
            : $"/cloudorc-database/postgresql/{instance}";
    }

    public static string VirtualPathFor(DatabaseSourceSpec spec, string database) =>
        VirtualRoot(spec) + (OperatingSystem.IsWindows() ? "\\" : "/") + SafeSegment(database);

    public static bool IsVirtualDatabasePath(string? path) =>
        path is not null && (path.StartsWith(@"C:\CloudOrc-Database\", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("/cloudorc-database/", StringComparison.Ordinal));

    /// <summary>Databases to dump: the policy's list, or every connectable non-template database minus exclusions.</summary>
    public async Task<(IReadOnlyList<string> Databases, string? Error)> ResolveDatabasesAsync(CancellationToken ct)
    {
        if (spec.Databases.Count > 0)
        {
            var invalid = spec.Databases.FirstOrDefault(d => !IsSafeDatabaseName(d));
            return invalid is null ? (spec.Databases, null) : ([], $"Invalid database name in policy: {invalid}");
        }

        var psql = tools.Tool("psql");
        if (!File.Exists(psql))
        {
            return ([], "psql was not found next to pg_dump; list the databases to back up in the protection plan.");
        }

        var args = ConnectionArgs("postgres");
        args.AddRange(["-At", "-c", "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname"]);
        var (code, stdout, stderr) = await RunCaptureAsync(psql, args, ListTimeout, ct).ConfigureAwait(false);
        if (code != 0)
        {
            return ([], $"Could not list databases: {LastLine(stderr)}");
        }

        var list = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsSafeDatabaseName)
            .Where(d => !spec.ExcludeDatabases.Contains(d, StringComparer.Ordinal))
            .ToList();
        return list.Count == 0 ? ([], "No databases to back up were found on this PostgreSQL server.") : (list, null);
    }

    /// <summary>Dumps one database through the engine. Deletes the snapshot again if pg_dump did not finish cleanly.</summary>
    public async Task<DumpOutcome> DumpDatabaseAsync(IBackupEngine engine, EngineContext ctx, string database, Action<long> onBytes, CancellationToken ct)
    {
        var target = new DumpTarget(database, VirtualPathFor(spec, database), SafeSegment(database) + ".dump");
        var args = ConnectionArgs(database);
        args.AddRange(["--format=custom", "--compress=0", "--no-password", "--verbose"]);
        return await StreamAsync(engine, ctx, target, tools.Tool("pg_dump"), args, onBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Roles and tablespaces (needs a superuser). Failure is a warning, never a failed backup.</summary>
    public async Task<DumpOutcome?> DumpGlobalsAsync(IBackupEngine engine, EngineContext ctx, CancellationToken ct)
    {
        var pgDumpAll = tools.Tool("pg_dumpall");
        if (!spec.IncludeGlobals || !File.Exists(pgDumpAll))
        {
            return null;
        }

        var target = new DumpTarget("_globals", VirtualPathFor(spec, "_globals"), "globals.sql");
        var args = ConnectionArgs(null);
        args.AddRange(["--globals-only", "--no-password"]);
        return await StreamAsync(engine, ctx, target, pgDumpAll, args, _ => { }, ct).ConfigureAwait(false);
    }

    /// <summary>pg_restore of a restored .dump file into a NEW database.</summary>
    public async Task<(bool Ok, string Message)> RestoreIntoNewDatabaseAsync(string dumpFile, string targetDatabase, CancellationToken ct)
    {
        if (!IsSafeDatabaseName(targetDatabase))
        {
            return (false, "The new database name may only contain letters, digits, '_' and '-'.");
        }

        var createdb = tools.Tool("createdb");
        var args = ConnectionArgs(null);
        args.AddRange(["--no-password", targetDatabase]);
        var (createCode, _, createErr) = await RunCaptureAsync(createdb, args, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (createCode != 0)
        {
            return (false, $"Could not create database '{targetDatabase}' (it must not exist yet): {LastLine(createErr)}");
        }

        var restoreArgs = ConnectionArgs(targetDatabase);
        restoreArgs.AddRange(["--no-password", "--no-owner", "--jobs=2", dumpFile]);
        var (code, _, stderr) = await RunCaptureAsync(tools.Tool("pg_restore"), restoreArgs, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        return code == 0
            ? (true, $"Database restored into new database '{targetDatabase}'.")
            : (false, $"pg_restore reported errors while loading '{targetDatabase}': {LastLine(stderr)}");
    }

    // ------------------------------------------------------------------ internals

    private async Task<DumpOutcome> StreamAsync(IBackupEngine engine, EngineContext ctx, DumpTarget target, string tool, List<string> args, Action<long> onBytes, CancellationToken ct)
    {
        long bytes = 0;
        int? dumpExit = null;
        var dumpErrors = new StringBuilder();

        async Task Producer(Stream engineInput, CancellationToken token)
        {
            var start = StartInfo(tool, args, redirectStdout: true);
            using var dump = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(tool)} could not be started.");
            var errTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await dump.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                {
                    if (!line.Contains("pg_dump: dumping contents", StringComparison.Ordinal) && dumpErrors.Length < 8192)
                    {
                        dumpErrors.AppendLine(SecretRedactor.Redact(line));
                    }
                }
            }, CancellationToken.None);

            var buffer = new byte[1 << 20];
            var source = dump.StandardOutput.BaseStream;
            int read;
            try
            {
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await engineInput.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    bytes += read;
                    onBytes(bytes);
                }
            }
            catch (OperationCanceledException)
            {
                try { dump.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            await dump.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            dumpExit = dump.ExitCode;
        }

        var prepared = await engine.PrepareStreamSourceAsync(ctx, target.VirtualPath, retention, ct).ConfigureAwait(false);
        if (!prepared.Ok)
        {
            return new DumpOutcome(target.Database, new SnapshotOutcome(false, prepared.ReasonCode, prepared.Message), 0);
        }

        var snapshot = await engine.BackupStreamAsync(ctx, target.VirtualPath, target.FileName, Producer, ct).ConfigureAwait(false);
        if (snapshot.Ok && dumpExit != 0)
        {
            // The engine stored what it received, but the dump itself did not complete: remove it.
            if (snapshot.SnapshotId is { } id)
            {
                await engine.DeleteSnapshotAsync(ctx, id, CancellationToken.None).ConfigureAwait(false);
            }

            var reason = LastLine(dumpErrors.ToString());
            log("warning", $"Dump of '{target.Database}' failed (exit {dumpExit}): {reason}");
            return new DumpOutcome(target.Database,
                new SnapshotOutcome(false, BackupReasonCodes.SourceReadErrors, $"pg_dump of '{target.Database}' failed: {reason}"), bytes);
        }

        return new DumpOutcome(target.Database, snapshot with { Bytes = snapshot.Bytes ?? bytes, Files = 1 }, bytes);
    }

    /// <summary>Connection flags. Peer authentication uses the local socket (no -h), as the postgres OS user.</summary>
    private List<string> ConnectionArgs(string? database)
    {
        var args = new List<string>();
        if (!spec.UsePeerAuth)
        {
            args.AddRange(["-h", spec.Host, "-p", spec.Port.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            args.AddRange(["-p", spec.Port.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-U", spec.Username]);
        if (database is not null)
        {
            args.AddRange(["-d", database]);
        }

        return args;
    }

    private ProcessStartInfo StartInfo(string tool, IReadOnlyList<string> args, bool redirectStdout)
    {
        var (fileName, prefix) = PeerPrefix(tool);
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            // The tools may run as the postgres OS user, who cannot enter the worker's private folder.
            WorkingDirectory = OperatingSystem.IsWindows() ? tools.BinDirectory : "/"
        };
        foreach (var p in prefix)
        {
            start.ArgumentList.Add(p);
        }

        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        // Secrets only through the environment - never on the command line (visible to every local user).
        if (!string.IsNullOrEmpty(password))
        {
            start.Environment["PGPASSWORD"] = password;
        }

        start.Environment["PGSSLMODE"] = spec.SslMode is "disable" or "prefer" or "require" or "verify-full" ? spec.SslMode : "prefer";
        start.Environment["PGCONNECT_TIMEOUT"] = "20";
        start.Environment["PGAPPNAME"] = "cloudorc-backup";
        return start;
    }

    private (string FileName, string[] Prefix) PeerPrefix(string tool)
    {
        if (!spec.UsePeerAuth || OperatingSystem.IsWindows())
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

    private async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(string tool, List<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var process = Process.Start(StartInfo(tool, args, redirectStdout: true)) ?? throw new InvalidOperationException($"{tool} could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return (-1, "", $"{Path.GetFileName(tool)} timed out after {timeout.TotalMinutes:F0} min");
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), SecretRedactor.Redact(await stderr.ConfigureAwait(false)) ?? "");
    }

    public static bool IsSafeDatabaseName(string? name) => name is not null && DatabaseNamePattern().IsMatch(name);

    private static string SafeSegment(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no details";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]{0,62}$")]
    private static partial Regex DatabaseNamePattern();
}
