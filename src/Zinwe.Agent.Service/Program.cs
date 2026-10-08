using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;
using Zinwe.Agent.Service;
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
builder.Services.AddSingleton<AgentStateHolder>();
builder.Services.AddSingleton<EnrollmentKeySource>();

// USB module. SAFETY: real registry enforcement only as LocalSystem with Agent:Enforcement:DryRun=false.
builder.Services.AddSingleton<IUsbEnforcer>(sp => UsbEnforcerSelector.Select(
    UsbEnforcerSelector.IsRunningAsSystem(),
    sp.GetRequiredService<IOptions<AgentOptions>>().Value.Enforcement.DryRun,
    () => new RegistryUsbEnforcer(new HklmRegistryWriter(), sp.GetRequiredService<ILogger<RegistryUsbEnforcer>>()),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<UsbProtection>();
builder.Services.AddSingleton<IUsbDeviceWatcher, WmiUsbDeviceWatcher>();
builder.Services.AddSingleton<PolicySync>();
builder.Services.AddSingleton<EventUploadPump>();

builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<PipeServer>();
builder.Services.AddHostedService<EventUploader>();
builder.Services.AddHostedService<UsbWatcherService>();

builder.Build().Run();
