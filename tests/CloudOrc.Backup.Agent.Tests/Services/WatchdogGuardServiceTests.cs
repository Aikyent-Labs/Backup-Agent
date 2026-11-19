using CloudOrc.Backup.Agent.Platform;
using CloudOrc.Backup.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudOrc.Backup.Agent.Tests.Services;

public sealed class WatchdogGuardServiceTests
{
    [Fact]
    public void StartsAStoppedWatchdog()
    {
        var starts = 0;
        var guard = new WatchdogGuardService(NullLogger<WatchdogGuardService>.Instance)
        {
            Query = () => ServiceControl.State.Stopped,
            Start = () => { starts++; return null; }
        };

        Assert.True(guard.CheckOnce());
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(ServiceControl.State.Running)]
    [InlineData(ServiceControl.State.NotInstalled)]
    [InlineData(ServiceControl.State.Unknown)]
    public void LeavesTheWatchdogAloneUnlessItIsStopped(ServiceControl.State state)
    {
        var starts = 0;
        var guard = new WatchdogGuardService(NullLogger<WatchdogGuardService>.Instance)
        {
            Query = () => state,
            Start = () => { starts++; return null; }
        };

        Assert.False(guard.CheckOnce());
        Assert.Equal(0, starts);
    }

    [Fact]
    public void AFailedStartIsReportedAsNotStartedAndRetriedNextTime()
    {
        var starts = 0;
        var guard = new WatchdogGuardService(NullLogger<WatchdogGuardService>.Instance)
        {
            Query = () => ServiceControl.State.Stopped,
            Start = () => { starts++; return "disabled"; }
        };

        Assert.False(guard.CheckOnce());
        Assert.False(guard.CheckOnce());
        Assert.Equal(2, starts);
    }
}
