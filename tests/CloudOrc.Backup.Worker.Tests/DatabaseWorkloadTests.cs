using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Shared;
using CloudOrc.Backup.Worker.Engines;
using CloudOrc.Backup.Worker.Orchestration;
using CloudOrc.Backup.Worker.Workloads;
using Microsoft.Data.SqlClient;

namespace CloudOrc.Backup.Worker.Tests;

/// <summary>MySQL / MariaDB and MS SQL Server workloads: everything that can be checked without a real server.</summary>
public class MySqlWorkloadTests
{
    private static readonly DatabaseSourceSpec Spec = new() { Host = "db.local", Port = 3307, Username = "backup", CredentialRef = "db:1" };

    [Fact]
    public void DumpArguments_AreTheConsistentSingleDatabaseDump_WithoutDatabasesFlagOrPassword()
    {
        var args = MySqlWorkload.DumpArguments(Spec, "sales");

        Assert.Equal(["--host=db.local", "--port=3307", "--user=backup"], args.Take(3));
        foreach (var flag in new[] { "--single-transaction", "--quick", "--routines", "--triggers", "--events", "--hex-blob", "--default-character-set=utf8mb4" })
        {
            Assert.Contains(flag, args);
        }

        Assert.Equal("sales", args[^1]);
        Assert.DoesNotContain("--databases", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--password", StringComparison.Ordinal) || a.StartsWith("-p", StringComparison.Ordinal));
    }

    [Fact]
    public void ClientArguments_NeverCarryAPassword()
    {
        var all = MySqlWorkload.ListArguments(Spec)
            .Concat(MySqlWorkload.CreateDatabaseArguments(Spec, "sales_copy"))
            .Concat(MySqlWorkload.LoadArguments(Spec, "sales_copy"))
            .ToList();
        Assert.DoesNotContain(all, a => a.Contains("password", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-p", StringComparison.Ordinal));
        Assert.Contains("SHOW DATABASES", MySqlWorkload.ListArguments(Spec));
        Assert.Contains("CREATE DATABASE `sales_copy` CHARACTER SET utf8mb4", MySqlWorkload.CreateDatabaseArguments(Spec, "sales_copy"));
        Assert.Contains("--database=sales_copy", MySqlWorkload.LoadArguments(Spec, "sales_copy"));
    }

    [Fact]
    public void Password_GoesIntoMysqlPwd_AndAnInheritedOneIsRemoved()
    {
        var env = new Dictionary<string, string?> { ["MYSQL_PWD"] = "inherited" };
        MySqlLocator.ApplyPassword(env, null);
        Assert.False(env.ContainsKey("MYSQL_PWD"));

        MySqlLocator.ApplyPassword(env, "s3cret!");
        Assert.Equal("s3cret!", env["MYSQL_PWD"]);
    }

    [Fact]
    public void QuoteIdentifier_DoublesBackticks() => Assert.Equal("`a``b`", MySqlWorkload.QuoteIdentifier("a`b"));

    [Fact]
    public void AllDatabases_ExcludeSystemSchemas_ExclusionsAndUnsafeNames()
    {
        var list = MySqlWorkload.FilterDatabaseList("information_schema\nmysql\nperformance_schema\nsys\nsales\nhr\n-oops\narchive\r\n", ["archive"]);
        Assert.Equal(["sales", "hr"], list);
    }

    [Theory]
    [InlineData("mysqldump  Ver 8.0.36 for Win64 on x86_64 (MySQL Community Server - GPL)", 8, 0)]
    [InlineData("mysqldump  Ver 10.19 Distrib 10.6.16-MariaDB, for debian-linux-gnu (x86_64)", 10, 6)]
    [InlineData("mysqldump from 11.4.2-MariaDB, client 10.19 for Linux (x86_64)", 11, 4)]
    public void DumpVersionParsing(string text, int major, int minor) => Assert.Equal(new Version(major, minor), MySqlLocator.ParseVersion(text));

    [Fact]
    public void FriendlyErrors_ExplainLoginAndReachability()
    {
        Assert.Contains("login failed for user 'backup'", MySqlLocator.FriendlyError("mysqldump: Got error: 1045: Access denied for user 'backup'@'localhost' (using password: YES)", "h", 3306, "backup"));
        Assert.Contains("Could not reach", MySqlLocator.FriendlyError("ERROR 2003 (HY000): Can't connect to MySQL server on 'h:3306' (10061)", "h", 3306, "backup"));
    }

    [Fact]
    public void VirtualPaths_ArePerEngineInstanceAndDatabase()
    {
        var path = MySqlWorkload.VirtualPathFor(Spec, "sales");
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Contains("mysql", path, StringComparison.Ordinal);
        Assert.Contains("db.local_3307", path, StringComparison.Ordinal);
        Assert.True(PostgresWorkload.IsVirtualDatabasePath(path));
    }
}

public class MsSqlWorkloadTests
{
    [Theory]
    [InlineData("127.0.0.1", 1433, null, "127.0.0.1,1433")]
    [InlineData("127.0.0.1", 1500, null, "127.0.0.1,1500")]
    [InlineData("127.0.0.1", 1433, "SQLEXPRESS", @"127.0.0.1\SQLEXPRESS")]
    [InlineData("srv", 0, "SQLEXPRESS", @"srv\SQLEXPRESS")]
    [InlineData("srv", 50123, "SQLEXPRESS", @"srv\SQLEXPRESS,50123")]
    [InlineData("srv", 1433, "MSSQLSERVER", "srv,1433")]
    public void DataSource_DefaultAndNamedInstances(string host, int port, string? instance, string expected) =>
        Assert.Equal(expected, MsSqlSupport.DataSource(host, port, instance));

    [Fact]
    public void ConnectionString_WindowsAuth_HasNoUserOrPassword()
    {
        var spec = new DatabaseSourceSpec { Host = "127.0.0.1", Port = 1433, Instance = "SQLEXPRESS", UseWindowsAuth = true, Username = "ignored" };
        var b = new SqlConnectionStringBuilder(MsSqlSupport.ConnectionString(spec, "not-used"));

        Assert.True(b.IntegratedSecurity);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password);
        Assert.Equal(@"127.0.0.1\SQLEXPRESS", b.DataSource);
        Assert.True(b.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Optional, b.Encrypt);
        Assert.Equal("master", b.InitialCatalog);
    }

    [Fact]
    public void ConnectionString_SqlLogin_UsesTheGrantPassword()
    {
        var spec = new DatabaseSourceSpec { Host = "db", Port = 1533, Username = "backup_op" };
        var b = new SqlConnectionStringBuilder(MsSqlSupport.ConnectionString(spec, "p;w'd=x"));

        Assert.False(b.IntegratedSecurity);
        Assert.Equal("backup_op", b.UserID);
        Assert.Equal("p;w'd=x", b.Password); // properly escaped by the builder, not concatenated
        Assert.Equal("db,1533", b.DataSource);
    }

    [Fact]
    public void Identifiers_AndLiterals_AreQuoted()
    {
        Assert.Equal("[my]]db]", MsSqlSupport.QuoteIdentifier("my]db"));
        Assert.Equal("[Sales DB]", MsSqlSupport.QuoteIdentifier("Sales DB"));
        Assert.Equal("N'C:\\x\\O''Brien.bak'", MsSqlSupport.QuoteLiteral(@"C:\x\O'Brien.bak"));
    }

    [Fact]
    public void BackupSql_IsCopyOnlyUncompressedWithChecksum()
    {
        var sql = MsSqlSupport.BackupDatabaseSql("a]b", @"D:\Backup\CloudOrcBackup\j1\a_b.bak");
        Assert.Equal(@"BACKUP DATABASE [a]]b] TO DISK = N'D:\Backup\CloudOrcBackup\j1\a_b.bak' WITH COPY_ONLY, INIT, CHECKSUM, NO_COMPRESSION, STATS = 5", sql);
    }

    [Fact]
    public void ReplaceSql_SingleUserThenRestoreWithReplace_InOneBatch()
    {
        var sql = MsSqlSupport.ReplaceDatabaseSql("Sales]X", @"C:\b\r.bak", databaseExists: true);
        Assert.Equal("ALTER DATABASE [Sales]]X] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\nRESTORE DATABASE [Sales]]X] FROM DISK = N'C:\\b\\r.bak' WITH REPLACE, RECOVERY, STATS = 5;", sql);
        Assert.DoesNotContain("SINGLE_USER", MsSqlSupport.ReplaceDatabaseSql("New", @"C:\b\r.bak", databaseExists: false));
        Assert.Equal("ALTER DATABASE [Sales]]X] SET MULTI_USER;", MsSqlSupport.MultiUserSql("Sales]X"));
    }

    [Fact]
    public void RestoreAsNew_MovesEveryFileToNewNames()
    {
        var moves = MsSqlSupport.PlanMoves("sales_copy", [("Sales", "D"), ("Sales_log", "L"), ("Sales_2", "D"), ("FS", "S")], @"C:\SQL\Data\", @"L:\Logs");
        Assert.Equal(
        [
            ("Sales", @"C:\SQL\Data\sales_copy_1.mdf"),
            ("Sales_log", @"L:\Logs\sales_copy_2.ldf"),
            ("Sales_2", @"C:\SQL\Data\sales_copy_3.ndf"),
            ("FS", @"C:\SQL\Data\sales_copy_4")
        ], moves);

        var sql = MsSqlSupport.RestoreAsNewDatabaseSql("sales_copy", @"C:\B\r.bak", moves.Take(2).ToList());
        Assert.Equal(@"RESTORE DATABASE [sales_copy] FROM DISK = N'C:\B\r.bak' WITH MOVE N'Sales' TO N'C:\SQL\Data\sales_copy_1.mdf', MOVE N'Sales_log' TO N'L:\Logs\sales_copy_2.ldf', RECOVERY, STATS = 5", sql);
        Assert.DoesNotContain("REPLACE", sql);
    }

    [Fact]
    public void LinuxServerPaths_KeepForwardSlashes() =>
        Assert.Equal("/var/opt/mssql/data/x_1.mdf", MsSqlSupport.JoinServerPath("/var/opt/mssql/data", "x_1.mdf"));

    [Fact]
    public void BackupFolder_FallsBackToTheErrorLogSibling() =>
        Assert.Equal(@"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Backup",
            MsSqlSupport.BackupDirectoryFromErrorLog(@"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Log\ERRORLOG"));

    [Fact]
    public void FriendlyErrors_ExplainTheLoginFix()
    {
        var windows = MsSqlSupport.FriendlyError(18456, "Login failed for user 'NT AUTHORITY\\SYSTEM'.", @"127.0.0.1\SQLEXPRESS", true, null);
        Assert.Contains("CREATE LOGIN [NT AUTHORITY\\SYSTEM] FROM WINDOWS", windows);
        Assert.Contains("ALTER SERVER ROLE sysadmin ADD MEMBER [NT AUTHORITY\\SYSTEM]", windows);

        var sqlLogin = MsSqlSupport.FriendlyError(18456, "Login failed for user 'bk'.", "db,1433", false, "bk");
        Assert.Contains("'bk'", sqlLogin);
        Assert.Contains("db_backupoperator", sqlLogin);

        Assert.Contains("Could not reach SQL Server", MsSqlSupport.FriendlyError(53, "network path not found", "db,1433", false, "bk"));
        Assert.Contains("already exists", MsSqlSupport.FriendlyError(1801, "exists", "db", false, "bk", "x"));
    }

    [Fact]
    public void FileStem_IsTheDatabaseName_SoTheBackendCanStripTheExtension()
    {
        Assert.Equal("Sales DB", DatabaseVirtualPaths.FileStem("Sales DB"));
        var odd = DatabaseVirtualPaths.FileStem("a/b:c");
        Assert.StartsWith("a_b_c_", odd, StringComparison.Ordinal);
        Assert.NotEqual(odd, DatabaseVirtualPaths.FileStem("a_b_c"));
        Assert.Equal("Sales DB.bak", new MsSqlWorkload(new DatabaseSourceSpec(), null, "j", new BackupRetention(), (_, _) => { }).FileNameFor("Sales DB"));
    }

    [Fact]
    public void VirtualRoot_UsesInstanceOrPort()
    {
        Assert.Contains("127.0.0.1_SQLEXPRESS", MsSqlWorkload.VirtualRoot(new DatabaseSourceSpec { Host = "127.0.0.1", Instance = "SQLEXPRESS", Port = 1433 }), StringComparison.Ordinal);
        Assert.Contains("127.0.0.1_1433", MsSqlWorkload.VirtualRoot(new DatabaseSourceSpec { Host = "127.0.0.1", Port = 1433 }), StringComparison.Ordinal);
    }
}

/// <summary>How a backend database restore (logical path + "&lt;db&gt;.ext") maps to the per-database engine snapshot.</summary>
public class DatabaseRestoreMappingTests
{
    [Theory]
    [InlineData("mssql://127.0.0.1:1433", "mssql")]
    [InlineData("mssql://srv/SQLEXPRESS", "mssql")]
    [InlineData("MySQL://h:3306", "mysql")]
    [InlineData("postgresql://h:5432", "postgresql")]
    public void EngineFromLogicalPath(string path, string engine)
    {
        Assert.Equal(engine, JobExecutor.EngineOfLogicalPath(path));
        Assert.True(DatabaseVirtualPaths.IsLogicalDatabasePath(path));
    }

    [Fact]
    public void SelectedFile_MapsToTheVirtualPathItWasBackedUpUnder()
    {
        var mssql = new DatabaseSourceSpec { Host = "127.0.0.1", Port = 1433, Instance = "SQLEXPRESS" };
        Assert.Equal(MsSqlWorkload.VirtualPathFor(mssql, "Sales DB"), JobExecutor.VirtualPathForFile("mssql", mssql, "Sales DB.bak"));

        var mysql = new DatabaseSourceSpec { Host = "127.0.0.1", Port = 3306 };
        Assert.Equal(MySqlWorkload.VirtualPathFor(mysql, "shop"), JobExecutor.VirtualPathForFile("mysql", mysql, "shop.sql"));

        var pg = new DatabaseSourceSpec { Host = "127.0.0.1", Port = 5432 };
        Assert.Equal(PostgresWorkload.VirtualPathFor(pg, "app"), JobExecutor.VirtualPathForFile("postgresql", pg, "app.dump"));
        Assert.Equal(PostgresWorkload.VirtualPathFor(pg, "_globals"), JobExecutor.VirtualPathForFile("postgresql", pg, "globals.sql"));

        Assert.Null(JobExecutor.VirtualPathForFile("mssql", mssql, "notes.txt"));
    }

    [Fact]
    public void SiblingSnapshot_IsFoundByJobDescriptionAndPath()
    {
        const string list = """
        [
          {"id":"k1","source":{"path":"C:\\CloudOrc-Database\\mssql\\h_1433\\a"},"description":"CloudOrc job J1"},
          {"id":"k2","source":{"path":"C:\\CloudOrc-Database\\mssql\\h_1433\\b"},"description":"CloudOrc job J1"},
          {"id":"k3","source":{"path":"C:\\CloudOrc-Database\\mssql\\h_1433\\b"},"description":"CloudOrc job J0"}
        ]
        """;
        Assert.Equal("k2", KopiaEngine.FindSnapshotOfSameJob(list, "k1", @"C:\CloudOrc-Database\mssql\h_1433\b"));
        Assert.Equal("k1", KopiaEngine.FindSnapshotOfSameJob(list, "k1", @"C:\CloudOrc-Database\mssql\h_1433\a"));
        Assert.Null(KopiaEngine.FindSnapshotOfSameJob(list, "k1", @"C:\CloudOrc-Database\mssql\h_1433\c"));
        Assert.Null(KopiaEngine.FindSnapshotOfSameJob(list, "missing", @"C:\CloudOrc-Database\mssql\h_1433\a"));
    }

    [Fact]
    public void Replace_IsRefusedWithoutConfirmation_AndOutsideMsSql()
    {
        Assert.Contains("confirm", DatabaseRestoreModes.Validate(new DatabaseRestoreSpec { Engine = "mssql", Mode = DatabaseRestoreModes.Replace }), StringComparison.OrdinalIgnoreCase);
        Assert.Null(DatabaseRestoreModes.Validate(new DatabaseRestoreSpec { Engine = "mssql", Mode = DatabaseRestoreModes.Replace, ConfirmReplace = true, TargetDatabase = "Sales" }));
        Assert.Contains("not supported", DatabaseRestoreModes.Validate(new DatabaseRestoreSpec { Engine = "postgresql", Mode = DatabaseRestoreModes.Replace, ConfirmReplace = true }));
        Assert.Contains("not supported", DatabaseRestoreModes.Validate(new DatabaseRestoreSpec { Engine = "mysql", Mode = DatabaseRestoreModes.Replace, ConfirmReplace = true }));
        Assert.Equal(".bak", DatabaseRestoreModes.FileExtensionFor("mssql"));
        Assert.Equal(".sql", DatabaseRestoreModes.FileExtensionFor("mysql"));
        Assert.Equal(".dump", DatabaseRestoreModes.FileExtensionFor("postgresql"));
    }
}
