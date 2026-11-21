using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.Backup.Agent.Backup;
using CloudOrc.Backup.Agent.Commands;
using CloudOrc.Backup.Agent.Discovery;

namespace CloudOrc.Backup.Agent.Tests.Commands;

/// <summary>MS SQL Server / MySQL support on the agent side: policy validation, database.discover, restore checks, inventory.</summary>
public class DatabaseSupportTests
{
    private static BackupPolicyDocument Policy(BackupSource source) => new()
    {
        PolicyId = "p1",
        PolicyVersionId = "v1",
        EffectiveHash = "h",
        Sources = [source],
        Schedule = new BackupSchedule(),
        Repository = new BackupRepositoryRef { RepositoryId = "r1", StoragePoolId = "pool" }
    };

    [Fact]
    public void NewKinds_AreDatabases()
    {
        Assert.True(BackupSourceKinds.IsDatabase("mssql"));
        Assert.True(BackupSourceKinds.IsDatabase("mysql"));
        Assert.True(BackupSourceKinds.IsDatabase("postgresql"));
        Assert.False(BackupSourceKinds.IsDatabase("folder"));
    }

    [Theory]
    [InlineData("mssql://127.0.0.1:1433")]
    [InlineData("mssql://127.0.0.1/SQLEXPRESS")]
    public void MsSqlSource_WithWindowsAuthAndAnyDatabaseName_IsAccepted(string path)
    {
        var errors = BackupPolicyValidator.Validate(Policy(new BackupSource
        {
            Path = path,
            Kind = BackupSourceKinds.MsSql,
            Database = new DatabaseSourceSpec
            {
                Host = "127.0.0.1", Port = 1433, Instance = path.EndsWith("SQLEXPRESS", StringComparison.Ordinal) ? "SQLEXPRESS" : null,
                UseWindowsAuth = true, Username = "", Databases = ["Sales DB", "AdventureWorks2019", "odd]name"]
            }
        }));
        Assert.Empty(errors);
    }

    [Fact]
    public void MySqlSource_WithSqlLogin_IsAccepted_AndBadNamesAreRefused()
    {
        var ok = BackupPolicyValidator.Validate(Policy(new BackupSource
        {
            Path = "mysql://127.0.0.1:3306",
            Kind = BackupSourceKinds.MySql,
            Database = new DatabaseSourceSpec { Port = 3306, Username = "root", CredentialRef = "db:7", Databases = ["shop", "2024_archive"] }
        }));
        Assert.Empty(ok);

        var bad = BackupPolicyValidator.Validate(Policy(new BackupSource
        {
            Path = "mysql://127.0.0.1:3306",
            Kind = BackupSourceKinds.MySql,
            Database = new DatabaseSourceSpec { Port = 3306, Username = "root", Databases = ["--all-databases"], UseWindowsAuth = true, Instance = "X" }
        }));
        Assert.Contains(bad, e => e.Contains("database names", StringComparison.Ordinal));
        Assert.Contains(bad, e => e.Contains("useWindowsAuth", StringComparison.Ordinal));
        Assert.Contains(bad, e => e.Contains("instance", StringComparison.Ordinal));
    }

    [Fact]
    public void MsSqlSource_WithoutWindowsAuth_NeedsAUserName()
    {
        var errors = BackupPolicyValidator.Validate(Policy(new BackupSource
        {
            Path = "mssql://127.0.0.1:1433",
            Kind = BackupSourceKinds.MsSql,
            Database = new DatabaseSourceSpec { Port = 1433, Username = "" }
        }));
        Assert.Contains(errors, e => e.Contains("database.username", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ database.discover

    [Fact]
    public void Discover_DefaultsPortAndUser_PerEngine()
    {
        var (mssql, _) = DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters { Engine = "mssql", Instance = "SQLEXPRESS", UseWindowsAuth = true });
        Assert.Equal(1433, mssql!.Port);
        Assert.Equal("SQLEXPRESS", mssql.Instance);
        Assert.Equal("127.0.0.1", mssql.Host);

        var (mysql, _) = DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters { Engine = "MySQL", Host = "db.local" });
        Assert.Equal(("mysql", 3306, "root"), (mysql!.Engine, mysql.Port, mysql.Username));

        var (pg, _) = DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters { Engine = "postgresql", UsePeerAuth = true });
        Assert.Equal((5432, "postgres"), (pg!.Port, pg.Username));
    }

