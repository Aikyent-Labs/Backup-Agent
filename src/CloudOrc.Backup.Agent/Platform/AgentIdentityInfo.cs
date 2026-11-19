using System.Runtime.InteropServices;

namespace CloudOrc.Backup.Agent.Platform;

/// <summary>What this agent tells the backend about itself (HELLO + activation).</summary>
public static class AgentIdentityInfo
{
    /// <summary>Distinguishes this standalone Backup Agent from the CloudOrc Server Agent on the same machine.</summary>
    public const string AgentKind = "backup";

    public static string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant()
    };

    public static string OsDescription => RuntimeInformation.OSDescription.Trim();
}
