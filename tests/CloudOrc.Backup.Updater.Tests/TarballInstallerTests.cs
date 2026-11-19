using System.Formats.Tar;
using System.IO.Compression;

namespace CloudOrc.Backup.Updater.Tests;

public sealed class TarballInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tarball-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static MemoryStream Tar(params (string Name, TarEntryType Type, string? Content, string? Link)[] entries)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, type, content, link) in entries)
            {
                var entry = new PaxTarEntry(type, name);
                if (content is not null)
                {
                    entry.DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                }

                if (link is not null)
                {
                    entry.LinkName = link;
                }

                writer.WriteEntry(entry);
            }
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void ExtractsFilesAndFolders_OverwritingTheOldVersion()
    {
        var target = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(target, "agent"));
        File.WriteAllText(Path.Combine(target, "agent", "version.txt"), "old");

        TarballInstallerRunner.ExtractSafely(Tar(
            ("./agent/", TarEntryType.Directory, null, null),
            ("./agent/version.txt", TarEntryType.RegularFile, "new", null),
            ("worker/cloudorc-backup-worker", TarEntryType.RegularFile, "bin", null)), target);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "agent", "version.txt")));
        Assert.Equal("bin", File.ReadAllText(Path.Combine(target, "worker", "cloudorc-backup-worker")));
    }

    [Fact]
    public void PathTraversal_IsRefused()
    {
        var target = Path.Combine(_root, "install");
        Assert.Throws<InvalidOperationException>(() =>
            TarballInstallerRunner.ExtractSafely(Tar(("../evil.txt", TarEntryType.RegularFile, "x", null)), target));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void Links_AreRefused()
    {
        var target = Path.Combine(_root, "install");
        Assert.Throws<InvalidOperationException>(() =>
            TarballInstallerRunner.ExtractSafely(Tar(("agent/link", TarEntryType.SymbolicLink, null, "/etc/passwd")), target));
    }

    [Fact]
    public void ServiceNames_AreTheBackupAgentAndItsWatchdog()
    {
        Assert.Equal(2, UpdatePlatform.ServiceNames.Count);
        Assert.Contains("backup", UpdatePlatform.ServiceNames[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("watchdog", UpdatePlatform.ServiceNames[1], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("cloudorc-backup" + (OperatingSystem.IsWindows() ? ".exe" : ""), UpdatePlatform.AgentExecutable("/opt/x"));
    }

    [Fact]
    public void GzipPackage_RoundTrip()
    {
        var target = Path.Combine(_root, "install");
        var package = Path.Combine(_root, "update.tar.gz");
        Directory.CreateDirectory(_root);
        using (var file = File.Create(package))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            Tar(("agent/a.txt", TarEntryType.RegularFile, "A", null)).CopyTo(gzip);
        }

        using var read = File.OpenRead(package);
        using var unzip = new GZipStream(read, CompressionMode.Decompress);
        TarballInstallerRunner.ExtractSafely(unzip, target);
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "agent", "a.txt")));
    }
}
