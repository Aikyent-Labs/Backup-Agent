using System.Diagnostics;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Worker.Consistency;
using CloudOrc.Backup.Worker.Engines;
using CloudOrc.Backup.Worker.Restore;
using CloudOrc.Backup.Worker.Scanning;
using CloudOrc.Backup.Worker.Storage;
using CloudOrc.Backup.Worker.Workloads;
using CloudOrc.Backup.Shared;

namespace CloudOrc.Backup.Worker.Orchestration;

/// <summary>
/// Runs one job inside the worker process and returns its terminal result. Stages follow the
/// guide's lifecycle: SNAPSHOTTING (consistency plan) → SCANNING (size + manifest) →
/// TRANSFERRING (engine snapshot, VSS held by the engine) → VERIFYING (engine verify).
/// It never claims more than it proved: read errors, degraded consistency or a failed
/// verification produce PARTIAL with a reason code, never a silent SUCCEEDED (§6, §15).
/// </summary>
public static class JobExecutor
{
    private static readonly TimeSpan GrantRenewLead = TimeSpan.FromMinutes(10);

    public static async Task<WorkerResult> RunAsync(WorkerJobSpec spec, IWorkerReporter reporter, CancellationToken ct)
    {
        Directory.CreateDirectory(spec.Paths.WorkDirectory);
        Directory.CreateDirectory(spec.Paths.CacheDirectory);

        EngineBinary engineBinary;
        try
        {
            engineBinary = EngineBinaryVerifier.Resolve(spec.Paths.EngineDirectory, spec.Engine);
        }
        catch (EngineBinaryException ex)
        {
            return Failed(ex.ReasonCode, ex.Message);
        }

        IBackupEngine engine = spec.Engine switch
        {
            BackupEngineNames.Kopia => new KopiaEngine(),
            _ => throw new InvalidOperationException($"Unknown engine '{spec.Engine}'. The Backup Agent ships Kopia only.")
        };

        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewLoop = KeepGrantFreshAsync(reporter, renewCts.Token);

        var grant = reporter.CurrentGrant;
        var context = new EngineContext
        {
            Engine = engineBinary,
            Storage = () => StorageTarget.Build(reporter.CurrentGrant),
            CurrentGrantId = () => reporter.CurrentGrant.GrantId,
            WorkDirectory = spec.Paths.WorkDirectory,
            CacheDirectory = spec.Paths.CacheDirectory,
            Budget = spec.Budget,
            SnapshotHost = grant.Repository.SnapshotHost ?? spec.EndpointId,
            SnapshotUser = grant.Repository.SnapshotUser ?? "cloudorc",
            JobId = spec.JobId,
            Password = grant.Repository.Password,
            CreateIfMissing = grant.Repository.CreateIfMissing && spec.Kind == BackupJobKinds.Backup,
            Log = reporter.Log
        };

        try
        {
            return spec.Kind switch
            {
                BackupJobKinds.Backup => await BackupAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                BackupJobKinds.Verify => await VerifyAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                BackupJobKinds.Restore => await RestoreAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                BackupJobKinds.Maintenance => await MaintenanceAsync(spec, engine, context, reporter, ct).ConfigureAwait(false),
                _ => Failed(BackupReasonCodes.PolicyInvalid, $"Unknown job kind '{spec.Kind}'.")
            };
        }
        finally
        {
            await engine.DisconnectAsync(context).ConfigureAwait(false);
            await renewCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // ==================================================================== backup

    private static async Task<WorkerResult> BackupAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var consistencyPolicy = spec.Consistency ?? new BackupConsistencyPolicy();
        var retention = spec.Retention ?? new BackupRetention();

        // Files/folders/Tally are read from disk; databases are dumped with their own tools.
        var fileSources = spec.Sources.Where(s => !BackupSourceKinds.IsDatabase(s.Kind)).ToList();
        var databaseSources = spec.Sources.Where(s => BackupSourceKinds.IsDatabase(s.Kind)).ToList();

        // ---- SNAPSHOTTING: decide what consistency we can honestly deliver.
        reporter.Stage(BackupJobStates.Snapshotting);
        await ConsistencyPlanner.WaitForApplicationCloseAsync(fileSources, consistencyPolicy, ConsistencyPlanner.AnyProcessRunning,
            TimeSpan.FromSeconds(30), message => reporter.Log("warning", message), ct).ConfigureAwait(false);
        var plan = ConsistencyPlanner.Plan(fileSources, consistencyPolicy, ConsistencyPlanner.ReadVolume, ConsistencyPlanner.AnyProcessRunning);
        foreach (var note in plan.Notes)
        {
            reporter.Log("warning", note);
        }

        if (plan.FailReasonCode is not null)
        {
            return Failed(plan.FailReasonCode, plan.FailMessage ?? "Consistency requirements not met.");
        }

        // ---- SCANNING: size the work, find the databases, write the browse manifest.
        reporter.Stage(BackupJobStates.Scanning);
        var databasePlans = new List<DatabasePlan>();
        foreach (var source in databaseSources)
        {
            var dbPlan = await PlanDatabaseSourceAsync(source, retention, spec.JobId, reporter, ct).ConfigureAwait(false);
            if (dbPlan.Error is not null)
            {
                reporter.Log("warning", dbPlan.Error);
            }

            databasePlans.Add(dbPlan);
        }

        // Browse manifest: every database file under the source's logical path ("mssql://host/SQLEXPRESS" → "sales.bak"),
        // which is how the backend lists and restores database recovery points.
        var virtualFiles = databasePlans.Where(p => p.Workload is not null)
            .SelectMany(p => p.Databases.Select(db => (p.Source.Path, p.Workload!.FileNameFor(db))))
            .ToList();
        var scan = SourceScanner.Scan(fileSources, spec.Paths.ManifestPath, spec.Budget.MaxManifestEntries,
            (files, bytes) => reporter.Progress(new JobProgress { FilesTotal = files, BytesTotal = bytes }), ct, virtualFiles);
        reporter.Progress(new JobProgress { FilesTotal = scan.Files, BytesTotal = scan.Bytes, Errors = scan.Errors }, scannerDurationMs: scan.DurationMs);

        var present = fileSources.Where(s => !scan.MissingSources.Contains(s.Path)).ToList();
        var databaseCount = databasePlans.Sum(p => p.Databases.Count);
        if (present.Count == 0 && databaseCount == 0)
        {
            var dbError = databasePlans.Select(p => p.Error).FirstOrDefault(e => e is not null);
            return Failed(BackupReasonCodes.SourceNotFound, dbError ?? "None of the policy's source paths exist on this device.");
        }

        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        // ---- TRANSFERRING: one engine snapshot per source.
        reporter.Stage(BackupJobStates.Transferring);
        var snapshots = new List<SnapshotRef>();

        // Sources finished by an earlier attempt of this job keep their snapshot (§8 checkpoints).
        foreach (var done in spec.CompletedSnapshots.Where(c => present.Any(p => string.Equals(p.Path, c.SourcePath, PlatformPaths.Comparison))))
        {
            snapshots.Add(done);
            reporter.Log("info", $"Source {done.SourcePath} was already backed up by an earlier attempt; skipping it.");
        }
        long filesNew = 0, filesChanged = 0, filesUnchanged = 0, uploaded = 0, errors = scan.Errors;
        EngineOutcome? firstFailure = null;
        var options = new BackupRunOptions(plan.UseVss, consistencyPolicy.VssMode, plan.UseVss ? plan.SnapshotLifetime : null, [], spec.FullScan);

        foreach (var source in present)
        {
            ct.ThrowIfCancellationRequested();
            if (snapshots.Any(s => string.Equals(s.SourcePath, source.Path, PlatformPaths.Comparison)))
            {
                continue;
            }

            var perSource = options with { Excludes = Engines.DefaultExcludes.With(source.Excludes) };

            var prepared = await engine.PrepareSourceAsync(ctx, source, retention, perSource, ct).ConfigureAwait(false);
            if (!prepared.Ok)
            {
                firstFailure ??= prepared;
                break;
            }

            var outcome = await engine.BackupAsync(ctx, source, perSource,
                (progress, rate) => reporter.Progress(new JobProgress
                {
                    FilesTotal = progress.FilesTotal ?? scan.Files,
                    FilesDone = progress.FilesDone,
                    BytesTotal = progress.BytesTotal ?? scan.Bytes,
                    BytesRead = progress.BytesRead,
                    BytesUploaded = progress.BytesUploaded,
                    Percent = progress.Percent,
                    EtaSeconds = progress.EtaSeconds,
                    Errors = progress.Errors
                }, rate), ct).ConfigureAwait(false);

            if (!outcome.Ok)
            {
                firstFailure ??= outcome;
                if (outcome.ReasonCode == BackupReasonCodes.Cancelled)
                {
                    return new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled, Message = "Cancelled." };
                }

                break;
            }

            var snapshotRef = new SnapshotRef
            {
                SourcePath = source.Path,
                SnapshotId = outcome.SnapshotId!,
                RootObjectId = outcome.RootObjectId,
                Files = outcome.Files,
                Bytes = outcome.Bytes,
                Errors = outcome.Errors
            };
            snapshots.Add(snapshotRef);
            reporter.SnapshotCompleted(snapshotRef);
            filesNew += outcome.FilesNew ?? 0;
            filesChanged += outcome.FilesChanged ?? 0;
            filesUnchanged += outcome.FilesUnchanged ?? 0;
            uploaded += outcome.BytesUploaded ?? 0;
            errors += outcome.Errors ?? 0;

            var retained = await engine.ApplyRetentionAsync(ctx, source, retention, ct).ConfigureAwait(false);
            if (!retained.Ok)
            {
                reporter.Log("warning", $"Retention for {source.Path} could not be applied now ({retained.ReasonCode}); maintenance will retry.");
            }
        }

        // Databases: one engine snapshot per database (pg_dump / mysqldump streamed, SQL Server .bak).
        // Each is reported under the source's logical path (the backend's identity of the database server);
        // a retried job dumps its databases again rather than trusting a half-finished earlier attempt.
        long databaseBytes = 0;
        if (firstFailure?.ReasonCode != BackupReasonCodes.Cancelled)
        {
            foreach (var dbPlan in databasePlans)
            {
                if (dbPlan.Workload is null)
                {
                    firstFailure ??= EngineOutcome.Fail(BackupReasonCodes.SourceNotFound, dbPlan.Error ?? "Database source unavailable.");
                    continue;
                }

                foreach (var database in dbPlan.Databases)
                {
                    ct.ThrowIfCancellationRequested();
                    var baseBytes = databaseBytes;
                    var dumped = await dbPlan.Workload.DumpDatabaseAsync(engine, ctx, database,
                        b => reporter.Progress(new JobProgress { FilesTotal = scan.Files + databaseCount, BytesRead = scan.Bytes + baseBytes + b, BytesUploaded = null }), ct).ConfigureAwait(false);
                    databaseBytes += dumped.DumpBytes;
                    if (!dumped.Snapshot.Ok)
                    {
                        if (dumped.Snapshot.ReasonCode == BackupReasonCodes.Cancelled)
                        {
                            return new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled, Message = "Cancelled." };
                        }

                        firstFailure ??= dumped.Snapshot;
                        continue;
                    }

                    var dbRef = new SnapshotRef
                    {
                        SourcePath = dbPlan.Source.Path,
                        SnapshotId = dumped.Snapshot.SnapshotId!,
                        RootObjectId = dumped.Snapshot.RootObjectId,
                        Files = 1,
                        Bytes = dumped.DumpBytes,
                        Errors = 0
                    };
                    snapshots.Add(dbRef);
                    reporter.SnapshotCompleted(dbRef);
                    filesChanged += 1;
                    uploaded += dumped.Snapshot.BytesUploaded ?? 0;
                    reporter.Log("info", $"{dbPlan.Workload.DisplayName} database '{database}' backed up ({dumped.DumpBytes / 1048576.0:F1} MB).");
                }

                var globals = await dbPlan.Workload.DumpGlobalsAsync(engine, ctx, ct).ConfigureAwait(false);
                if (globals is { Snapshot.Ok: true })
                {
                    var globalsRef = new SnapshotRef
                    {
                        SourcePath = dbPlan.Source.Path,
                        SnapshotId = globals.Snapshot.SnapshotId!,
                        RootObjectId = globals.Snapshot.RootObjectId,
                        Files = 1,
                        Bytes = globals.DumpBytes,
                        Errors = 0
                    };
                    snapshots.Add(globalsRef);
                    reporter.SnapshotCompleted(globalsRef);
                }
                else if (globals is not null)
                {
                    reporter.Log("warning", "Roles/tablespaces (globals) were not backed up - the database account is not a superuser. Database contents are unaffected.");
                }
            }
        }

