using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Enrollment;
using CloudOrc.Backup.Agent.Platform;
using CloudOrc.Backup.Agent.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.Backup.Agent.Cli;

/// <summary>
/// The command-line side of the agent binary. Every command runs BEFORE the service host is built,
/// exits with a meaningful code (installers and scripts check it) and never prints a secret.
/// </summary>
public static class CommandLine
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitNotElevated = 3;
    public const int ExitServiceUnavailable = 4;

    private static readonly string[] Commands =
        ["activate", "enroll", "status", "diagnostics", "set-activation", "restart-service", "version", "--version", "-v", "help", "--help", "-h"];

    public static bool IsCliCommand(string first) => Commands.Contains(first, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        switch (command)
        {
            case "version" or "--version" or "-v":
                Console.WriteLine("CloudOrc Backup Agent");
                Console.WriteLine($"Version: {AgentVersionInfo.GetVersion()}");
                Console.WriteLine($"OS: {AgentIdentityInfo.OsDescription} ({AgentPlatform.OsName}, {AgentIdentityInfo.Architecture})");
                return ExitOk;

            case "help" or "--help" or "-h":
                PrintHelp();
                return ExitOk;
        }

        var (agentOptions, activationOptions) = LoadOptions();
        DeviceKeyStore.DefaultKeyDirectory = Path.Combine(agentOptions.DataDirectory, "keys");

        switch (command)
        {
            case "activate":
            {
                if (!RequireElevation())
                {
                    return ExitNotElevated;
                }

                var code = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : Arg(args, "--code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    Console.Error.WriteLine("Usage: cloudorc-backup activate <CODE> [--api-base https://<control-plane>/]");
                    return ExitUsage;
                }

                var enrollArgs = new List<string> { "enroll", "--code", code.Trim() };
                if (Arg(args, "--api-base") is { } apiBase)
                {
                    enrollArgs.AddRange(["--api-base", apiBase]);
                }

                var result = await EnrollAsync(enrollArgs.ToArray(), agentOptions, activationOptions).ConfigureAwait(false);
                if (result == ExitOk && !args.Contains("--no-restart", StringComparer.OrdinalIgnoreCase))
                {
                    RestartAfterActivation();
                }

                return result;
            }

            case "enroll":
                if (!RequireElevation())
                {
                    return ExitNotElevated;
                }

                return await EnrollAsync(args, agentOptions, activationOptions).ConfigureAwait(false);

            case "set-activation":
                if (!RequireElevation())
                {
                    return ExitNotElevated;
                }

                return ActivationSettingsFile.RunCommandLine(args, agentOptions.DataDirectory);

            case "restart-service":
            {
                // Launched detached by the running service after a local activation (tray), or by hand.
                if (args.Contains("--delayed", StringComparer.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }

                var error = ServiceControl.RestartAgent();
                if (error is not null)
                {
                    Console.Error.WriteLine($"Could not restart the Backup Agent service: {error}");
                    return ExitServiceUnavailable;
                }

                Console.WriteLine("Backup Agent service restarted.");
                return ExitOk;
            }

            case "status":
                return await StatusAsync(asJson: args.Contains("--json", StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);

            case "diagnostics":
                return Diagnostics(agentOptions);
        }

        PrintHelp();
        return ExitUsage;
    }

    private static async Task<int> EnrollAsync(string[] args, ControlAgentOptions agentOptions, ActivationOptions activationOptions)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        return await EnrollmentCommandLine.RunAsync(args, agentOptions, activationOptions, loggerFactory).ConfigureAwait(false);
    }

    private static void RestartAfterActivation()
    {
        var state = ServiceControl.QueryAgent();
        if (state is ServiceControl.State.NotInstalled or ServiceControl.State.Unknown)
        {
            Console.WriteLine("Activated. Start the CloudOrc Backup Agent service to connect.");
            return;
        }

        var error = ServiceControl.RestartAgent();
        Console.WriteLine(error is null
            ? "Activated. The Backup Agent is connecting to CloudOrc - this machine will appear in the portal shortly."
            : $"Activated, but the service could not be restarted ({error}). Restart it to connect.");
    }

    private static async Task<int> StatusAsync(bool asJson)
    {
        LocalStatusResponse? response = null;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", LocalStatusProtocol.PipeName, PipeDirection.InOut,
                OperatingSystem.IsWindows() ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var request = JsonSerializer.Serialize(new LocalStatusRequest { Action = LocalStatusProtocol.ActionStatus }, ProtocolJson.Options) + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is not null)
            {
                response = JsonSerializer.Deserialize<LocalStatusResponse>(line, ProtocolJson.Options);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            var hint = ex is UnauthorizedAccessException || (!OperatingSystem.IsWindows() && !AgentPlatform.IsElevated())
                ? " Run it with sudo (the agent only talks to root)."
                : "";
            Console.Error.WriteLine($"The Backup Agent service is not reachable ({ServiceControl.QueryAgent()}).{hint}");
            return ExitServiceUnavailable;
        }

        if (response is null)
        {
            Console.Error.WriteLine("The Backup Agent service did not answer.");
            return ExitServiceUnavailable;
        }

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, ProtocolJson.Options));
            return ExitOk;
        }

        Console.WriteLine($"State:          {response.State}");
        Console.WriteLine($"Device:         {response.DeviceName}");
        Console.WriteLine($"Agent version:  {response.AgentVersion}");
        Console.WriteLine($"Last contact:   {response.LastContactAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
        if (!string.IsNullOrWhiteSpace(response.ProtectionSummary))
        {
            Console.WriteLine($"Protection:     {response.ProtectionSummary}");
        }

        if (!string.IsNullOrWhiteSpace(response.Problem))
        {
            Console.WriteLine($"Problem:        {response.Problem}");
        }

        if (!string.IsNullOrWhiteSpace(response.RetryStatus))
        {
            Console.WriteLine($"Retry:          {response.RetryStatus}");
        }

        Console.WriteLine($"Diagnostics ID: {response.DiagnosticsId}");
        if (!string.IsNullOrWhiteSpace(response.PortalUrl))
        {
            Console.WriteLine($"Portal:         {response.PortalUrl}");
        }

        return ExitOk;
    }

    private static int Diagnostics(ControlAgentOptions agentOptions)
    {
        var store = new EnrolledStateStore(Options.Create(agentOptions), NullLogger<EnrolledStateStore>.Instance);
        var enrolled = store.TryLoad();
        var installRoot = AgentPlatform.InstallRootFromBaseDirectory(AppContext.BaseDirectory);
        var engines = Path.Combine(installRoot, "engines");

        Console.WriteLine("CloudOrc Backup Agent diagnostics (contains no secrets)");
        Console.WriteLine($"  Version:          {AgentVersionInfo.GetVersion()}");
        Console.WriteLine($"  OS:               {AgentIdentityInfo.OsDescription}");
        Console.WriteLine($"  OS family / arch: {AgentPlatform.OsName} / {AgentIdentityInfo.Architecture}");
        Console.WriteLine($"  Machine name:     {Environment.MachineName}");
        Console.WriteLine($"  CPU cores:        {Environment.ProcessorCount}");
        Console.WriteLine($"  Install root:     {installRoot}");
        Console.WriteLine($"  Data directory:   {agentOptions.DataDirectory}");
        Console.WriteLine($"  Service:          {AgentPlatform.ServiceDisplayName} = {ServiceControl.QueryAgent()}");
        Console.WriteLine($"  Activated:        {(enrolled is null ? "no" : $"yes ({enrolled.EnrollmentMethod}, {enrolled.EnrolledAtUtc:yyyy-MM-dd})")}");
        if (enrolled is not null)
        {
            Console.WriteLine($"  Agent ID:         {enrolled.AgentId}");
            Console.WriteLine($"  Backend:          {enrolled.BackendUrl}");
            Console.WriteLine($"  API base:         {enrolled.ApiBaseUrl ?? "-"}");
        }

        Console.WriteLine($"  Engines manifest: {(File.Exists(Path.Combine(engines, "engines.manifest.json")) ? "present" : "MISSING")}");
        Console.WriteLine($"  Worker:           {(File.Exists(Path.Combine(AppContext.BaseDirectory, "cloudorc-backup-worker" + AgentPlatform.ExeSuffix)) ? "present" : "MISSING")}");
        Console.WriteLine($"  Watchdog:         {(File.Exists(Path.Combine(AppContext.BaseDirectory, "cloudorc-backup-watchdog" + AgentPlatform.ExeSuffix)) ? "present" : "MISSING")}");
        Console.WriteLine($"  Elevated:         {AgentPlatform.IsElevated()}");
        Console.WriteLine($"  Logs:             {agentOptions.LogsDirectory}");
        return ExitOk;
    }

    private static (ControlAgentOptions Agent, ActivationOptions Activation) LoadOptions()
    {
        var baseConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var agent = baseConfig.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>() ?? new ControlAgentOptions();
        var config = new ConfigurationBuilder()
            .AddConfiguration(baseConfig)
            .AddJsonFile(ActivationSettingsFile.PathFor(agent.DataDirectory), optional: true)
            .Build();
        var activation = config.GetSection(ActivationOptions.SectionName).Get<ActivationOptions>() ?? new ActivationOptions();
        return (agent, activation);
    }

    private static bool RequireElevation()
    {
        if (AgentPlatform.IsElevated())
        {
            return true;
        }

        Console.Error.WriteLine(OperatingSystem.IsWindows()
            ? "Run this command from an elevated (Administrator) prompt."
            : "Run this command with sudo (the Backup Agent stores its identity as root).");
        return false;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("CloudOrc Backup Agent");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  cloudorc-backup activate <CODE> [--api-base https://<control-plane>/]   Activate this machine (code from the CloudOrc portal)");
        Console.WriteLine("  cloudorc-backup status [--json]                                       Connection and protection status");
        Console.WriteLine("  cloudorc-backup diagnostics                                           Support information (no secrets)");
        Console.WriteLine("  cloudorc-backup restart-service                                       Restart the agent service");
        Console.WriteLine("  cloudorc-backup set-activation --api-base https://... [--portal-url ...]");
        Console.WriteLine("  cloudorc-backup enroll --token ENR-...                                Zero-touch enrollment (CloudOrc VPS)");
        Console.WriteLine("  cloudorc-backup --version");
        Console.WriteLine();
        Console.WriteLine("Backups, folders, schedules and restores are managed in the CloudOrc portal, not on this machine.");
    }
}
