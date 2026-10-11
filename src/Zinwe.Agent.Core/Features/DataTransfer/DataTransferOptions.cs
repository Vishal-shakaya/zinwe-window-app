namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>
/// Settings for the data-transfer logging feature, bound from config section
/// <c>Agent:Features:data_transfer</c>. Every value has a sensible default in code, so the feature works with
/// nothing in appsettings. This is a visibility layer only: it never blocks a copy and never changes the PC.
/// </summary>
public sealed class DataTransferOptions
{
    /// <summary>The config path this feature reads (under the root "Agent" section).</summary>
    public const string Section = "Agent:Features:data_transfer";

    /// <summary>Master switch; false makes the module a clean no-op.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A burst of writes to the same destination is collapsed into one event/metric at this cadence (seconds).
    /// Also dedupes the many Created/Changed notifications Windows raises for a single file.
    /// </summary>
    public int FlushSeconds { get; set; } = 5;

    /// <summary>How often removable/network drives are re-enumerated so watchers follow drives in and out (seconds).</summary>
    public int DrivePollSeconds { get; set; } = 10;

    /// <summary>How often classic Outlook (if already running) is polled for freshly sent mail (seconds).</summary>
    public int EmailPollSeconds { get; set; } = 30;

    /// <summary>A removable-drive copy at or above this many bytes is a <c>warning</c> (else <c>info</c>). Default 50 MB.</summary>
    public long UsbWarnBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>A network-share copy at or above this many bytes is a <c>warning</c> (else <c>info</c>). Default 50 MB.</summary>
    public long LanWarnBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>An email send whose attachments total at or above this many bytes is a <c>warning</c>. Default 20 MB.</summary>
    public long EmailWarnBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// When true, a single copy to a USB drive OVER <see cref="UsbWarnBytes"/> makes the agent immediately set USB
    /// to read-only on this PC (driver-free, via the USB policy) and alert. It can't undo the triggering copy, but
    /// it stops any further writes until the admin restores USB. Real only as the LocalSystem service (else dry-run).
    /// </summary>
    public bool UsbLockdownOverLimit { get; set; }

    /// <summary>
    /// OFF by default (consent-first / DPDP): when false only COUNTS, SIZES and the destination identifier are
    /// recorded. Turn on to also attach file names (copies) and the mail subject (email) to events.
    /// </summary>
    public bool IncludeDetails { get; set; }

    /// <summary>When details are on, at most this many names are attached to one event (keeps payloads &lt; 4 KB).</summary>
    public int MaxDetailNames { get; set; } = 20;
}
