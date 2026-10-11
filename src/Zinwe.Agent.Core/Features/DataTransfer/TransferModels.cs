namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>Which egress channel a copy went out on.</summary>
public enum TransferChannel
{
    /// <summary>A removable (USB) drive. Metric category "transfer.usb", event data.usb_copy.</summary>
    Usb,

    /// <summary>A mapped network drive or UNC share. Metric category "transfer.lan", event data.share_copy.</summary>
    Lan,
}

/// <summary>
/// A place files can be written to that we watch: one removable drive or one network share.
/// <see cref="Root"/> is the filesystem path the watcher sits on (e.g. "E:\\"); <see cref="Key"/> is the stable,
/// owner-recognisable identifier recorded in metrics and events (a drive label or a host\share) — never a file path.
/// </summary>
public sealed record TransferTarget(TransferChannel Channel, string Root, string Key)
{
    public string MetricCategory => Channel switch
    {
        TransferChannel.Usb => "transfer.usb",
        TransferChannel.Lan => "transfer.lan",
        _ => "transfer.other",
    };
}

/// <summary>A single file write the OS reported on a watched target (size read best-effort at notice time).</summary>
/// <param name="FullPath">Absolute path of the written file; used only to dedupe and, if details are on, for its leaf name.</param>
public sealed record FileWriteNotice(string FullPath, long Bytes);

/// <summary>A mail send classic Outlook reported (counts and sizes only; <paramref name="Subject"/> used only if details are on).</summary>
public sealed record EmailSendNotice(int AttachmentCount, long TotalBytes, string? Subject = null);

/// <summary>
/// Watches one folder tree for file writes. The service implements this with a <c>FileSystemWatcher</c>; tests use
/// a fake. Must never throw out of <see cref="Start"/>; a watcher that cannot start is simply skipped by the host.
/// </summary>
public interface IFolderWatcher : IDisposable
{
    /// <summary>The target this watcher reports writes for.</summary>
    TransferTarget Target { get; }

    /// <summary>Raised (any thread) when a file under the target's root is created or changed.</summary>
    event EventHandler<FileWriteNotice>? FileWritten;

    /// <summary>Begin watching. Throws if the OS facility is unavailable; the host logs and drops this watcher.</summary>
    void Start();
}

/// <summary>Creates an <see cref="IFolderWatcher"/> for a target. Service: real FileSystemWatcher; tests: fake.</summary>
public interface IFolderWatcherFactory
{
    IFolderWatcher Create(TransferTarget target);
}

/// <summary>Enumerates the removable and network destinations currently mounted. Service: DriveInfo/WMI; tests: fake.</summary>
public interface IDriveEnumerator
{
    /// <summary>Current writable removable/network targets. Read-only detection; may run live.</summary>
    IReadOnlyList<TransferTarget> GetTargets();
}

/// <summary>
/// Best-effort watcher for attachments leaving via classic desktop Outlook. Service implements it with late-bound
/// COM against an already-running Outlook; tests use a fake. No-ops cleanly when Outlook/COM is absent.
/// </summary>
public interface IOutlookAttachmentWatcher : IDisposable
{
    /// <summary>Raised (any thread) when a mail with attachments was sent.</summary>
    event EventHandler<EmailSendNotice>? AttachmentsSent;

    /// <summary>Begin polling. Must never throw; if Outlook is unavailable it simply reports nothing.</summary>
    void Start();
}
