using CloudOrc.Backup.Updater;

// CloudOrc signed self-updater (CO-DEV-BKP-001 §13). Launched detached by Agent Core after it
// has downloaded and pre-verified the installer; re-verifies it independently, installs,
// health-checks, and rolls back on failure. Writes a JSON result file Core reports on restart.

const int ExitInvalidArguments = 2;
const int ExitAlreadyRunning = 3;
const int ExitUnexpected = 1;

var parsed = UpdaterArguments.TryParse(args, out var argError, out var resultFileHint);
if (parsed is null)
{
    Console.Error.WriteLine($"Invalid arguments: {argError}");
    Console.Error.WriteLine("Usage: cloudorc-backup-updater --installer <path> --sha256 <hex> --expected-version <X.Y.Z> " +
                            "--install-dir <dir> --work-dir <dir> --result-file <path> --update-id <id> " +
                            "[--health-timeout-seconds 180] [--allow-unsigned]");
    TryWriteEarlyFailure(resultFileHint, $"Invalid arguments: {argError}");
    return ExitInvalidArguments;
}

using var mutex = new Mutex(initiallyOwned: false, @"Global\CloudOrc.Backup.Updater");
bool owned;
try
{
    owned = mutex.WaitOne(TimeSpan.Zero);
}
catch (AbandonedMutexException)
{
    owned = true; // previous updater died; we now own it.
}

if (!owned)
{
    Console.Error.WriteLine("Another CloudOrc updater is already running.");
    return ExitAlreadyRunning;
}

var log = new FileUpdateLog(parsed.LogFile);
try
{
    Directory.CreateDirectory(parsed.WorkDirectory);

    var serviceControl = UpdatePlatform.CreateServiceControl(log);
    var orchestrator = new UpdateOrchestrator(
        new FileHashVerifier(),
        UpdatePlatform.CreateSignatureVerifier(),
        new ControlAgentVersionProbe(parsed.ControlAgentExe),
        new PipeHealthProbe(),
        serviceControl,
        UpdatePlatform.CreateInstallerRunner(parsed.InstallDirectory, serviceControl, log),
        new DirectoryBackup(),
        log,
        TimeProvider.System);

    var result = await orchestrator.RunAsync(parsed, CancellationToken.None).ConfigureAwait(false);
    ResultWriter.Write(parsed.ResultFile, result);
    log.Info($"Result written to '{parsed.ResultFile}': {result.Outcome}.");
    return result.ToExitCode();
}
catch (Exception ex)
{
    log.Error($"Unexpected updater failure: {ex}");
    TryWriteEarlyFailure(parsed.ResultFile, $"Unexpected updater failure: {ex.Message}", parsed);
    return ExitUnexpected;
}
finally
{
    mutex.ReleaseMutex();
}

static void TryWriteEarlyFailure(string? resultFile, string reason, UpdaterArguments? args = null)
{
    if (string.IsNullOrWhiteSpace(resultFile))
    {
        return;
    }

    try
    {
        var now = DateTimeOffset.UtcNow;
        ResultWriter.Write(resultFile, new UpdateResult
        {
            UpdateId = args?.UpdateId ?? "unknown",
            ToVersion = args?.ExpectedVersion ?? "unknown",
            Outcome = UpdateOutcomes.FailedBeforeInstall,
            Reason = reason,
            StartedAt = now,
            CompletedAt = now
        });
    }
    catch (Exception)
    {
        // Nothing more we can do; the exit code still reports failure.
    }
}
