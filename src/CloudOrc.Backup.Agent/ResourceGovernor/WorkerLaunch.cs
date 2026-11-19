using System.Diagnostics;

namespace CloudOrc.Backup.Agent.ResourceGovernor;

/// <summary>
/// Builds the command line that starts the backup worker at LOW priority from its very first
/// instruction, so the worker, every thread it creates and every engine it spawns (kopia,
/// pg_dump) inherit that priority. On Linux, nice and ionice are per-thread attributes, so
/// changing them after start would miss threads that already exist; starting through the
/// standard OS tools avoids that race entirely.
/// <list type="bullet">
/// <item>Linux: <c>nice -n 10|19 ionice -c 2 -n 7 | -c 3 worker ...</c> (coreutils / util-linux)</item>
/// <item>macOS: <c>taskpolicy -b worker ...</c> (Darwin background policy: CPU, disk and network throttled)</item>
/// <item>Windows: the worker starts directly; the Job Object + BELOW_NORMAL/IDLE priority class apply.</item>
/// </list>
/// Missing tools are skipped (logged by the caller): the cgroup / Job Object hard caps still hold.
/// </summary>
public static class WorkerLaunch
{
    public static string[] NicePaths { get; set; } = ["/usr/bin/nice", "/bin/nice"];
    public static string[] IonicePaths { get; set; } = ["/usr/bin/ionice", "/bin/ionice"];
    public static string[] TaskpolicyPaths { get; set; } = ["/usr/sbin/taskpolicy"];

    public sealed record LaunchPlan(string FileName, IReadOnlyList<string> PrefixArguments, string Description);

    public static LaunchPlan Build(string workerExecutable, JobPriority priority)
    {
        if (OperatingSystem.IsWindows() || priority == JobPriority.Normal)
        {
            return new LaunchPlan(workerExecutable, [], "direct");
        }

        if (OperatingSystem.IsMacOS())
        {
            var taskpolicy = FirstExisting(TaskpolicyPaths);
            return taskpolicy is null
                ? new LaunchPlan(workerExecutable, [], "direct (taskpolicy not found)")
                : new LaunchPlan(taskpolicy, ["-b", workerExecutable], "taskpolicy -b (background)");
        }

        var nice = FirstExisting(NicePaths);
        var ionice = FirstExisting(IonicePaths);
        var niceLevel = priority == JobPriority.Idle ? "19" : "10";
        var args = new List<string>();
        var description = new List<string>();
        string? fileName = null;

        if (nice is not null)
        {
            fileName = nice;
            args.AddRange(["-n", niceLevel]);
            description.Add($"nice {niceLevel}");
        }

        if (ionice is not null)
        {
            // Idle IO class (3) only for the "Idle" profile: best-effort level 7 (lowest) otherwise,
            // so a busy server still lets the backup finish instead of starving it forever.
            var ioArgs = priority == JobPriority.Idle ? new[] { "-c", "3" } : ["-c", "2", "-n", "7"];
            if (fileName is null)
            {
                fileName = ionice;
            }
            else
            {
                args.Add(ionice);
            }

            args.AddRange(ioArgs);
            description.Add(priority == JobPriority.Idle ? "ionice idle" : "ionice best-effort 7");
        }

        if (fileName is null)
        {
            return new LaunchPlan(workerExecutable, [], "direct (nice/ionice not found)");
        }

        args.Add(workerExecutable);
        return new LaunchPlan(fileName, args, string.Join(", ", description));
    }

    public static void Apply(ProcessStartInfo startInfo, LaunchPlan plan)
    {
        startInfo.FileName = plan.FileName;
        for (var i = plan.PrefixArguments.Count - 1; i >= 0; i--)
        {
            startInfo.ArgumentList.Insert(0, plan.PrefixArguments[i]);
        }
    }

    private static string? FirstExisting(IEnumerable<string> paths) => paths.FirstOrDefault(File.Exists);
}
