using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.Backup.Agent.Backend;
using CloudOrc.Backup.Agent.Backup;
using CloudOrc.Backup.Agent.Cli;
using CloudOrc.Backup.Agent.Commands;
using CloudOrc.Backup.Agent.Configuration;
using CloudOrc.Backup.Agent.Discovery;
using CloudOrc.Backup.Agent.DurableQueue;
using CloudOrc.Backup.Agent.Enrollment;
using CloudOrc.Backup.Agent.Health;
using CloudOrc.Backup.Agent.Identity;
using CloudOrc.Backup.Agent.LocalStatus;
using CloudOrc.Backup.Agent.Platform;
using CloudOrc.Backup.Agent.ResourceGovernor;
using CloudOrc.Backup.Agent.Services;
using CloudOrc.Backup.Agent.Startup;
using CloudOrc.Backup.Agent.Telemetry;
using CloudOrc.Backup.Agent.Transport;
using CloudOrc.Backup.Agent.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;

// CloudOrc Backup Agent - one binary for the service and the command line, on Windows, Linux and macOS.
//
//   cloudorc-backup                      run as the service (Windows Service / systemd / launchd)
//   cloudorc-backup activate CODE        activate this machine with a code from the CloudOrc portal
//   cloudorc-backup enroll --token ENR-  zero-touch enrollment (CloudOrc VPS, used by the Server Agent)
//   cloudorc-backup status               connection + protection status (asks the running service)
//   cloudorc-backup diagnostics          support information (no secrets)
//   cloudorc-backup set-activation ...   where activation codes are redeemed (written by installers)
//   cloudorc-backup restart-service      restart the agent service
//   cloudorc-backup --version
if (args.Length > 0 && CommandLine.IsCliCommand(args[0]))
{
    return await CommandLine.RunAsync(args).ConfigureAwait(false);
}

var builder = Host.CreateApplicationBuilder(args);
{
    var bootstrapOptions = builder.Configuration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>() ?? new ControlAgentOptions();
    builder.Configuration.AddJsonFile(ActivationSettingsFile.PathFor(bootstrapOptions.DataDirectory), optional: true, reloadOnChange: false);
}

builder.Services.Configure<ControlAgentOptions>(builder.Configuration.GetSection(ControlAgentOptions.SectionName));
builder.Services.Configure<AgentIdentityOptions>(builder.Configuration.GetSection(AgentIdentityOptions.SectionName));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
builder.Services.Configure<ActivationOptions>(builder.Configuration.GetSection(ActivationOptions.SectionName));
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));

var backupOptions = builder.Configuration.GetSection(BackupOptions.SectionName).Get<BackupOptions>() ?? new BackupOptions();
var agentOptions = builder.Configuration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>() ?? new ControlAgentOptions();
var backendOptions = builder.Configuration.GetSection(BackendConnectionOptions.SectionName).Get<BackendConnectionOptions>() ?? new BackendConnectionOptions();

// Directories must exist before Serilog's file sink writes into logs/.
DirectoryBootstrapper.EnsureDirectories(agentOptions);
DeviceKeyStore.DefaultKeyDirectory = Path.Combine(agentOptions.DataDirectory, "keys");

// Windows: restrict the data directory to SYSTEM + Administrators in the background (the service
// must report "started" within the SCM timeout). Linux/macOS: done above (root, 0700).
if (OperatingSystem.IsWindows() && agentOptions.HardenDataDirectoryAcl && !DataDirectoryAcl.IsHardened(agentOptions.DataDirectory))
{
    var dataDirectory = agentOptions.DataDirectory;
    _ = Task.Run(() =>
    {
        if (OperatingSystem.IsWindows() && DataDirectoryAcl.Harden(dataDirectory) is { } aclError)
        {
            Console.Error.WriteLine($"WARNING: could not restrict {dataDirectory} to SYSTEM and Administrators: {aclError}");
        }
    });
}

// Once enrolled, the backend-issued URL always wins over appsettings.json.
var enrolledState = new EnrolledStateStore(Options.Create(agentOptions), NullLogger<EnrolledStateStore>.Instance).TryLoad();
var backendUrlIsFromEnrollment = enrolledState is not null;
if (enrolledState is not null)
{
    backendOptions.Enabled = true;
    backendOptions.Url = enrolledState.BackendUrl;
}

builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(agentOptions.LogsDirectory, "backup-agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

if (backendOptions.Enabled)
{
    var isInsecureWs = backendOptions.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
    if (isInsecureWs && !backendOptions.DevelopmentAllowInsecureWs && !backendUrlIsFromEnrollment)
    {
        throw new InvalidOperationException(
            $"BackendConnection.Url ('{backendOptions.Url}') uses insecure ws:// but DevelopmentAllowInsecureWs is false. Refusing to start.");
    }

    if (string.IsNullOrWhiteSpace(backendOptions.Url))
    {
        throw new InvalidOperationException("BackendConnection.Enabled is true but BackendConnection.Url is empty.");
    }
}

// Service lifetime per OS: Windows Service, systemd (Type=notify) or launchd / console.
builder.Services.AddWindowsService(options => options.ServiceName = AgentPlatform.WindowsServiceName);
builder.Services.AddSystemd();

builder.Services.AddSingleton<IOptions<BackendConnectionOptions>>(Options.Create(backendOptions));
builder.Services.AddSingleton<EnrolledStateStore>();
builder.Services.AddSingleton<ControlAgentHealthState>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BackupPaths>();
builder.Services.AddSingleton<BackendLinkState>();

// ---- Durable local state: always available, even before activation.
builder.Services.AddSingleton(sp => new AgentStateDatabase(
    sp.GetRequiredService<BackupPaths>().StateDatabase,
    sp.GetRequiredService<ILogger<AgentStateDatabase>>()));
builder.Services.AddSingleton(sp => new CommandInboxStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddSingleton(sp => new OutboxStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddSingleton(sp => new BackupPolicyStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddSingleton(sp => new BackupJobStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddHostedService<DurableStateRetentionService>();

// ---- Agent core's own budget: measure, trim when idle, flag breaches.
builder.Services.AddSingleton<CoreBudgetMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CoreBudgetMonitor>());

// ---- Local status IPC (tray on Windows, `cloudorc-backup status` everywhere): works enrolled or not.
builder.Services.AddSingleton<ActivationService>();
builder.Services.AddHostedService<AgentStatusPipeServer>();

if (backendOptions.Enabled)
{
    builder.Services.AddSingleton<AgentIdentityProvider>();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<AgentIdentityProvider>().GetIdentity());
    builder.Services.AddSingleton<OutgoingMessageChannel>();
    builder.Services.AddSingleton<TelemetryCollector>();

    // ---- Transport: renewable credential, HTTPS control plane, device key, capability profile.
    builder.Services.AddSingleton<AgentCredentialProvider>();
    builder.Services.AddSingleton<BackendApiClient>();
    builder.Services.AddSingleton(_ => new DeviceKeyStore());
    builder.Services.AddSingleton<SessionRenewalService>();
    builder.Services.AddSingleton<ISessionRenewer>(sp => sp.GetRequiredService<SessionRenewalService>());
    builder.Services.AddSingleton<IAgentCapabilitiesProvider, AgentCapabilitiesProvider>();
    builder.Services.AddHostedService<CredentialRenewalScheduler>();
    builder.Services.AddSingleton<CertificateRotationService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CertificateRotationService>());
    builder.Services.AddSingleton<ITypedCommandHandler, CertificateRenewHandler>();

    // ---- Durable outbox delivery (at-least-once) + updater result reporting.
    builder.Services.AddSingleton<OutboxDispatcherService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcherService>());
    builder.Services.AddHostedService<UpdateResultReporter>();

    // ---- Typed commands: allow-listed, versioned, signed, idempotent via the durable inbox.
    builder.Services.AddSingleton(sp => new CommandSignatureVerifier(
        sp.GetRequiredService<EnrolledStateStore>().TryLoad()?.CommandSigningPublicKey,
        agentOptions.RequireSignedTypedCommands,
        sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton(sp => new TypedCommandDispatcher(
        sp.GetServices<ITypedCommandHandler>(),
        sp.GetRequiredService<CommandInboxStore>(),
        sp.GetRequiredService<OutgoingMessageChannel>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<TypedCommandDispatcher>>(),
        sp.GetRequiredService<CommandSignatureVerifier>()));
    builder.Services.AddSingleton<ITypedCommandRouter>(sp => sp.GetRequiredService<TypedCommandDispatcher>());
    builder.Services.AddSingleton<ITypedCommandHandler, AgentUpdateHandler>();
    builder.Services.AddSingleton<ITypedCommandHandler, CredentialRenewHandler>();

    // ---- Machine inventory ("analyse this machine") - sent after connect and on request.
    builder.Services.AddSingleton<MachineInventoryCollector>();
    builder.Services.AddSingleton<ITypedCommandHandler, InventoryRefreshHandler>();
    builder.Services.AddHostedService<WatchdogEventForwarder>();
    builder.Services.AddSingleton<InventoryPublisherService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InventoryPublisherService>());

    if (backupOptions.Enabled)
    {
        // ---- Backup capability: governor, isolated worker, scheduler, typed handlers.
        builder.Services.AddSingleton<IBackupControlPlane, BackupControlPlane>();
        builder.Services.AddSingleton<IResourceGovernor, PlatformResourceGovernor>();
        builder.Services.AddSingleton<IWorkerHost, ProcessWorkerHost>();
        builder.Services.AddSingleton(sp =>
        {
            var recorder = new JobEventRecorder(
                sp.GetRequiredService<BackupJobStore>(),
                sp.GetRequiredService<AgentIdentity>().AgentId,
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromSeconds(Math.Max(5, backupOptions.ProgressEventIntervalSeconds)));
            var dispatcher = sp.GetRequiredService<OutboxDispatcherService>();
            recorder.EventQueued += dispatcher.Wake;
            return recorder;
        });
        builder.Services.AddSingleton<BackupJobRunner>();
        builder.Services.AddSingleton<BackupJobCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupJobCoordinator>());
        builder.Services.AddSingleton<BackupSchedulerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupSchedulerService>());

        builder.Services.AddSingleton<ITypedCommandHandler, PolicyApplyHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, PolicyRemoveHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, BackupRunHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, JobCancelHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, VerifyHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, MaintenanceHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, RestoreExecuteHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, DiscoveryBrowseHandler>();
    }

    builder.Services.AddHostedService(sp => new BackendConnectionService(
        sp.GetRequiredService<IOptions<BackendConnectionOptions>>(),
        sp.GetRequiredService<AgentIdentity>(),
        sp.GetRequiredService<OutgoingMessageChannel>(),
        sp.GetRequiredService<ControlAgentHealthState>(),
        sp.GetRequiredService<ILogger<BackendConnectionService>>(),
        sp.GetRequiredService<AgentCredentialProvider>(),
        sp.GetRequiredService<ITypedCommandRouter>(),
        sp.GetRequiredService<IAgentCapabilitiesProvider>(),
        sp.GetRequiredService<ISessionRenewer>(),
        sp.GetRequiredService<BackendLinkState>(),
        sp.GetRequiredService<IOptions<ControlAgentOptions>>(),
        sp.GetRequiredService<EnrolledStateStore>()));
    builder.Services.AddHostedService<HeartbeatPublisherService>();
    builder.Services.AddHostedService<TelemetryPublisherService>();
}

builder.Services.AddHostedService<CoreLivenessService>();
builder.Services.AddHostedService<WatchdogGuardService>();
builder.Services.AddHostedService<HealthPipeServer>();

var host = builder.Build();

try
{
    Log.Information(
        "CloudOrc Backup Agent {Version} starting on {Os} ({Arch}). Data directory: {DataDirectory}. Activated={Enrolled}.",
        AgentVersionInfo.GetVersion(), AgentIdentityInfo.OsDescription, AgentIdentityInfo.Architecture,
        agentOptions.DataDirectory, backendUrlIsFromEnrollment);
    if (!backendUrlIsFromEnrollment)
    {
        Log.Information("This machine is not activated yet. Run: cloudorc-backup activate <CODE> (code from the CloudOrc portal).");
    }

    host.Run();
}
finally
{
    Log.CloseAndFlush();
}

return 0;
