using CloudOrc.Backup.Agent.Backup;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.Backup.Agent.Tests.Backup;

/// <summary>Backup type: when a backup re-reads every file (full) instead of trusting the change cache.</summary>
public sealed class BackupModeTests
{
    private static readonly DateTimeOffset Sunday = new(2026, 11, 15, 22, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset Monday = Sunday.AddDays(1);

    [Fact]
    public void Incremental_IsTheDefault()
    {
        Assert.False(BackupModes.IsFullScan(null, null, null, Sunday));
        Assert.False(BackupModes.IsFullScan(null, BackupModes.Incremental, "Sunday", Sunday));
        Assert.Equal(BackupModes.Incremental, new BackupPolicyDocument
        {
            PolicyId = "p", PolicyVersionId = "v", EffectiveHash = "h",
            Schedule = new BackupSchedule(), Repository = new BackupRepositoryRef { RepositoryId = "r", StoragePoolId = "s", Engine = "kopia" }
        }.BackupMode);
    }

    [Fact]
    public void FullPolicy_IsAlwaysFull()
    {
        Assert.True(BackupModes.IsFullScan(null, BackupModes.Full, null, Monday));
    }

    [Fact]
    public void WeeklyFull_IsFullOnlyOnTheChosenDay()
    {
        Assert.True(BackupModes.IsFullScan(null, BackupModes.WeeklyFull, "Sunday", Sunday));
        Assert.False(BackupModes.IsFullScan(null, BackupModes.WeeklyFull, "Sunday", Monday));
    }

    [Fact]
    public void MonthlyFullIsFullOnlyOnItsDayOfTheMonth()
    {
        var first = new DateTimeOffset(2026, 12, 1, 22, 0, 0, TimeSpan.Zero);
        var second = first.AddDays(1);
        Assert.True(BackupModes.IsFullScan(null, BackupModes.MonthlyFull, null, first, null, null));
        Assert.False(BackupModes.IsFullScan(null, BackupModes.MonthlyFull, null, second, null, null));
        Assert.True(BackupModes.IsFullScan(null, BackupModes.MonthlyFull, null, second, null, 2));
    }

    [Fact]
    public void CustomIsFullOnTheChosenWeekdaysOrDayOfTheMonth()
    {
        var wednesday = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero);
        var thursday = wednesday.AddDays(1);
        string[] days = ["Wednesday", "Saturday"];
        Assert.True(BackupModes.IsFullScan(null, BackupModes.Custom, null, wednesday, days, null));
        Assert.False(BackupModes.IsFullScan(null, BackupModes.Custom, null, thursday, days, null));
        Assert.True(BackupModes.IsFullScan(null, BackupModes.Custom, null, thursday, days, 19));
        Assert.False(BackupModes.IsFullScan(BackupModes.Incremental, BackupModes.Custom, null, wednesday, days, null));
        Assert.True(BackupModes.IsKnown(BackupModes.Custom));
    }

    [Fact]
    public void CustomDailyFullIsTheFirstBackupOfEachDay()
    {
        var policy = new BackupPolicyDocument
        {
            PolicyId = "p1",
            PolicyVersionId = "v1",
            EffectiveHash = "h1",
            Schedule = new BackupSchedule(),
            Repository = new BackupRepositoryRef { RepositoryId = "r1", StoragePoolId = "s1" },
            BackupMode = BackupModes.Custom,
            FullBackupDaily = true
        };
        var morning = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        Assert.True(BackupModes.IsFullScan(null, policy, morning, lastFullLocalDate: null));
        Assert.True(BackupModes.IsFullScan(null, policy, morning, DateOnly.FromDateTime(morning.DateTime.AddDays(-1))));
        Assert.False(BackupModes.IsFullScan(null, policy, morning.AddHours(1), DateOnly.FromDateTime(morning.DateTime)));
        Assert.False(BackupModes.IsFullScan(BackupModes.Incremental, policy, morning, null));
        Assert.DoesNotContain(BackupPolicyValidator.Validate(policy), e => e.Contains("custom", StringComparison.OrdinalIgnoreCase));
        Assert.True(BackupModes.IsKnown(BackupModes.MonthlyFull));
        Assert.True(BackupModes.IsFullScan(null, BackupModes.WeeklyFull, "monday", Monday));
    }

    [Fact]
    public void PerRunRequest_WinsOverThePolicy()
    {
        Assert.True(BackupModes.IsFullScan(BackupModes.Full, BackupModes.Incremental, null, Monday));
        Assert.False(BackupModes.IsFullScan(BackupModes.Incremental, BackupModes.Full, null, Monday));
    }
}