        if (snapshots.Count == 0)
        {
            return Failed(firstFailure?.ReasonCode ?? BackupReasonCodes.EngineFailed, firstFailure?.Message ?? "No snapshot was created.");
        }

        // ---- VERIFYING: engine-native verification of what was just written.
        reporter.Stage(BackupJobStates.Verifying);
        var percent = Math.Clamp(spec.Verification?.VerifyFilesPercent ?? 0, 0, 100);
        var verify = await engine.VerifyAsync(ctx, snapshots.Select(s => s.SnapshotId).ToList(), percent, ct).ConfigureAwait(false);
        var verification = verify.Ok ? VerificationStates.Verified : VerificationStates.VerificationFailed;

        // Tally may have been started during the run: re-check before claiming consistency.
        var consistency = plan.ExpectedLevel;
        if (consistency == ConsistencyLevels.ApplicationConsistent && spec.Sources.Any(s => s.Kind == BackupSourceKinds.Tally)
            && ConsistencyPlanner.AnyProcessRunning(consistencyPolicy.Tally.ProcessNames))
        {
            consistency = ConsistencyLevels.CrashConsistent;
        }

        var summary = new JobResultSummary
        {
            Engine = engine.Name,
            EngineVersion = ctx.Engine.Version,
            EngineSnapshots = snapshots,
            Consistency = consistency,
            VerificationState = verification,
            VerifyFilesPercent = percent,
            SourceTime = started,
            FilesNew = filesNew,
            FilesChanged = filesChanged,
            FilesUnchanged = filesUnchanged,
            SourceBytes = scan.Bytes + databaseBytes,
            BackupMode = spec.FullScan ? BackupModes.Full : BackupModes.Incremental,
            BytesRead = scan.Bytes + databaseBytes,
            BytesUploaded = uploaded,
            Errors = errors,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ManifestEntries = scan.ManifestEntries
        };

