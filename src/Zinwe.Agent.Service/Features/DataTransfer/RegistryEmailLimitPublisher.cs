using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Features.DataTransfer;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Features.DataTransfer;

/// <summary>
/// Writes the email-attachment limit to <c>HKLM\SOFTWARE\Zinwe\SendGuard\MaxAttachmentBytes</c> (a DWORD in bytes),
/// which the classic-Outlook SendGuard add-in reads. SAFETY: the same rule as every enforcer — only writes the
/// machine registry when running as LocalSystem with Agent:Enforcement:DryRun=false; otherwise it just logs.
/// </summary>
internal sealed class RegistryEmailLimitPublisher(
    IRegistryWriter registry, bool canWrite, ILogger<RegistryEmailLimitPublisher> logger) : IEmailLimitPublisher
{
    private const string KeyPath = @"SOFTWARE\Zinwe\SendGuard";
    private const string ValueName = "MaxAttachmentBytes";

    public void Publish(long maxAttachmentBytes)
    {
        var value = (int)Math.Clamp(maxAttachmentBytes, 0, int.MaxValue);
        if (!canWrite)
        {
            logger.LogInformation(
                "[dry run] would set HKLM\\{Key}\\{Name}={Value} for the Outlook send-guard add-in", KeyPath, ValueName, value);
            return;
        }
        try
        {
            registry.SetDword(KeyPath, ValueName, value);
            logger.LogInformation("Outlook send-guard email limit published: {Value} bytes", value);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not publish the Outlook send-guard email limit");
        }
    }
}
