using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.Backup.Watchdog.Configuration;
using CloudOrc.Backup.Watchdog.ControlAgentManagement;
using CloudOrc.Backup.Watchdog.Recovery;
using CloudOrc.Backup.Watchdog.Services;
using Serilog;

// CloudOrc Backup Watchdog: keeps the Backup Agent service healthy (Windows / Linux / macOS).
if (args.Length > 0 && (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("CloudOrc Backup Watchdog");
    Console.WriteLine($"Version: {AgentVersionInfo.GetVersion()}");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WatchdogOptions>(builder.Configuration.GetSection(WatchdogOptions.SectionName));

var watchdogOptions =
    builder.Configuration.GetSection(WatchdogOptions.SectionName).Get<WatchdogOptions>()
    ?? new WatchdogOptions();

Directory.CreateDirectory(watchdogOptions.DataDirectory);
Directory.CreateDirectory(watchdogOptions.LogsDirectory);

builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(watchdogOptions.LogsDirectory, "backup-watchdog-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Windows Service, systemd (Type=notify) or launchd/console - chosen automatically at runtime.
builder.Services.AddWindowsService(options => options.ServiceName = "CloudOrcBackupWatchdog");
builder.Services.AddSystemd();

builder.Services.AddSingleton(watchdogOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ControlAgentServiceManager>();
builder.Services.AddSingleton<ControlAgentHealthClient>();
builder.Services.AddSingleton<WatchdogResourceMonitors>();
builder.Services.AddSingleton<ConsecutiveFailureTracker>();
builder.Services.AddSingleton<RecoveryRateLimiter>();
builder.Services.AddSingleton<WatchdogEventLog>();

builder.Services.AddHostedService<WatchdogMonitorService>();

var host = builder.Build();

try
{
    Log.Information(
        "CloudOrc Backup Watchdog starting. Monitoring service '{ServiceName}'.",
        watchdogOptions.ControlAgentServiceName);
    host.Run();
}
finally
{
    Log.CloseAndFlush();
}

return 0;