        // ---- Outcome: never claim more than was proven.
        var (state, reason, message) = (BackupJobStates.Succeeded, (string?)null, "Backup completed and verified.");
        if (firstFailure is not null)
        {
            (state, reason, message) = (BackupJobStates.Partial, firstFailure.ReasonCode, $"Only {snapshots.Count} of {present.Count + databaseCount} items were backed up: {firstFailure.Message}");
        }
        else if (!verify.Ok)
        {
            (state, reason, message) = (BackupJobStates.Partial, BackupReasonCodes.VerificationFailed, $"Backup written but verification failed: {verify.Message}");
        }
        else if (scan.MissingSources.Count > 0)
        {
            (state, reason, message) = (BackupJobStates.Partial, BackupReasonCodes.SourceNotFound, $"{scan.MissingSources.Count} source path(s) no longer exist.");
        }
        else if (errors > 0)
        {
            (state, reason, message) = (BackupJobStates.Partial, BackupReasonCodes.SourceReadErrors, $"{errors} file(s) could not be read.");
        }
        else if (ConsistencyPlanner.IsDegraded(consistency, consistencyPolicy))
        {
            (state, reason, message) = (BackupJobStates.Partial,
                consistency == ConsistencyLevels.CrashConsistent ? BackupReasonCodes.ApplicationNotQuiesced : BackupReasonCodes.SnapshotFailed,
                consistency == ConsistencyLevels.CrashConsistent ? "Backup completed while Tally was running (crash-consistent)." : "Backup completed without a snapshot (files read live).");
        }

