using System.Globalization;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.DataTransfer;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Features.DataTransfer;

/// <summary>
/// The enforcement response to a USB copy over the limit: set USB to read-only (real only as the LocalSystem
/// service; dry-run otherwise), raise a critical event for the owner's feed, and drop a note in the member's
/// widget. Never throws (the logger wraps it too). It can't undo the triggering copy, only stop further writes.
/// </summary>
internal sealed class UsbLockdownResponder(
    IUsbEnforcer enforcer,
    EventQueue events,
    NotificationWriter notifications,
    ILogger<UsbLockdownResponder> logger) : ITransferLimitResponder
{
    public void OnUsbLimitExceeded(string destination, long totalBytes, long limitBytes, DateTimeOffset at)
    {
        // 1) Lock USB to read-only on this PC (dry-run unless the LocalSystem service with enforcement on).
        enforcer.Apply(UsbMode.ReadOnly);

        // 2) Owner-facing critical alert in the activity feed.
        events.Enqueue(DataTransferEvents.UsbLockedDown(destination, totalBytes, limitBytes, enforcer.IsDryRun, at));

        // 3) Tell the member on their widget why USB just went read-only. Negative id never collides with a
        //    server message id; each lockdown is a distinct note.
        var id = -DateTimeOffset.UtcNow.Ticks;
        var body = $"A transfer over the {DataTransferEvents.HumanSize(limitBytes)} limit was detected, so copying "
            + "to USB drives has been paused on this PC. Ask your admin to restore it.";
        notifications.Merge([new AgentMessage(id, "USB set to read-only", body, EventSeverity.Critical, at)]);

        logger.LogWarning(
            "USB over-limit lockdown: {Bytes} bytes to {Dest} > {Limit}; USB set to read-only (dryRun={DryRun})",
            totalBytes.ToString(CultureInfo.InvariantCulture), destination,
            limitBytes.ToString(CultureInfo.InvariantCulture), enforcer.IsDryRun);
    }
}
