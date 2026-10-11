using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Features;
using Zinwe.Agent.Core.Features.RemoteSoftware;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Features.RemoteSoftware;

/// <summary>
/// DI wiring for the "remote_software" feature (suspicious/remote-access software detection + dry-run blocking).
/// Auto-discovered by the feature host, so this adds nothing to Program.cs. SAFETY: the real process-killing
/// blocker is selected only when running as LocalSystem with Agent:Enforcement:DryRun=false — every other context
/// gets the dry-run blocker, which only logs. Detection sources are read-only and always live.
/// </summary>
public sealed class RemoteSoftwareRegistration : IFeatureRegistration
{
    public void Register(IServiceCollection services, IConfiguration config)
    {
        var defaults = new RemoteSoftwareOptions();
        config.GetSection(RemoteSoftwareOptions.Section).Bind(defaults);
        var dryRun = config.GetValue<bool>("Agent:Enforcement:DryRun");

        // Scoped: a new policy rebuilds the scope with options mapped from its remote_software block (or defaults).
        services.AddScoped(sp => RemoteSoftwarePolicyMap.FromPolicy(defaults, sp.GetRequiredService<FeaturePolicyState>().Current?.RemoteSoftware));

        services.AddScoped(sp => RemoteToolCatalog.FromOptions(sp.GetRequiredService<RemoteSoftwareOptions>()));

        // Read-only detection sources (OS-facing).
        services.AddScoped<IInstalledSoftwareSource, RegistryInstalledSoftwareSource>();
        services.AddScoped<IProcessSource, WindowsProcessSource>();
        services.AddScoped<IListeningPortSource, TcpListeningPortSource>();

        // Baseline so only NEW installs are reported — SINGLETON so it survives a policy-driven scope rebuild
        // (otherwise every reconfigure would re-seed and could re-report the whole installed set).
        services.AddSingleton<IInstallBaseline>(sp => new FileInstallBaseline(
            Environment.ExpandEnvironmentVariables(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory)));

        // SAFETY: real termination only as LocalSystem with dry run off; otherwise a logging-only blocker.
        services.AddScoped<IRemoteSoftwareBlocker>(sp => RemoteSoftwareBlockerSelector.Select(
            UsbEnforcerSelector.IsRunningAsSystem(),
            dryRun,
            () => new ProcessKillRemoteSoftwareBlocker(
                new ServiceProcessTerminator(), sp.GetRequiredService<ILogger<ProcessKillRemoteSoftwareBlocker>>()),
            sp.GetRequiredService<ILoggerFactory>()));

        // Member-facing widget notes (new install / remote-tool blocked). NotificationWriter is the singleton
        // from Program.cs that targets the widget's per-user folder.
        services.AddScoped<IMemberNotifier>(sp => new WidgetMemberNotifier(
            sp.GetRequiredService<NotificationWriter>()));

        services.AddScoped<InstallMonitor>();
        services.AddScoped<RemoteToolMonitor>();
        services.AddScoped<RemoteSoftwareModule>();
        services.AddScoped<IFeatureModule>(sp => sp.GetRequiredService<RemoteSoftwareModule>());
    }
}