    [Theory]
    [InlineData("oracle", null, null, false, false, null, "engine")]
    [InlineData("mysql", "bad host;", null, false, false, null, "host")]
    [InlineData("mysql", null, 70000, false, false, null, "Port")]
    [InlineData("mysql", null, null, true, false, null, "Windows authentication")]
    [InlineData("mssql", null, null, false, true, null, "Peer authentication")]
    [InlineData("postgresql", null, null, false, false, "relative\\bin", "binDirectory")]
    public void Discover_RefusesBadParameters(string engine, string? host, int? port, bool windows, bool peer, string? bin, string expected)
    {
        var (target, error) = DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters
        {
            Engine = engine, Host = host, Port = port, UseWindowsAuth = windows, UsePeerAuth = peer, BinDirectory = bin
        });
        Assert.Null(target);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_InstanceIsOnlyForMsSql() =>
        Assert.Null(DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters { Engine = "mysql", Instance = "SQLEXPRESS" }).Target);

    [Fact]
    public async Task Discover_InvalidParameters_AreRejectedWithTheResultShape_AndNeverEchoThePassword()
    {
        var json = """{"type":"COMMAND","commandId":"c1","commandType":"database.discover","parameters":{"engine":"oracle","password":"Sup3r-Secret"}}""";
        var acceptance = await new DatabaseDiscoverHandler().HandleAsync(
            new TypedCommandContext("c1", null, BackupCommandTypes.DatabaseDiscover, "1.0", null, json), CancellationToken.None);

        Assert.False(acceptance.Accepted);
        var serialized = JsonSerializer.Serialize(acceptance, ProtocolJson.Options);
        Assert.DoesNotContain("Sup3r-Secret", serialized);
        using var doc = JsonDocument.Parse(serialized);
        var data = doc.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("ok").GetBoolean());
        foreach (var field in new[] { "engine", "serverVersion", "toolsFound", "databases", "error" })
        {
            Assert.True(data.TryGetProperty(field, out _), field);
        }
    }

    [Fact]
    public void Discover_ParametersToString_RedactsThePassword() =>
        Assert.DoesNotContain("pw123", new DatabaseDiscoverParameters { Engine = "mysql", Password = "pw123" }.ToString());

    [Fact]
    public void Discover_MySqlArguments_HaveNoPassword()
    {
        var (t, _) = DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters { Engine = "mysql", Username = "bk", Password = "pw123" });
        var args = DatabaseDiscoverHandler.MySqlDiscoverArguments(t!);
        Assert.DoesNotContain(args, a => a.Contains("pw123", StringComparison.Ordinal) || a.StartsWith("--password", StringComparison.Ordinal));
        Assert.Contains("--user=bk", args);
    }

    [Fact]
    public void Discover_PostgresArguments_HaveNoPassword_AndPeerUsesTheSocket()
    {
        var (t, _) = DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters { Engine = "postgresql", Host = "db", Password = "pw123" });
        var args = DatabaseDiscoverHandler.PostgresDiscoverArguments(t!);
        Assert.DoesNotContain(args, a => a.Contains("pw123", StringComparison.Ordinal));
        Assert.Contains("--no-password", args);
        Assert.Contains("db", args);

        var (peer, _) = DatabaseDiscoverHandler.Validate(new DatabaseDiscoverParameters { Engine = "postgresql", Host = "db", UsePeerAuth = true });
        Assert.DoesNotContain("-h", DatabaseDiscoverHandler.PostgresDiscoverArguments(peer!));
    }

    [Fact]
    public void Discover_MySqlOutput_IsParsed_WithoutSystemSchemas()
    {
        var (version, dbs) = DatabaseDiscoverHandler.ParseMySqlDiscoverOutput("10.11.6-MariaDB\r\ninformation_schema\tNULL\nmysql\t123\nshop\t4096\nempty\tNULL\n");
        Assert.Equal("10.11.6-MariaDB", version);
        Assert.Equal(["shop", "empty"], dbs.Select(d => d.Name));
        Assert.Equal(4096, dbs[0].SizeBytes);
        Assert.Null(dbs[1].SizeBytes);
        Assert.Null(dbs[0].RecoveryModel);
    }

    [Fact]
    public void DiscoverCommand_IsTypedAndAllowListed()
    {
        Assert.Equal("database.discover", BackupCommandTypes.DatabaseDiscover);
        Assert.True(BackupCommandTypes.IsTyped("database.discover"));
        Assert.Equal("database.discover.v1", AgentCapabilities.DatabaseDiscoverV1);
        Assert.Equal("database.mssql.v1", AgentCapabilities.DatabaseMsSqlV1);
        Assert.Equal("database.mysql.v1", AgentCapabilities.DatabaseMySqlV1);
        Assert.Equal("database.postgresql.v1", AgentCapabilities.DatabasePostgreSqlV1);
    }

    // ------------------------------------------------------------------ restore.execute

    private static RestoreExecuteParameters Restore(DatabaseRestoreSpec database, string sourceRoot = "mssql://127.0.0.1/SQLEXPRESS") => new()
    {
        RestoreJobId = "rj1",
        RecoveryPointId = "rp1",
        RepositoryId = "repo1",
        Engine = BackupEngineNames.Kopia,
        EngineSnapshotId = "k0123456789abcdef0123456789abcdef",
        SourceRoot = sourceRoot,
        Selection = ["Sales.bak"],
        TargetKind = RestoreTargetKinds.Alternate,
        TargetPath = OperatingSystem.IsWindows() ? @"C:\ProgramData\CloudOrc\Backup\restore-db\x" : "/var/lib/cloudorc-backup/restore-db/x",
        Mode = RestoreModes.Rename,
        Database = database
    };

    [Fact]
    public void Restore_Replace_IsRefusedWithoutConfirmReplace()
    {
        var error = RestoreExecuteHandler.Validate(Restore(new DatabaseRestoreSpec { Engine = "mssql", Mode = "replace", TargetDatabase = "Sales" }));
        Assert.NotNull(error);
        Assert.Contains("confirm", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_Replace_IsAcceptedWhenConfirmed_ForMsSqlOnly()
    {
        Assert.Null(RestoreExecuteHandler.Validate(Restore(new DatabaseRestoreSpec { Engine = "mssql", Mode = "replace", TargetDatabase = "Sales", ConfirmReplace = true })));
        Assert.NotNull(RestoreExecuteHandler.Validate(Restore(new DatabaseRestoreSpec { Engine = "mysql", Mode = "replace", ConfirmReplace = true }, "mysql://127.0.0.1:3306")));
    }

    [Fact]
    public void Restore_LogicalDatabaseRoot_IsOnlyAcceptedWithADatabaseSection()
    {
        Assert.Null(RestoreExecuteHandler.Validate(Restore(new DatabaseRestoreSpec { Engine = "mysql", Mode = "new-database", TargetDatabase = "shop_copy" }, "mysql://127.0.0.1:3306")));
        var withoutDatabase = Restore(new DatabaseRestoreSpec(), "mssql://127.0.0.1:1433");
        var plain = new RestoreExecuteParameters
        {
            RestoreJobId = withoutDatabase.RestoreJobId, RecoveryPointId = withoutDatabase.RecoveryPointId, RepositoryId = withoutDatabase.RepositoryId,
            Engine = withoutDatabase.Engine, EngineSnapshotId = withoutDatabase.EngineSnapshotId, SourceRoot = "mssql://127.0.0.1:1433",
            TargetPath = withoutDatabase.TargetPath
        };
        Assert.NotNull(RestoreExecuteHandler.Validate(plain));
    }

    // ------------------------------------------------------------------ inventory

    [Theory]
    [InlineData("1433", 1433)]
    [InlineData("50123,50124", 50123)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData(null, null)]
    public void SqlServerPorts_AreParsedFromTheRegistryValues(string? value, int? expected) =>
        Assert.Equal(expected, MachineInventoryCollector.ParseSqlPort(value));

    [Fact]
    public void InventoryDatabase_SerializesTheInstance()
    {
        var json = JsonSerializer.Serialize(new InventoryDatabase { Engine = "mssql", Instance = "SQLEXPRESS", BackupSupported = true }, ProtocolJson.Options);
        Assert.Contains("\"instance\":\"SQLEXPRESS\"", json);
        Assert.Contains("\"backupSupported\":true", json);
    }
}
