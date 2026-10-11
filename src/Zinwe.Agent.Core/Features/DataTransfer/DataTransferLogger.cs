using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>
/// The brain of the data-transfer feature, with no OS dependencies so it is fully unit-testable.
///
/// File writes are accumulated per destination (deduped by path, so Windows' repeated Created/Changed
/// notifications and a growing file count once) and turned into one rolled-up metric + one event on
/// <see cref="Flush"/>. Email sends are recorded immediately. Never throws: a store failure is logged and dropped.
/// </summary>
public sealed class DataTransferLogger(
    MetricsStore metrics, EventQueue events, DataTransferOptions options,
    ITransferLimitResponder limitResponder, ILogger<DataTransferLogger> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Pending> _pending = [];

    /// <summary>Record one observed write to a watched target. Thread-safe; does not emit until <see cref="Flush"/>.</summary>
    public void RecordFileWrite(TransferTarget target, FileWriteNotice notice)
    {
        if (string.IsNullOrWhiteSpace(notice.FullPath))
        {
            return;
        }
        var bytes = Math.Max(0, notice.Bytes);
        lock (_lock)
        {
            if (!_pending.TryGetValue(target.Key, out var pending))
            {
                pending = new Pending(target);
                _pending[target.Key] = pending;
            }
            // Latest size wins per distinct path: a file reported many times as it grows counts once, at its final size.
            pending.Files[notice.FullPath] = bytes;
        }
    }

    /// <summary>
    /// Emit one metric + one event per destination that saw writes since the last flush, then clear. Safe to call
    /// on a timer and once more at shutdown. Returns how many destinations were reported (for tests/logging).
    /// </summary>
    public int Flush(DateTimeOffset? at = null)
    {
        List<Pending> ready;
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return 0;
            }
            ready = [.. _pending.Values];
            _pending.Clear();
        }

        var when = at ?? DateTimeOffset.Now;
        foreach (var pending in ready)
        {
            var fileCount = pending.Files.Count;
            if (fileCount == 0)
            {
                continue;
            }
            long totalBytes = 0;
            foreach (var size in pending.Files.Values)
            {
                totalBytes += size;
            }
            var target = pending.Target;

            TryAddMetric(target.MetricCategory, target.Key, fileCount, totalBytes, when);

            var names = DetailNames(pending.Files.Keys);
            var warnBytes = target.Channel == TransferChannel.Usb ? options.UsbWarnBytes : options.LanWarnBytes;
            var evt = target.Channel == TransferChannel.Usb
                ? DataTransferEvents.UsbCopy(target, fileCount, totalBytes, names, warnBytes, when)
                : DataTransferEvents.ShareCopy(target, fileCount, totalBytes, names, warnBytes, when);
            TryEnqueue(evt);

            // Enforcement response: a USB copy over the limit locks USB to read-only (if the admin turned that on).
            if (target.Channel == TransferChannel.Usb && options.UsbLockdownOverLimit
                && options.UsbWarnBytes > 0 && totalBytes > options.UsbWarnBytes)
            {
                try
                {
                    limitResponder.OnUsbLimitExceeded(target.Key, totalBytes, options.UsbWarnBytes, when);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "USB over-limit lockdown response failed");
                }
            }
        }
        return ready.Count;
    }

    /// <summary>Record a mail send with attachments right away (one send → one metric row + one event).</summary>
    public void RecordEmailAttachments(EmailSendNotice notice, DateTimeOffset? at = null)
    {
        if (notice.AttachmentCount <= 0)
        {
            return;
        }
        var when = at ?? DateTimeOffset.Now;
        var totalBytes = Math.Max(0, notice.TotalBytes);

        TryAddMetric("transfer.email", "Outlook", notice.AttachmentCount, totalBytes, when);

        var subject = options.IncludeDetails ? notice.Subject : null;
        TryEnqueue(DataTransferEvents.EmailAttachment(notice.AttachmentCount, totalBytes, options.EmailWarnBytes, subject, when));
    }

    private IReadOnlyList<string> DetailNames(IEnumerable<string> paths)
    {
        if (!options.IncludeDetails)
        {
            return [];
        }
        var names = new List<string>();
        foreach (var path in paths)
        {
            if (names.Count >= Math.Max(0, options.MaxDetailNames))
            {
                break;
            }
            try
            {
                names.Add(Path.GetFileName(path));
            }
            catch (ArgumentException)
            {
                // malformed path: skip its name, the count/size already stands
            }
        }
        return names;
    }

    private void TryAddMetric(string category, string key, long count, long bytes, DateTimeOffset when)
    {
        try
        {
            metrics.Add(category, key, count, bytes, seconds: 0, when);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError(e, "Could not record a {Category} metric", category);
        }
    }

    private void TryEnqueue(AgentEvent evt)
    {
        try
        {
            var dropped = events.Enqueue(evt);
            if (dropped > 0)
            {
                logger.LogWarning("Event queue full ({Capacity}); dropped {Dropped} oldest event(s)", events.Capacity, dropped);
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            logger.LogError(e, "Could not record a data-transfer event");
        }
    }

    private sealed class Pending(TransferTarget target)
    {
        public TransferTarget Target { get; } = target;

        /// <summary>Distinct written paths → latest observed size. Dedupes the write-notification storm.</summary>
        public Dictionary<string, long> Files { get; } = [];
    }
}
