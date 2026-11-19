using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.Backup.Agent.Enrollment;

/// <summary>
/// Persists/loads the agent's <see cref="EnrolledAgentState"/> (agent id, backend URL, renewable
/// credential) in the data directory.
/// <list type="bullet">
/// <item>Windows: <c>enrollment.dat</c>, encrypted with DPAPI at LocalMachine scope (the installer
/// may enroll as an elevated admin while the service runs as LocalSystem - only machine-scoped keys
/// work across that boundary). The data directory ACL is SYSTEM + Administrators.</item>
/// <item>Linux / macOS: <c>enrollment.json</c>, mode 0600 in a 0700 root-owned directory.</item>
/// </list>
/// Writes go to a temp file and are renamed into place, so a crash never leaves a half-written file.
/// </summary>
public sealed class EnrolledStateStore(IOptions<ControlAgentOptions> options, ILogger<EnrolledStateStore> logger)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudOrc.Backup.Agent.Enrollment.v1");
    private readonly ControlAgentOptions _options = options.Value;

    private string FilePath => Path.Combine(_options.DataDirectory, OperatingSystem.IsWindows() ? "enrollment.dat" : "enrollment.json");

    public bool Exists() => File.Exists(FilePath);

    public EnrolledAgentState? TryLoad()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var stored = File.ReadAllBytes(FilePath);
            var jsonBytes = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.LocalMachine)
                : stored;
            return JsonSerializer.Deserialize<EnrolledAgentState>(jsonBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not read the persisted enrollment state at {Path}. Treating this agent as not enrolled.", FilePath);
            return null;
        }
    }

    public void Save(EnrolledAgentState state)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_options.DataDirectory);
            var protectedBytes = ProtectedData.Protect(jsonBytes, Entropy, DataProtectionScope.LocalMachine);
            var tempPath = FilePath + ".tmp";
            File.WriteAllBytes(tempPath, protectedBytes);
            File.Move(tempPath, FilePath, overwrite: true);
            return;
        }

        SecureFile.WriteAllBytes(FilePath, jsonBytes);
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
