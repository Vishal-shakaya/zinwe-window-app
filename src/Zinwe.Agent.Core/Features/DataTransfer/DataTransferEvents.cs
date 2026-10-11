using System.Globalization;
using System.Text.Json.Nodes;
using Zinwe.Agent.Core.Events;

namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>Event types this feature emits. Dotted-lowercase; the backend words titles for the owner later.</summary>
public static class DataTransferEventTypes
{
    /// <summary>Files were copied to a removable (USB) drive.</summary>
    public const string UsbCopy = "data.usb_copy";

    /// <summary>Files were copied to a mapped network drive / UNC share.</summary>
    public const string ShareCopy = "data.share_copy";

    /// <summary>A mail with attachments was sent from classic desktop Outlook.</summary>
    public const string EmailAttachment = "data.email_attachment";

    /// <summary>A copy to USB went over the admin's limit, so USB was auto-set to read-only on this PC.</summary>
    public const string UsbLockedDown = "data.usb_locked_down";
}

/// <summary>
/// Builders for the data-transfer events. Payloads carry COUNTS, SIZES and the destination identifier only;
/// file names (copies) and the mail subject (email) are added just when <c>IncludeDetails</c> is on.
/// </summary>
public static class DataTransferEvents
{
    private const int MaxFieldLength = 200;

    /// <param name="names">Leaf file names, already filtered/capped by the caller; empty unless details are on.</param>
    public static AgentEvent UsbCopy(
        TransferTarget target, int fileCount, long totalBytes, IReadOnlyList<string> names, long warnBytes, DateTimeOffset? at = null)
    {
        var data = new JsonObject
        {
            ["summary"] = Summary(fileCount, totalBytes, "to", target.Key),
            ["channel"] = "usb",
            ["device"] = Clip(target.Key),
            ["file_count"] = fileCount,
            ["total_bytes"] = totalBytes,
        };
        AddNames(data, names);
        return AgentEvent.Create(DataTransferEventTypes.UsbCopy, Severity(totalBytes, warnBytes), data, at);
    }

    public static AgentEvent ShareCopy(
        TransferTarget target, int fileCount, long totalBytes, IReadOnlyList<string> names, long warnBytes, DateTimeOffset? at = null)
    {
        var data = new JsonObject
        {
            ["summary"] = Summary(fileCount, totalBytes, "to", target.Key),
            ["channel"] = "lan",
            ["share"] = Clip(target.Key),
            ["file_count"] = fileCount,
            ["total_bytes"] = totalBytes,
        };
        AddNames(data, names);
        return AgentEvent.Create(DataTransferEventTypes.ShareCopy, Severity(totalBytes, warnBytes), data, at);
    }

    /// <param name="subject">Mail subject, or null; included only when the caller passes it (details on).</param>
    public static AgentEvent EmailAttachment(
        int attachmentCount, long totalBytes, long warnBytes, string? subject = null, DateTimeOffset? at = null)
    {
        var data = new JsonObject
        {
            ["summary"] = Summary(attachmentCount, totalBytes, "emailed via", "Outlook"),
            ["channel"] = "email",
            ["attachment_count"] = attachmentCount,
            ["total_bytes"] = totalBytes,
        };
        if (!string.IsNullOrWhiteSpace(subject))
        {
            data["subject"] = Clip(subject);
        }
        return AgentEvent.Create(DataTransferEventTypes.EmailAttachment, Severity(totalBytes, warnBytes), data, at);
    }

    private static void AddNames(JsonObject data, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }
        var array = new JsonArray();
        foreach (var name in names)
        {
            array.Add(Clip(name));
        }
        data["files"] = array;
    }

    /// <summary>
    /// A USB copy crossed the limit and the agent auto-set USB to read-only. Critical, for the owner's alert feed.
    /// <paramref name="dryRun"/> true means the read-only switch was logged, not really applied (not the service).
    /// </summary>
    public static AgentEvent UsbLockedDown(
        string destination, long totalBytes, long limitBytes, bool dryRun, DateTimeOffset? at = null)
    {
        var summary = Clip($"USB set to read-only: a {HumanSize(totalBytes)} copy to {destination} went over the "
            + $"{HumanSize(limitBytes)} limit" + (dryRun ? " (test mode, not applied)" : ""));
        var data = new JsonObject
        {
            ["summary"] = summary,
            ["channel"] = "usb",
            ["device"] = Clip(destination),
            ["total_bytes"] = totalBytes,
            ["limit_bytes"] = limitBytes,
            ["action"] = "usb_read_only",
            ["dry_run"] = dryRun,
        };
        return AgentEvent.Create(DataTransferEventTypes.UsbLockedDown, EventSeverity.Critical, data, at);
    }

    private static string Severity(long totalBytes, long warnBytes) =>
        warnBytes > 0 && totalBytes >= warnBytes ? EventSeverity.Warning : EventSeverity.Info;

    private static string Summary(int count, long totalBytes, string verb, string where)
    {
        var noun = count == 1 ? "file" : "files";
        return Clip($"{count} {noun} ({HumanSize(totalBytes)}) {verb} {where}");
    }

    /// <summary>Compact human size, e.g. "3.4 MB". Decimal (1000-based) to match what Explorer-minded owners read.</summary>
    public static string HumanSize(long bytes)
    {
        if (bytes < 1000)
        {
            return $"{bytes} B";
        }
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1000;
            unit++;
        }
        while (value >= 1000 && unit < units.Length - 1);
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private static string Clip(string value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length <= MaxFieldLength ? trimmed : trimmed[..MaxFieldLength];
    }
}
