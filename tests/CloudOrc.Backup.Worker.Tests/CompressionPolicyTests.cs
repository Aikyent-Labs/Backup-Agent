using CloudOrc.Backup.Worker.Engines;

namespace CloudOrc.Backup.Worker.Tests;

/// <summary>Folder backups are compressed (zstd) before Kopia encrypts them; already-compressed files are skipped.</summary>
public class CompressionPolicyTests
{
    [Fact]
    public void FolderBackupsUseZstd()
    {
        var args = KopiaEngine.FolderCompressionArgs().ToList();
        Assert.Contains("--compression=zstd", args);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".mp4")]
    [InlineData(".zip")]
    [InlineData(".7z")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pdf")]
    public void AlreadyCompressedFilesAreNotCompressedAgain(string extension)
    {
        Assert.Contains($"--add-never-compress={extension}", KopiaEngine.FolderCompressionArgs());
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    [InlineData(".sql")]
    [InlineData(".log")]
    [InlineData(".900")] // Tally data files
    [InlineData(".tsf")]
    public void TextAndTallyDataAreCompressed(string extension)
    {
        Assert.DoesNotContain($"--add-never-compress={extension}", KopiaEngine.FolderCompressionArgs());
    }

    [Fact]
    public void NeverCompressListIsCleanAndUnique()
    {
        var list = KopiaEngine.NeverCompressExtensions;
        Assert.Equal(list.Length, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(list, e => Assert.Matches("^\\.[a-z0-9]+$", e));
    }

    [Fact]
    public void TheListIsNeverClearedSoSettingItEveryRunIsIdempotent()
    {
        // "--clear-never-compress" in the same command wipes the list in Kopia 0.23; adding is de-duplicated by Kopia.
        Assert.DoesNotContain(KopiaEngine.FolderCompressionArgs(), a => a.StartsWith("--clear-never-compress", StringComparison.Ordinal));
    }
}
