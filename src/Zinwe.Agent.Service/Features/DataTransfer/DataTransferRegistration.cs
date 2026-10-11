using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Features;
using Zinwe.Agent.Core.Features.DataTransfer;
using Zinwe.Agent.Core.Usb;
using Zinwe.Agent.Service.Usb;

namespace Zinwe.Agent.Service.Features.DataTransfer;

/// <summary>
/// DI wiring for the "data_transfer" feature. Auto-discovered by <see cref="FeatureModuleRegistration"/>; no edits
/// to Program.cs. Reads <c>Agent:Features:data_transfer</c> (all defaults live in <see cref="DataTransferOptions"/>),
/// registers the OS-facing collectors behind their Core interfaces, and exposes the module to the feature host.
/// </summary>
public sealed class DataTransferRegistration : IFeatureRegistration
{
    public void Register(IServiceCollection services, IConfiguration config)
    {
        var defaults = config.GetSection(DataTransferOptions.Section).Get<DataTransferOptions>() ?? new DataTransferOptions();

        // Scoped: a new policy rebuilds the scope with options mapped from its data_transfer block (or defaults).
        services.AddScoped(sp => DataTransferPolicyMap.FromPolicy(defaults, sp.GetRequiredService<FeaturePolicyState>().Current?.DataTransfer));

        services.AddScoped<IDriveEnumerator, SystemDriveEnumerator>();
        services.AddScoped<IFolderWatcherFactory, FileSystemFolderWatcherFactory>();
        services.AddScoped<IOutlookAttachmentWatcher, OutlookAttachmentWatcher>();
        // Enforcement response to an over-limit USB copy (reuses the singleton USB enforcer + notification writer).
        services.AddScoped<ITransferLimitResponder, UsbLockdownResponder>();

        // Mirror the email limit to the registry the classic-Outlook SendGuard add-in reads. Machine-registry
        // writes only happen as the LocalSystem service with enforcement on (same safety rule as USB/web).
        var dryRun = config.GetValue<bool>("Agent:Enforcement:DryRun");
        services.AddScoped<IEmailLimitPublisher>(sp => new RegistryEmailLimitPublisher(
            new HklmRegistryWriter(),
            UsbEnforcerSelector.IsRunningAsSystem() && !dryRun,
            sp.GetRequiredService<ILogger<RegistryEmailLimitPublisher>>()));

        services.AddScoped<DataTransferLogger>();
        services.AddScoped<DataTransferModule>();

        // Expose the module to the feature supervisor (it enumerates IFeatureModule per scope and runs Start/Stop).
        services.AddScoped<IFeatureModule>(sp => sp.GetRequiredService<DataTransferModule>());
    }
}