        return new WorkerResult { State = state, ReasonCode = reason, Message = message, Summary = summary };
    }

    // ==================================================================== verify

    private static async Task<WorkerResult> VerifyAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.EngineSnapshotId))
        {
            return Failed(BackupReasonCodes.PolicyInvalid, "No recovery point to verify.");
        }

        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        reporter.Stage(BackupJobStates.Verifying);
        var percent = Math.Clamp(spec.VerifyFilesPercent ?? 5, 0, 100);
        var verify = await engine.VerifyAsync(ctx, [spec.EngineSnapshotId], percent, ct).ConfigureAwait(false);
        var summary = new JobResultSummary
        {
            Engine = engine.Name,
            EngineVersion = ctx.Engine.Version,
            EngineSnapshots = [new SnapshotRef { SourcePath = "", SnapshotId = spec.EngineSnapshotId }],
            VerificationState = verify.Ok ? VerificationStates.Verified : VerificationStates.VerificationFailed,
            VerifyFilesPercent = percent
        };

        return verify.Ok
            ? new WorkerResult { State = BackupJobStates.Succeeded, Message = "Recovery point verified.", Summary = summary }
            : new WorkerResult { State = BackupJobStates.Failed, ReasonCode = verify.ReasonCode ?? BackupReasonCodes.VerificationFailed, Message = verify.Message, Summary = summary };
    }

    // ==================================================================== restore

    private static async Task<WorkerResult> RestoreAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        var restore = spec.Restore;
        if (restore?.EngineSnapshotId is null || restore.SourceRoot is null)
        {
            return Failed(BackupReasonCodes.RestoreTargetInvalid, "Restore parameters are incomplete.");
        }

        var dbRestore = restore.Database;
        if (DatabaseRestoreModes.Validate(dbRestore) is { } dbError)
        {
            return Failed(BackupReasonCodes.RestoreTargetInvalid, dbError);
        }

        var isLogicalDatabasePath = DatabaseVirtualPaths.IsLogicalDatabasePath(restore.SourceRoot);
        var isDatabasePoint = dbRestore is not null || PostgresWorkload.IsVirtualDatabasePath(restore.SourceRoot) || isLogicalDatabasePath;
        var loadsDatabase = isDatabasePoint && dbRestore is { Mode: DatabaseRestoreModes.NewDatabase or DatabaseRestoreModes.Replace };
        if (isDatabasePoint && !loadsDatabase && restore.TargetKind == RestoreTargetKinds.Original)
        {
            return Failed(BackupReasonCodes.RestoreTargetInvalid,
                "A database backup file is restored into a folder; choose a target folder (or restore into a database).");
        }

        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        // A database recovery point lists its files under the server's logical path ("mssql://host/SQLEXPRESS" →
        // "sales.bak"); each database is its own engine snapshot, written by the same job. Find that snapshot.
        var snapshotId = restore.EngineSnapshotId;
        var sourceRoot = restore.SourceRoot;
        if (isLogicalDatabasePath)
        {
            var resolved = await ResolveDatabaseSnapshotAsync(engine, ctx, restore, ct).ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                return Failed(BackupReasonCodes.RestoreTargetInvalid, resolved.Error);
            }

            (snapshotId, sourceRoot) = (resolved.SnapshotId!, resolved.VirtualPath!);
        }

        // Loading into a database: the dump only needs a scratch folder (this job's own work folder) unless the
        // request names a usable folder to keep a copy in.
        string? scratchDirectory = null;
        RestorePlan plan;
        try
        {
            plan = RestorePathGuard.Plan(CopyWith(restore, sourceRoot), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (loadsDatabase && ex is RestoreRejectedException or ArgumentException or NotSupportedException or IOException)
        {
            scratchDirectory = Path.Combine(spec.Paths.WorkDirectory, "db-restore");
            plan = new RestorePlan(scratchDirectory, restore.Selection.Count == 0 ? [null] : restore.Selection.Select(s => (string?)RestorePathGuard.NormalizeRelative(s)).ToList());
        }
        catch (RestoreRejectedException ex)
        {
            return Failed(BackupReasonCodes.RestorePathUnsafe, ex.Message);
        }

        try
        {
            return await RestoreIntoPlanAsync(spec, engine, ctx, reporter, restore, dbRestore, loadsDatabase, snapshotId, sourceRoot, plan, scratchDirectory, ct).ConfigureAwait(false);
        }
        finally
        {
            if (scratchDirectory is not null)
            {
                try { Directory.Delete(scratchDirectory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<WorkerResult> RestoreIntoPlanAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, RestoreExecuteParameters restore,
        DatabaseRestoreSpec? dbRestore, bool loadsDatabase, string snapshotId, string sourceRoot, RestorePlan plan, string? scratchDirectory, CancellationToken ct)
    {
        reporter.Stage(BackupJobStates.Restoring);
        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(plan.TargetDirectory);
        var done = 0;
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await engine.RestoreAsync(ctx, snapshotId, sourceRoot, item, plan.TargetDirectory, restore.Mode, ct).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                return outcome.ReasonCode == BackupReasonCodes.Cancelled
                    ? new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled, Message = "Cancelled." }
                    : Failed(outcome.ReasonCode ?? BackupReasonCodes.EngineFailed, outcome.Message ?? "Restore failed.");
            }

            done++;
            reporter.Progress(new JobProgress { FilesDone = done, FilesTotal = plan.Items.Count, Percent = Math.Round(done * 100.0 / plan.Items.Count, 1) });
        }

        // ---- VERIFYING: every selected item exists, nothing links outside the target.
        reporter.Stage(BackupJobStates.Verifying);
        long files = 0, bytes = 0;
        var unsafeLinks = 0;
        foreach (var item in plan.Items)
        {
            var destination = item is null ? plan.TargetDirectory : Path.Combine(plan.TargetDirectory, item);
            if (!File.Exists(destination) && !Directory.Exists(destination))
            {
                return Failed(BackupReasonCodes.RestoreVerificationFailed, "A selected item is missing after the restore.");
            }

            if (File.Exists(destination))
            {
                files++;
                bytes += new FileInfo(destination).Length;
                continue;
            }

            var audit = RestorePathGuard.Audit(destination);
            files += audit.Files;
            bytes += audit.Bytes;
            unsafeLinks += audit.UnsafeLinks;
        }

        var summary = new JobResultSummary
        {
            Engine = engine.Name,
            EngineVersion = ctx.Engine.Version,
            EngineSnapshots = [new SnapshotRef { SourcePath = restore.SourceRoot!, SnapshotId = restore.EngineSnapshotId! }],
            VerificationState = unsafeLinks == 0 ? VerificationStates.Verified : VerificationStates.VerificationFailed,
            RestoredFiles = files,
            RestoredBytes = bytes,
            RestoreTarget = scratchDirectory is null ? plan.TargetDirectory : null,
            DurationMs = stopwatch.ElapsedMilliseconds
        };

        if (unsafeLinks == 0 && loadsDatabase && dbRestore is not null)
        {
            var loaded = await LoadDatabaseAsync(dbRestore, plan.TargetDirectory, spec.JobId, reporter, ct).ConfigureAwait(false);
            if (loaded.Ok)
            {
                return new WorkerResult { State = BackupJobStates.Succeeded, Message = loaded.Message, Summary = summary };
            }

            return scratchDirectory is null
                ? new WorkerResult { State = BackupJobStates.Partial, ReasonCode = BackupReasonCodes.RestoreVerificationFailed, Message = $"The backup file was restored to {plan.TargetDirectory}, but {loaded.Message}", Summary = summary }
                : new WorkerResult { State = BackupJobStates.Failed, ReasonCode = BackupReasonCodes.RestoreVerificationFailed, Message = $"The database could not be restored: {loaded.Message}", Summary = summary };
        }

        return unsafeLinks == 0
            ? new WorkerResult { State = BackupJobStates.Succeeded, Message = $"Restored {files} file(s).", Summary = summary }
            : new WorkerResult
            {
                State = BackupJobStates.Partial,
                ReasonCode = BackupReasonCodes.RestorePathUnsafe,
                Message = $"Restore finished but {unsafeLinks} link(s) point outside the restore folder - review before use.",
                Summary = summary
            };
    }

    // ==================================================================== maintenance

    private static async Task<WorkerResult> MaintenanceAsync(WorkerJobSpec spec, IBackupEngine engine, EngineContext ctx, IWorkerReporter reporter, CancellationToken ct)
    {
        var connected = await engine.ConnectAsync(ctx, ct).ConfigureAwait(false);
        if (!connected.Ok)
        {
            return Failed(connected.ReasonCode!, connected.Message!);
        }

        reporter.Stage(BackupJobStates.Maintaining);
        var stopwatch = Stopwatch.StartNew();
        var outcome = await engine.MaintenanceAsync(ctx, spec.MaintenanceFull, spec.Retention, ct).ConfigureAwait(false);
        var summary = new JobResultSummary { Engine = engine.Name, EngineVersion = ctx.Engine.Version, DurationMs = stopwatch.ElapsedMilliseconds };
        return outcome.Ok
            ? new WorkerResult { State = BackupJobStates.Succeeded, Message = "Maintenance completed.", Summary = summary }
            : Failed(outcome.ReasonCode ?? BackupReasonCodes.EngineFailed, outcome.Message ?? "Maintenance failed.");
    }

    // ==================================================================== grants

    /// <summary>Asks for a new grant ahead of expiry so a long job never runs on a dead credential.</summary>
    private static async Task KeepGrantFreshAsync(IWorkerReporter reporter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            var grant = reporter.CurrentGrant;
            if (grant.ExpiresAt - DateTimeOffset.UtcNow < GrantRenewLead)
            {
                if (await reporter.RenewGrantAsync(ct).ConfigureAwait(false) is null)
                {
                    reporter.Log("warning", "Storage grant renewal failed; the job will stop if the current grant expires.");
                }
            }
        }
    }

    private sealed record DatabasePlan(BackupSource Source, DatabaseSourceSpec Spec, IDatabaseWorkload? Workload, IReadOnlyList<string> Databases, string? Error);

    /// <summary>Routes a database source to its workload by kind (postgresql / mssql / mysql) and resolves the databases to back up.</summary>
    private static async Task<DatabasePlan> PlanDatabaseSourceAsync(BackupSource source, BackupRetention retention, string jobId, IWorkerReporter reporter, CancellationToken ct)
    {
        var dbSpec = source.Database ?? new DatabaseSourceSpec();
        var secret = SecretFor(reporter.CurrentGrant, dbSpec.CredentialRef);
        IDatabaseWorkload workload;
        string describe;
        switch (source.Kind)
        {
            case BackupSourceKinds.PostgreSql:
            {
                var tools = PostgresLocator.FindNewest(dbSpec.BinDirectory);
                if (tools is null)
                {
                    return new DatabasePlan(source, dbSpec, null, [], "PostgreSQL client tools (pg_dump) were not found on this machine. Install the PostgreSQL client matching your server version.");
                }

                workload = new PostgresWorkload(tools, dbSpec, secret, retention, reporter.Log);
                describe = $"PostgreSQL {dbSpec.Host}:{dbSpec.Port} with pg_dump {tools.Version}";
                break;
            }

            case BackupSourceKinds.MySql:
            {
                var tools = MySqlLocator.Find(dbSpec.BinDirectory);
                if (tools is null)
                {
                    return new DatabasePlan(source, dbSpec, null, [], "MySQL/MariaDB client tools (mysqldump) were not found on this machine. Install the MySQL or MariaDB client tools.");
                }

                workload = new MySqlWorkload(tools, dbSpec, secret, retention, reporter.Log);
                describe = $"MySQL/MariaDB {dbSpec.Host}:{dbSpec.Port} with {Path.GetFileName(tools.DumpPath)} {tools.Version}";
                break;
            }

            case BackupSourceKinds.MsSql:
                workload = new MsSqlWorkload(dbSpec, secret, jobId, retention, reporter.Log);
                describe = $"SQL Server {MsSqlSupport.DataSource(dbSpec)} ({(dbSpec.UseWindowsAuth ? "Windows authentication" : "SQL login " + dbSpec.Username)})";
                break;

            default:
                return new DatabasePlan(source, dbSpec, null, [], $"Database type '{source.Kind}' is not supported by this agent version.");
        }

        var (databases, error) = await workload.ResolveDatabasesAsync(ct).ConfigureAwait(false);
        reporter.Log("info", $"{describe}: {databases.Count} database(s) to back up.");
        return new DatabasePlan(source, dbSpec, error is null ? workload : null, databases, error);
    }

    /// <summary>
    /// Maps a backend database restore (sourceRoot = logical path, selection = ["sales.bak"]) to the engine snapshot
    /// holding that one file: the snapshot of the database's virtual path written by the same backup job.
    /// </summary>
    private static async Task<(string? SnapshotId, string? VirtualPath, string? Error)> ResolveDatabaseSnapshotAsync(IBackupEngine engine, EngineContext ctx,
        RestoreExecuteParameters restore, CancellationToken ct)
    {
        var connection = restore.Database?.Connection;
        if (connection is null)
        {
            return (null, null, "The database connection details for this restore are missing.");
        }

        if (restore.Selection.Count != 1)
        {
            return (null, null, "Choose exactly one database to restore.");
        }

        string file;
        try
        {
            file = RestorePathGuard.NormalizeRelative(restore.Selection[0]);
        }
        catch (RestoreRejectedException ex)
        {
            return (null, null, ex.Message);
        }

        // The logical path names the engine ("mssql://..."); it is only an identifier - the connection details come from the spec.
        var engineName = EngineOfLogicalPath(restore.SourceRoot!);
        var virtualPath = VirtualPathForFile(engineName, connection, file);
        if (virtualPath is null)
        {
            return (null, null, $"'{file}' is not a database backup file of this recovery point.");
        }

        var found = await engine.FindSnapshotOfSameJobAsync(ctx, restore.EngineSnapshotId!, virtualPath, ct).ConfigureAwait(false);
        return found is null
            ? (null, null, $"The backup of '{Path.GetFileNameWithoutExtension(file)}' was not found in this recovery point.")
            : (found, virtualPath, null);
    }

    /// <summary>"mssql://host/SQLEXPRESS" → "mssql".</summary>
    internal static string EngineOfLogicalPath(string logicalPath)
    {
        var i = logicalPath.IndexOf("://", StringComparison.Ordinal);
        return i > 0 ? logicalPath[..i].ToLowerInvariant() : BackupSourceKinds.PostgreSql;
    }

    /// <summary>Virtual (repository) path of the snapshot holding "&lt;db&gt;.dump|.sql|.bak" for this connection.</summary>
    internal static string? VirtualPathForFile(string engineName, DatabaseSourceSpec connection, string fileName)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0)
        {
            return null;
        }

        return engineName switch
        {
            BackupSourceKinds.PostgreSql when name == "globals.sql" => PostgresWorkload.VirtualPathFor(connection, "_globals"),
            BackupSourceKinds.PostgreSql when extension.Equals(".dump", StringComparison.OrdinalIgnoreCase) => PostgresWorkload.VirtualPathFor(connection, stem),
            BackupSourceKinds.MySql when extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) => MySqlWorkload.VirtualPathFor(connection, stem),
            BackupSourceKinds.MsSql when extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) =>
                DatabaseVirtualPaths.Child(MsSqlWorkload.VirtualRoot(connection), stem),
            _ => null
        };
    }

    private static RestoreExecuteParameters CopyWith(RestoreExecuteParameters r, string sourceRoot) => new()
    {
        RestoreJobId = r.RestoreJobId,
        RecoveryPointId = r.RecoveryPointId,
        RepositoryId = r.RepositoryId,
        Engine = r.Engine,
        EngineSnapshotId = r.EngineSnapshotId,
        SourceRoot = sourceRoot,
        Selection = r.Selection,
        TargetKind = r.TargetKind,
        TargetPath = r.TargetPath,
        Mode = r.Mode,
        IdempotencyKey = r.IdempotencyKey,
        Database = r.Database,
        ApprovedBy = r.ApprovedBy
    };

    /// <summary>Loads a restored dump / .bak into a database: routed by <see cref="DatabaseRestoreSpec.Engine"/>.</summary>
    private static async Task<(bool Ok, string Message)> LoadDatabaseAsync(DatabaseRestoreSpec dbRestore, string targetDirectory, string jobId, IWorkerReporter reporter, CancellationToken ct)
    {
        var extension = DatabaseRestoreModes.FileExtensionFor(dbRestore.Engine);
        var file = Directory.EnumerateFiles(targetDirectory, "*" + extension, SearchOption.AllDirectories)
            .FirstOrDefault(f => !Path.GetFileName(f).Equals("globals.sql", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return (false, $"no {extension} file was found in the restored data.");
        }

        var connection = dbRestore.Connection ?? new DatabaseSourceSpec();
        var secret = SecretFor(reporter.CurrentGrant, connection.CredentialRef);
        reporter.Stage(BackupJobStates.Restoring);
        switch (dbRestore.Engine)
        {
            case BackupSourceKinds.MsSql:
            {
                var workload = new MsSqlWorkload(connection, secret, jobId, new BackupRetention(), reporter.Log);
                return dbRestore.Mode == DatabaseRestoreModes.Replace
                    ? await workload.ReplaceDatabaseAsync(file, dbRestore.TargetDatabase, ct).ConfigureAwait(false)
                    : await workload.RestoreAsNewDatabaseAsync(file, dbRestore.TargetDatabase!, ct).ConfigureAwait(false);
            }

            case BackupSourceKinds.MySql:
            {
                var tools = MySqlLocator.Find(connection.BinDirectory);
                if (tools is null)
                {
                    return (false, "the mysql client was not found on this machine.");
                }

                return await new MySqlWorkload(tools, connection, secret, new BackupRetention(), reporter.Log)
                    .RestoreIntoNewDatabaseAsync(file, dbRestore.TargetDatabase!, ct).ConfigureAwait(false);
            }

            default:
            {
                var tools = PostgresLocator.FindNewest(connection.BinDirectory);
                if (tools is null)
                {
                    return (false, "pg_restore was not found on this machine.");
                }

                return await new PostgresWorkload(tools, connection, secret, new BackupRetention(), reporter.Log)
                    .RestoreIntoNewDatabaseAsync(file, dbRestore.TargetDatabase!, ct).ConfigureAwait(false);
            }
        }
    }

    private static string? SecretFor(StorageGrant grant, string? credentialRef) =>
        credentialRef is not null && grant.SourceSecrets.TryGetValue(credentialRef, out var secret) ? secret : null;

    private static WorkerResult Failed(string reasonCode, string message) =>
        new() { State = BackupJobStates.Failed, ReasonCode = reasonCode, Message = message };
}
