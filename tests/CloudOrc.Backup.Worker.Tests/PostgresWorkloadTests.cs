using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Worker.Workloads;

namespace CloudOrc.Backup.Worker.Tests;

public class PostgresWorkloadTests
{
    [Fact]
    public void VirtualPaths_AreStableAbsolutePaths_PerInstanceAndDatabase()
    {
        var spec = new DatabaseSourceSpec { Host = "127.0.0.1", Port = 5432 };
        var path = PostgresWorkload.VirtualPathFor(spec, "app_db");

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.EndsWith("app_db", path, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1_5432", path, StringComparison.Ordinal);
        Assert.True(PostgresWorkload.IsVirtualDatabasePath(path));
        Assert.Equal(path, PostgresWorkload.VirtualPathFor(spec, "app_db"));
        Assert.NotEqual(path, PostgresWorkload.VirtualPathFor(new DatabaseSourceSpec { Port = 5433 }, "app_db"));
    }

    [Theory]
    [InlineData("app_db", true)]
    [InlineData("crm-prod", true)]
    [InlineData("_x", true)]
    [InlineData("1db", false)]
    [InlineData("db; DROP", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DatabaseNames_AreStrictlyValidated(string? name, bool ok) => Assert.Equal(ok, PostgresWorkload.IsSafeDatabaseName(name));

    [Fact]
    public void OrdinaryFolders_AreNotDatabasePaths()
    {
        Assert.False(PostgresWorkload.IsVirtualDatabasePath(@"D:\Data"));
        Assert.False(PostgresWorkload.IsVirtualDatabasePath("/home/user"));
        Assert.False(PostgresWorkload.IsVirtualDatabasePath(null));
    }
}
