using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Metrics;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Core.Usb;
using Zinwe.Agent.Service;
using Zinwe.Agent.Service.Features;
using Zinwe.Agent.Service.Usb;

// Content root = the exe's folder, so appsettings*.json load no matter what the working directory is
// (the SCM starts services in System32).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Config priority (low → high): appsettings*.json, HKLM\SOFTWARE\Zinwe\Agent (MSI), ZINWE_Agent__* env vars.
builder.Configuration.AddInMemoryCollection(MachineSettings.ReadConfigOverrides());
builder.Configuration.AddEnvironmentVariables("ZINWE_");

// No-op when started from a console (`dotnet run`); under the SCM this wires service lifetime + Event Log.
builder.Services.AddWindowsService(o => o.ServiceName = AgentInfo.ServiceName);

builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.Section))
    .Validate(o => Uri.TryCreate(o.BackendBaseUrl, UriKind.Absolute, out _), "Agent:BackendBaseUrl must be an absolute URL.")
    .Validate(o => o.HeartbeatSeconds is >= 15 and <= 3600, "Agent:HeartbeatSeconds must be 15..3600.")
    .ValidateOnStart();

builder.Services.AddHttpClient<BackendClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    http.BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(30);
});

static string DataDirectory(IServiceProvider sp) =>
    Environment.ExpandEnvironmentVariables(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory);

builder.Services.AddSingleton(sp => new IdentityStore(DataDirectory(sp)));
builder.Services.AddSingleton(sp => new PolicyStore(DataDirectory(sp)));
builder.Services.AddSingleton(sp => new EventQueue(DataDirectory(sp)));
builder.Services.AddSingleton(sp => new MetricsStore(DataDirectory(sp)));
builder.Services.AddSingleton<AgentStateHolder>();
builder.Services.AddSingleton(sp => new MemberCredentialSource(
    sp.GetRequiredService<IOptionsMonitor<AgentOptions>>(),
    sp.GetRequiredService<ILogger<MemberCredentialSource>>(),
    DataDirectory(sp)));

// USB module. SAFETY: real registry enforcement only as LocalSystem with Agent:Enforcement:DryRun=false.
builder.Services.AddSingleton<IUsbEnforcer>(sp => UsbEnforcerSelector.Select(
    UsbEnforcerSelector.IsRunningAsSystem(),
    sp.GetRequiredService<IOptions<AgentOptions>>().Value.Enforcement.DryRun,
    () => new RegistryUsbEnforcer(new HklmRegistryWriter(), sp.GetRequiredService<ILogger<RegistryUsbEnforcer>>()),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<UsbProtection>();
builder.Services.AddSingleton<IUsbDeviceWatcher, WmiUsbDeviceWatcher>();
builder.Services.AddSingleton<PolicySync>();
// Owner → member messages: NotificationWriter targets the per-user %LOCALAPPDATA%\Zinwe (the widget's folder),
// NOT the agent data dir, so the widget sees them.
builder.Services.AddSingleton(_ => new NotificationWriter());
// The member's outbound replies (widget writes, agent drains) — shared per-user file.
builder.Services.AddSingleton(_ => new MessageOutbox());
builder.Services.AddSingleton<MessageSync>();
// Usage metrics upload + the member's work status (read from the widget's per-user file).
builder.Services.AddSingleton<MetricsUploadPump>();
builder.Services.AddSingleton(_ => new WorkStatusReader());
builder.Services.AddSingleton<EventUploadPump>();

// Self-contained feature modules (internet usage, data-transfer, remote-software...) register and run themselves.
builder.AddFeatureModules();

builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<PipeServer>();
builder.Services.AddHostedService<EventUploader>();
builder.Services.AddHostedService<UsbWatcherService>();

builder.Build().Run();
