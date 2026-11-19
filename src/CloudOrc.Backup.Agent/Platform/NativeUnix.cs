using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CloudOrc.Backup.Agent.Platform;

/// <summary>The few libc calls the agent needs on Linux and macOS. Every call is guarded by the caller.</summary>
internal static partial class NativeUnix
{
    [LibraryImport("libc", EntryPoint = "geteuid")]
    [UnsupportedOSPlatform("windows")]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "getloadavg")]
    [UnsupportedOSPlatform("windows")]
    private static partial int getloadavg([Out] double[] loadavg, int nelem);

    public static uint GetEffectiveUserId()
    {
        if (OperatingSystem.IsWindows())
        {
            return uint.MaxValue;
        }

        try
        {
            return geteuid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return uint.MaxValue;
        }
    }

    /// <summary>1-minute load average, or null when unavailable.</summary>
    public static double? LoadAverage1()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var values = new double[3];
            return getloadavg(values, 3) > 0 ? values[0] : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
