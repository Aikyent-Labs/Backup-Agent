using System.Text.Json;
using CloudOrc.Backup.Watchdog.Configuration;
using CloudOrc.Backup.Watchdog.ControlAgentManagement;

namespace CloudOrc.Backup.Watchdog.Tests.ControlAgentManagement;

public sealed class AgentLivenessFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-liveness-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, AgentLivenessFile.FileName);

    public AgentLivenessFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private void Write(DateTimeOffset at) => File.WriteAllText(FilePath, JsonSerializer.Serialize(new { at, pid = 42 }));

    [Fact]
    public void Fresh_file_counts_as_alive()
    {
        var now = DateTimeOffset.UtcNow;
        Write(now.AddSeconds(-20));
        Assert.True(AgentLivenessFile.IsFresh(FilePath, TimeSpan.FromSeconds(60), now));
    }

    [Fact]
    public void Stale_file_does_not_count()
    {
        var now = DateTimeOffset.UtcNow;
        Write(now.AddMinutes(-5));
        Assert.False(AgentLivenessFile.IsFresh(FilePath, TimeSpan.FromSeconds(60), now));
    }

    [Fact]
    public void Missing_or_broken_file_does_not_count()
    {
        Assert.False(AgentLivenessFile.IsFresh(FilePath, TimeSpan.FromSeconds(60)));
        File.WriteAllText(FilePath, "not json");
        Assert.False(AgentLivenessFile.IsFresh(FilePath, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Liveness_file_lives_in_the_agent_data_directory()
    {
        var options = new WatchdogOptions { DataDirectory = Path.Combine(_dir, "watchdog") };
        Assert.Equal(Path.Combine(_dir, "agent-liveness.json"), options.LivenessFile);
    }
}
