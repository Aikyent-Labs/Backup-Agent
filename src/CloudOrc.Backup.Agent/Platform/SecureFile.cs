using System.Security.Cryptography;
using System.Text;

namespace CloudOrc.Backup.Agent.Platform;

/// <summary>
/// Writes secrets (credential, device key, certificate) so only the service account can read them.
/// <list type="bullet">
/// <item>Windows: <see cref="ProtectSecret"/> encrypts with DPAPI (machine scope); the data directory
/// ACL is SYSTEM + Administrators only.</item>
/// <item>Linux / macOS: the file is created with mode 0600 inside a 0700 directory owned by root,
/// written to a temp file first and renamed, so a crash never leaves a half-written secret.</item>
/// </list>
/// </summary>
public static class SecureFile
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudOrc.Backup.Agent.Secret.v1");

    public static void WriteAllText(string path, string content) => WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

    public static void WriteAllBytes(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        EnsurePrivateDirectory(directory);
        var temp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(temp, content);
        }
        else
        {
            using (var stream = new FileStream(temp, new FileStreamOptions
                   {
                       Mode = FileMode.Create,
                       Access = FileAccess.Write,
                       Share = FileShare.None,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   }))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Creates the directory; on Unix restricts it to the owner (0700).</summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not the owner (e.g. a developer run): leave the existing mode.
        }
    }

    /// <summary>Windows: DPAPI machine-scope encryption. Unix: unchanged (protected by file mode).</summary>
    public static byte[] ProtectSecret(byte[] plain) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine) : plain;

    public static byte[] UnprotectSecret(byte[] stored) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.LocalMachine) : stored;
}
