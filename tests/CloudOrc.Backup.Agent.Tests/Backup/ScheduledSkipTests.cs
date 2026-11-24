using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Agent.Backup;
using CloudOrc.Backup.Agent.DurableQueue;

namespace CloudOrc.Backup.Agent.Tests.Backup;

/// <summary>A missed scheduled slot is not run straight after a "Back up now" that already covered it.</summary>
public class ScheduledSkipTests
{
    private static readonly DateTimeOffset Slot = new(2026, 11, 24, 4, 45, 0, TimeSpan.Zero);

    private static BackupJobRecord Job(string id, string state, DateTimeOffset? slot = null, DateTimeOffset? started = null, string kind = BackupJobKinds.Backup) => new()
    {
        JobId = id,
        Kind = kind,
        IdempotencyKey = id,
        CorrelationId = id,
        State = state,
        NotBefore = Slot,
        CreatedAt = Slot,
        ScheduledSlot = slot,
        StartedAt = started
    };

    [Fact]
    public void SkipsWhenABackupSucceededAfterTheSlot()
    {
        var scheduled = Job("sched", BackupJobStates.Queued, slot: Slot);
        var manual = Job("manual", BackupJobStates.Succeeded, started: Slot.AddMinutes(26));
        Assert.True(BackupJobCoordinator.IsCoveredByLaterBackup(scheduled, [scheduled, manual]));
    }

    [Fact]
    public void RunsWhenTheLastBackupWasBeforeTheSlot()
    {
        var scheduled = Job("sched", BackupJobStates.Queued, slot: Slot);
        var old = Job("old", BackupJobStates.Succeeded, started: Slot.AddHours(-20));
        Assert.False(BackupJobCoordinator.IsCoveredByLaterBackup(scheduled, [scheduled, old]));
    }

    [Fact]
    public void RunsWhenTheLaterBackupFailed()
    {
        var scheduled = Job("sched", BackupJobStates.Queued, slot: Slot);
        var failed = Job("failed", BackupJobStates.Failed, started: Slot.AddMinutes(5));
        Assert.False(BackupJobCoordinator.IsCoveredByLaterBackup(scheduled, [scheduled, failed]));
    }

    [Fact]
    public void ManualJobsAlwaysRun()
    {
        var manual = Job("manual2", BackupJobStates.Queued);
        var earlier = Job("manual", BackupJobStates.Succeeded, started: Slot.AddMinutes(1));
        Assert.False(BackupJobCoordinator.IsCoveredByLaterBackup(manual, [manual, earlier]));
    }
}
