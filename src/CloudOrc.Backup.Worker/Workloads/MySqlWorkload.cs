using System.Diagnostics;
using System.Globalization;
using System.Text;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Shared;
using CloudOrc.Backup.Worker.Engines;

namespace CloudOrc.Backup.Worker.Workloads;

/// <summary>
/// MySQL / MariaDB backup and restore with the server's own client tools.
/// <list type="bullet">
/// <item>Backup: every selected database is dumped with <c>mysqldump --single-transaction</c> (a consistent
/// InnoDB snapshot without locking writers) and streamed straight into the backup engine as
/// "&lt;db&gt;.sql" - no temporary file. No <c>--databases</c>, so the dump carries no CREATE DATABASE / USE
/// and can be loaded under any name. A dump that fails part-way is deleted from the repository.</item>
/// <item>Restore: "file" puts the .sql in a folder; "new-database" creates a NEW database (fails if the
/// name exists - nothing is ever overwritten) and loads the .sql with the mysql client.</item>
/// <item>Secrets: the password reaches the tools only as MYSQL_PWD in their environment, never on the
/// command line.</item>
/// </list>
/// </summary>
public sealed class MySqlWorkload(MySqlLocator.ClientTools tools, DatabaseSourceSpec spec, string? password, BackupRetention retention, Action<string, string> log)
    : IDatabaseWorkload
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

    public string DisplayName => "MySQL/MariaDB";

    public static string VirtualRoot(DatabaseSourceSpec spec) =>
        DatabaseVirtualPaths.Root(BackupSourceKinds.MySql, $"{DatabaseVirtualPaths.SafeSegment(spec.Host)}_{spec.Port.ToString(CultureInfo.InvariantCulture)}");

    public static string VirtualPathFor(DatabaseSourceSpec spec, string database) =>
        DatabaseVirtualPaths.Child(VirtualRoot(spec), DatabaseVirtualPaths.SafeSegment(database));

    string IDatabaseWorkload.VirtualPathFor(string database) => VirtualPathFor(spec, database);

    public string FileNameFor(string database) => DatabaseVirtualPaths.SafeSegment(database) + ".sql";

    // ------------------------------------------------------------------ argument building (pure, unit-tested)

    public static List<string> DumpArguments(DatabaseSourceSpec spec, string database)
    {
        var args = MySqlLocator.ConnectionArgs(spec.Host, spec.Port, spec.Username);
        args.AddRange([
            "--single-transaction", "--quick", "--routines", "--triggers", "--events", "--hex-blob",
            "--default-character-set=utf8mb4",
            // Tablespace DDL needs the PROCESS privilege on MySQL 8 and is never needed to restore a database.
            "--no-tablespaces",
            database
        ]);
        return args;
    }

    public static List<string> ListArguments(DatabaseSourceSpec spec)
    {
        var args = MySqlLocator.ConnectionArgs(spec.Host, spec.Port, spec.Username);
        args.AddRange(["--connect-timeout=20", "-N", "-B", "-e", "SHOW DATABASES"]);
        return args;
    }

    public static List<string> CreateDatabaseArguments(DatabaseSourceSpec spec, string database)
    {
        var args = MySqlLocator.ConnectionArgs(spec.Host, spec.Port, spec.Username);
        args.AddRange(["--connect-timeout=20", "-e", $"CREATE DATABASE {QuoteIdentifier(database)} CHARACTER SET utf8mb4"]);
        return args;
    }

    public static List<string> LoadArguments(DatabaseSourceSpec spec, string database)
    {
        var args = MySqlLocator.ConnectionArgs(spec.Host, spec.Port, spec.Username);
        args.AddRange(["--connect-timeout=20", "--default-character-set=utf8mb4", "--database=" + database]);
        return args;
    }

    /// <summary>`name` with every backtick doubled.</summary>
    public static string QuoteIdentifier(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>SHOW DATABASES output minus system schemas, exclusions and names this agent cannot pass safely.</summary>
    public static List<string> FilterDatabaseList(string showDatabasesOutput, IReadOnlyList<string> exclude) =>
        showDatabasesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => !MySqlLocator.SystemDatabases.Contains(d))
            .Where(d => DatabaseNameRules.IsValid(BackupSourceKinds.MySql, d))
            .Where(d => !exclude.Contains(d, StringComparer.Ordinal))
            .ToList();

    // ------------------------------------------------------------------ backup

    public async Task<(IReadOnlyList<string> Databases, string? Error)> ResolveDatabasesAsync(CancellationToken ct)
    {
        if (spec.Databases.Count > 0)
        {
            var invalid = spec.Databases.FirstOrDefault(d => !DatabaseNameRules.IsValid(BackupSourceKinds.MySql, d));
            return invalid is null ? (spec.Databases, null) : ([], $"Invalid database name in policy: {invalid}");
        }

        if (tools.ClientPath is null)
        {
            return ([], "The mysql client was not found next to mysqldump; list the databases to back up in the protection plan.");
        }

        var (code, stdout, stderr) = await RunCaptureAsync(tools.ClientPath, ListArguments(spec), null, ListTimeout, ct).ConfigureAwait(false);
        if (code != 0)
        {
            return ([], $"Could not list databases: {MySqlLocator.FriendlyError(stderr, spec.Host, spec.Port, spec.Username)}");
        }

        var list = FilterDatabaseList(stdout, spec.ExcludeDatabases);
        return list.Count == 0 ? ([], "No databases to back up were found on this MySQL/MariaDB server.") : (list, null);
    }

    public Task<DumpOutcome> DumpDatabaseAsync(IBackupEngine engine, EngineContext ctx, string database, Action<long> onBytes, CancellationToken ct) =>
        DatabaseStreaming.StreamAsync(engine, ctx, retention, database, VirtualPathFor(spec, database), FileNameFor(database),
            (input, counted, token) => DumpIntoAsync(database, input, counted, token), "mysqldump", onBytes, log, ct);

    public Task<DumpOutcome?> DumpGlobalsAsync(IBackupEngine engine, EngineContext ctx, CancellationToken ct) => Task.FromResult<DumpOutcome?>(null);

    private async Task<(bool Ok, string? Error)> DumpIntoAsync(string database, Stream engineInput, Action<int> counted, CancellationToken ct)
    {
        var errors = new StringBuilder();
        using var dump = Process.Start(StartInfo(tools.DumpPath, DumpArguments(spec, database), redirectStdout: true, redirectStdin: false))
                         ?? throw new InvalidOperationException("mysqldump could not be started.");
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await dump.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
            {
                if (errors.Length < 8192)
                {
                    errors.AppendLine(SecretRedactor.Redact(line));
                }
            }
        }, CancellationToken.None);

        try
        {
            await DatabaseStreaming.CopyAsync(dump.StandardOutput.BaseStream, engineInput, counted, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { dump.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        catch (IOException)
        {
            try { dump.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        await dump.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await errTask.ConfigureAwait(false);
        return dump.ExitCode == 0
            ? (true, null)
            : (false, $"mysqldump exit {dump.ExitCode}: {MySqlLocator.FriendlyError(errors.ToString(), spec.Host, spec.Port, spec.Username)}");
    }

    // ------------------------------------------------------------------ restore

    /// <summary>CREATE DATABASE (fails when it exists) and load the restored .sql with the mysql client.</summary>
    public async Task<(bool Ok, string Message)> RestoreIntoNewDatabaseAsync(string sqlFile, string targetDatabase, CancellationToken ct)
    {
        if (!DatabaseNameRules.IsValid(BackupSourceKinds.MySql, targetDatabase))
        {
            return (false, "The new database name may only contain letters, digits, '_', '$' and '-'.");
        }

        if (tools.ClientPath is null)
        {
            return (false, "the mysql client was not found on this machine.");
        }

        var (createCode, _, createErr) = await RunCaptureAsync(tools.ClientPath, CreateDatabaseArguments(spec, targetDatabase), null, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (createCode != 0)
        {
            return (false, $"could not create database '{targetDatabase}' (it must not exist yet): {MySqlLocator.FriendlyError(createErr, spec.Host, spec.Port, spec.Username)}");
        }

        var (code, _, stderr) = await RunCaptureAsync(tools.ClientPath, LoadArguments(spec, targetDatabase), sqlFile, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        return code == 0
            ? (true, $"Database restored into new database '{targetDatabase}'.")
            : (false, $"the mysql client reported errors while loading '{targetDatabase}': {MySqlLocator.FriendlyError(stderr, spec.Host, spec.Port, spec.Username)}");
    }

    // ------------------------------------------------------------------ process helpers

    private ProcessStartInfo StartInfo(string tool, IReadOnlyList<string> args, bool redirectStdout, bool redirectStdin)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            WorkingDirectory = OperatingSystem.IsWindows() ? tools.BinDirectory : "/"
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        // Secrets only through the environment - never on the command line (visible to every local user).
        MySqlLocator.ApplyPassword(start.Environment, password);
        return start;
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(string tool, List<string> args, string? stdinFile, TimeSpan timeout, CancellationToken ct)
    {
        using var process = Process.Start(StartInfo(tool, args, redirectStdout: true, redirectStdin: stdinFile is not null))
                            ?? throw new InvalidOperationException($"{tool} could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            if (stdinFile is not null)
            {
                try
                {
                    await using var file = new FileStream(stdinFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                    await file.CopyToAsync(process.StandardInput.BaseStream, 1 << 20, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // the client exited early; its exit code and stderr report why
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch (IOException) { }
                }
            }

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
}
