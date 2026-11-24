using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Worker.Engines;
using CloudOrc.Backup.Worker.Scanning;

namespace CloudOrc.Backup.Worker.Tests;

/// <summary>Speed defaults: Kopia upload writers and the folders never worth backing up.</summary>
public class SpeedDefaultsTests
{
    [Fact]
    public void KopiaUsesUploadParallelismWhenGiven()
    {
        Assert.Equal(8, KopiaEngine.UploadParallelism(new WorkerBudget { Profile = "balanced", Parallelism = 2, UploadParallelism = 8 }));
    }

    [Fact]
    public void OlderAgentsWithoutUploadParallelismKeepParallelism()
    {
        Assert.Equal(3, KopiaEngine.UploadParallelism(new WorkerBudget { Profile = "balanced", Parallelism = 3 }));
    }

    [Fact]
    public void UploadParallelismIsBounded()
    {
        Assert.Equal(32, KopiaEngine.UploadParallelism(new WorkerBudget { Profile = "throughput", Parallelism = 2, UploadParallelism = 500 }));
    }

    [Theory]
    [InlineData("web/node_modules/react/index.js", true)]
    [InlineData("node_modules", true)]
    [InlineData("app/__pycache__/x.pyc", true)]
    [InlineData("$RECYCLE.BIN/S-1-5/file.txt", true)]
    [InlineData("pagefile.sys", true)]
    [InlineData("Docs/~$Budget.xlsx", true)]
    [InlineData("Docs/Budget.xlsx", false)]
    [InlineData("repo/.git/HEAD", false)]
    [InlineData("Tally/Data/10000/Company.900", false)]
    [InlineData("project/bin/report.pdf", false)]
    public void DefaultExclusionsSkipOnlyRebuildableAndSystemFiles(string relativePath, bool excluded)
    {
        var matcher = new GlobMatcher(DefaultExcludes.Patterns);
        var parts = relativePath.Split('/');
        var hit = Enumerable.Range(1, parts.Length).Any(n => matcher.IsMatch(string.Join('/', parts.Take(n))));
        Assert.Equal(excluded, hit);
    }

    [Fact]
    public void CustomerRulesAreKeptAfterDefaults()
    {
        var all = DefaultExcludes.With(["*.bak", "node_modules"]);
        Assert.Contains("*.bak", all);
        Assert.Single(all, p => p == "node_modules");
    }
}
