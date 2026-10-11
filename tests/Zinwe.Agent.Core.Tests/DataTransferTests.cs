using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.DataTransfer;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Tests;

public sealed class TransferKeysTests
{
    [Theory]
    [InlineData(@"E:\", "KINGSTON", "E: (KINGSTON)")]
    [InlineData(@"e:\", null, "E:")]
    [InlineData(@"E:\", "   ", "E:")]
    [InlineData("F:", "Backup", "F: (Backup)")]
    public void Removable_key_is_letter_plus_label(string root, string? label, string expected) =>
        Assert.Equal(expected, TransferKeys.RemovableKey(root, label));

    [Theory]
    [InlineData(@"\\FS01\Finance\sub\q.xlsx", "FS01\\Finance")]
    [InlineData(@"\\FS01\Finance", "FS01\\Finance")]
    [InlineData(@"\\FS01", "FS01")]
    [InlineData(@"E:\local\file", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Share_is_parsed_from_unc_without_subpath(string? unc, string? expected) =>
        Assert.Equal(expected, TransferKeys.ShareFromUnc(unc));

    [Fact]
    public void Network_key_prefers_unc_then_falls_back_to_letter()
    {
        Assert.Equal("FS01\\Finance", TransferKeys.NetworkKey(@"Z:\", @"\\FS01\Finance\x", "Fin"));
        Assert.Equal("Z: (Fin)", TransferKeys.NetworkKey(@"Z:\", null, "Fin"));
        Assert.Equal("Z:", TransferKeys.NetworkKey(@"Z:\", "", null));
    }

    [Fact]
    public void Keys_are_clipped_to_the_max_length()
    {
        var label = new string('x', 500);
        Assert.True(TransferKeys.RemovableKey(@"E:\", label).Length <= TransferKeys.MaxKeyLength);
    }
}

public sealed class DataTransferEventsTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(999L, "999 B")]
    [InlineData(1000L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1_000_000L, "1 MB")]
    [InlineData(3_400_000L, "3.4 MB")]
    public void Human_size_is_compact_decimal(long bytes, string expected) =>
        Assert.Equal(expected, DataTransferEvents.HumanSize(bytes));

    [Fact]
    public void Usb_copy_carries_counts_sizes_and_device_only()
    {
        var target = new TransferTarget(TransferChannel.Usb, @"E:\", "E: (KINGSTON)");
        var evt = DataTransferEvents.UsbCopy(target, fileCount: 3, totalBytes: 2048, names: [], warnBytes: 1_000_000);

        Assert.Equal(DataTransferEventTypes.UsbCopy, evt.Type);
        Assert.Equal(EventSeverity.Info, evt.Severity);
        var data = JsonNode.Parse(evt.DataJson)!.AsObject();
        Assert.Equal("usb", (string?)data["channel"]);
        Assert.Equal("E: (KINGSTON)", (string?)data["device"]);
        Assert.Equal(3, (int?)data["file_count"]);
        Assert.Equal(2048, (long?)data["total_bytes"]);
        Assert.Contains("E: (KINGSTON)", (string?)data["summary"]);
        Assert.False(data.ContainsKey("files")); // no names by default
        evt.Validate();
    }

    [Fact]
    public void Names_appear_only_when_supplied()
    {
        var target = new TransferTarget(TransferChannel.Usb, @"E:\", "E:");
        var evt = DataTransferEvents.UsbCopy(target, 2, 10, names: ["a.docx", "b.xlsx"], warnBytes: 1_000_000);
        var files = JsonNode.Parse(evt.DataJson)!.AsObject()["files"]!.AsArray();
        Assert.Equal(["a.docx", "b.xlsx"], files.Select(n => (string?)n));
    }

    [Fact]
    public void Share_copy_uses_share_field_and_warns_past_threshold()
    {
        var target = new TransferTarget(TransferChannel.Lan, @"Z:\", "FS01\\Finance");
        var evt = DataTransferEvents.ShareCopy(target, 1, 5_000_000, names: [], warnBytes: 1_000_000);

        Assert.Equal(DataTransferEventTypes.ShareCopy, evt.Type);
        Assert.Equal(EventSeverity.Warning, evt.Severity);
        var data = JsonNode.Parse(evt.DataJson)!.AsObject();
        Assert.Equal("lan", (string?)data["channel"]);
        Assert.Equal("FS01\\Finance", (string?)data["share"]);
    }

    [Fact]
    public void Email_subject_included_only_when_passed()
    {
        var withSubject = DataTransferEvents.EmailAttachment(2, 100, warnBytes: 1_000, subject: "Q3 numbers");
        var without = DataTransferEvents.EmailAttachment(2, 100, warnBytes: 1_000);

        Assert.Equal("Q3 numbers", (string?)JsonNode.Parse(withSubject.DataJson)!.AsObject()["subject"]);
        Assert.False(JsonNode.Parse(without.DataJson)!.AsObject().ContainsKey("subject"));
        Assert.Equal(DataTransferEventTypes.EmailAttachment, without.Type);
    }
}

public sealed class DataTransferLoggerTests
{
    private static DataTransferLogger Build(TempDir dir, DataTransferOptions options, out MetricsStore metrics, out EventQueue events, ITransferLimitResponder? responder = null)
    {
        metrics = new MetricsStore(dir.Path);
        events = new EventQueue(dir.Path);
        return new DataTransferLogger(metrics, events, options, responder ?? new NullTransferLimitResponder(), NullLogger<DataTransferLogger>.Instance);
    }

    private static readonly TransferTarget Usb = new(TransferChannel.Usb, @"E:\", "E: (KINGSTON)");
    private static readonly TransferTarget Lan = new(TransferChannel.Lan, @"Z:\", "FS01\\Finance");

    private sealed class CapturingResponder : ITransferLimitResponder
    {
        public int Calls { get; private set; }
        public string? LastDestination { get; private set; }
        public long LastTotal { get; private set; }
        public long LastLimit { get; private set; }

        public void OnUsbLimitExceeded(string destination, long totalBytes, long limitBytes, DateTimeOffset at)
        {
            Calls++;
            LastDestination = destination;
            LastTotal = totalBytes;
            LastLimit = limitBytes;
        }
    }

    [Fact]
    public void Usb_copy_over_limit_triggers_lockdown_only_when_enabled()
    {
        using var dir = new TempDir();
        var responder = new CapturingResponder();
        // Limit 50 MB, lockdown ON.
        var options = new DataTransferOptions { UsbWarnBytes = 50L * 1024 * 1024, UsbLockdownOverLimit = true };
        var logger = Build(dir, options, out _, out _, responder);

        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\big.zip", 60L * 1024 * 1024)); // over the limit
        logger.Flush();
        Assert.Equal(1, responder.Calls);
        Assert.Equal("E: (KINGSTON)", responder.LastDestination);
        Assert.Equal(60L * 1024 * 1024, responder.LastTotal);
        Assert.Equal(50L * 1024 * 1024, responder.LastLimit);

        // Under the limit: no lockdown.
        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\small.txt", 10L * 1024 * 1024));
        logger.Flush();
        Assert.Equal(1, responder.Calls);
    }

    [Fact]
    public void Usb_copy_over_limit_does_not_lock_down_when_disabled()
    {
        using var dir = new TempDir();
        var responder = new CapturingResponder();
        var options = new DataTransferOptions { UsbWarnBytes = 50L * 1024 * 1024, UsbLockdownOverLimit = false };
        var logger = Build(dir, options, out _, out _, responder);

        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\big.zip", 60L * 1024 * 1024));
        logger.Flush();
        Assert.Equal(0, responder.Calls);
    }

    [Fact]
    public void Lan_copy_over_limit_never_triggers_usb_lockdown()
    {
        using var dir = new TempDir();
        var responder = new CapturingResponder();
        var options = new DataTransferOptions { LanWarnBytes = 10L * 1024 * 1024, UsbLockdownOverLimit = true };
        var logger = Build(dir, options, out _, out _, responder);

        logger.RecordFileWrite(Lan, new FileWriteNotice(@"Z:\big.zip", 60L * 1024 * 1024));
        logger.Flush();
        Assert.Equal(0, responder.Calls); // lockdown is USB-only
    }

    [Fact]
    public void Repeated_notifications_for_one_file_count_once_at_latest_size()
    {
        using var dir = new TempDir();
        var logger = Build(dir, new DataTransferOptions(), out var metrics, out var events);

        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\a.bin", 100));
        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\a.bin", 500));
        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\a.bin", 900)); // same file, growing

        Assert.Equal(1, logger.Flush());

        var row = Assert.Single(metrics.Snapshot());
        Assert.Equal("transfer.usb", row.Category);
        Assert.Equal("E: (KINGSTON)", row.Key);
        Assert.Equal(1, row.Count);
        Assert.Equal(900, row.Bytes);

        var evt = Assert.Single(events.PeekBatch(10));
        var data = JsonNode.Parse(evt.DataJson)!.AsObject();
        Assert.Equal(1, (int?)data["file_count"]);
        Assert.Equal(900, (long?)data["total_bytes"]);
    }

    [Fact]
    public void Distinct_files_sum_into_one_rolled_up_event_per_target()
    {
        using var dir = new TempDir();
        var logger = Build(dir, new DataTransferOptions(), out var metrics, out var events);

        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\a", 100));
        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\b", 200));
        logger.RecordFileWrite(Lan, new FileWriteNotice(@"Z:\c", 50));

        Assert.Equal(2, logger.Flush());

        var rows = metrics.Snapshot();
        Assert.Equal(2, rows.Count);
        var usb = rows.Single(r => r.Category == "transfer.usb");
        Assert.Equal(2, usb.Count);
        Assert.Equal(300, usb.Bytes);
        var lan = rows.Single(r => r.Category == "transfer.lan");
        Assert.Equal(1, lan.Count);
        Assert.Equal(50, lan.Bytes);
        Assert.Equal(2, events.Count());
    }

    [Fact]
    public void Flush_clears_pending_so_a_second_flush_emits_nothing()
    {
        using var dir = new TempDir();
        var logger = Build(dir, new DataTransferOptions(), out _, out var events);
        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\a", 100));

        Assert.Equal(1, logger.Flush());
        Assert.Equal(0, logger.Flush());
        Assert.Equal(1, events.Count());
    }

    [Fact]
    public void Large_copy_is_a_warning()
    {
        using var dir = new TempDir();
        var options = new DataTransferOptions { UsbWarnBytes = 1000 };
        var logger = Build(dir, options, out _, out var events);

        logger.RecordFileWrite(Usb, new FileWriteNotice(@"E:\big", 5000));
        logger.Flush();

        Assert.Equal(EventSeverity.Warning, Assert.Single(events.PeekBatch(10)).Severity);
    }

    [Fact]
    public void File_names_are_withheld_unless_details_are_enabled()
    {
        using var dir = new TempDir();
        var off = Build(dir, new DataTransferOptions { IncludeDetails = false }, out _, out var eventsOff);
        off.RecordFileWrite(Usb, new FileWriteNotice(@"E:\secret-plan.docx", 10));
        off.Flush();
        var dataOff = JsonNode.Parse(eventsOff.PeekBatch(1)[0].DataJson)!.AsObject();
        Assert.False(dataOff.ContainsKey("files"));

        using var dir2 = new TempDir();
        var on = Build(dir2, new DataTransferOptions { IncludeDetails = true }, out _, out var eventsOn);
        on.RecordFileWrite(Usb, new FileWriteNotice(@"E:\secret-plan.docx", 10));
        on.Flush();
        var filesOn = JsonNode.Parse(eventsOn.PeekBatch(1)[0].DataJson)!.AsObject()["files"]!.AsArray();
        Assert.Equal("secret-plan.docx", (string?)filesOn[0]);
    }

    [Fact]
    public void Detail_names_are_capped()
    {
        using var dir = new TempDir();
        var on = Build(dir, new DataTransferOptions { IncludeDetails = true, MaxDetailNames = 3 }, out _, out var events);
        for (var i = 0; i < 10; i++)
        {
            on.RecordFileWrite(Usb, new FileWriteNotice($@"E:\f{i}.bin", 1));
        }
        on.Flush();
        var data = JsonNode.Parse(events.PeekBatch(1)[0].DataJson)!.AsObject();
        Assert.Equal(10, (int?)data["file_count"]);          // count is honest
        Assert.Equal(3, data["files"]!.AsArray().Count);      // but only 3 names leak
    }

    [Fact]
    public void Email_send_is_recorded_immediately_with_counts_and_size()
    {
        using var dir = new TempDir();
        var logger = Build(dir, new DataTransferOptions(), out var metrics, out var events);

        logger.RecordEmailAttachments(new EmailSendNotice(2, 4096, "Quarterly"));

        var row = Assert.Single(metrics.Snapshot());
        Assert.Equal("transfer.email", row.Category);
        Assert.Equal("Outlook", row.Key);
        Assert.Equal(2, row.Count);
        Assert.Equal(4096, row.Bytes);

        var data = JsonNode.Parse(Assert.Single(events.PeekBatch(10)).DataJson)!.AsObject();
        Assert.Equal(2, (int?)data["attachment_count"]);
        Assert.Equal("email", (string?)data["channel"]);
        Assert.False(data.ContainsKey("subject")); // details off
    }

    [Fact]
    public void Email_without_attachments_is_ignored()
    {
        using var dir = new TempDir();
        var logger = Build(dir, new DataTransferOptions(), out var metrics, out var events);
        logger.RecordEmailAttachments(new EmailSendNotice(0, 0));
        Assert.Empty(metrics.Snapshot());
        Assert.Equal(0, events.Count());
    }

    [Fact]
    public void Empty_path_writes_are_ignored()
    {
        using var dir = new TempDir();
        var logger = Build(dir, new DataTransferOptions(), out _, out _);
        logger.RecordFileWrite(Usb, new FileWriteNotice("", 100));
        Assert.Equal(0, logger.Flush());
    }
}

public sealed class DataTransferModuleTests
{
    private static readonly TransferTarget Usb = new(TransferChannel.Usb, @"E:\", "E: (KINGSTON)");
    private static readonly TransferTarget Lan = new(TransferChannel.Lan, @"Z:\", "FS01\\Finance");

    private static DataTransferModule Build(
        TempDir dir, FakeDriveEnumerator drives, FakeFolderWatcherFactory factory, FakeOutlookWatcher outlook,
        out DataTransferLogger logger, out MetricsStore metrics, out EventQueue events, DataTransferOptions? options = null)
    {
        options ??= new DataTransferOptions();
        metrics = new MetricsStore(dir.Path);
        events = new EventQueue(dir.Path);
        logger = new DataTransferLogger(metrics, events, options, new NullTransferLimitResponder(), NullLogger<DataTransferLogger>.Instance);
        return new DataTransferModule(drives, factory, outlook, logger, new NullEmailLimitPublisher(), options, NullLogger<DataTransferModule>.Instance);
    }

    [Fact]
    public void Sync_starts_a_watcher_per_target()
    {
        using var dir = new TempDir();
        var drives = new FakeDriveEnumerator([Usb, Lan]);
        var factory = new FakeFolderWatcherFactory();
        var module = Build(dir, drives, factory, new FakeOutlookWatcher(), out _, out _, out _);

        module.SyncDrives();

        Assert.Equal(["E: (KINGSTON)", "FS01\\Finance"], module.ActiveWatcherKeys.Order());
        Assert.All(factory.Created, w => Assert.True(w.Started));
    }

    [Fact]
    public void Sync_drops_watchers_for_drives_that_went_away_and_adds_new_ones()
    {
        using var dir = new TempDir();
        var drives = new FakeDriveEnumerator([Usb, Lan]);
        var factory = new FakeFolderWatcherFactory();
        var module = Build(dir, drives, factory, new FakeOutlookWatcher(), out _, out _, out _);
        module.SyncDrives();

        drives.Targets = [Lan]; // USB unplugged
        module.SyncDrives();

        Assert.Equal(["FS01\\Finance"], module.ActiveWatcherKeys);
        var usbWatcher = factory.Created.Single(w => w.Target.Key == "E: (KINGSTON)");
        Assert.True(usbWatcher.Disposed);

        drives.Targets = [Lan, Usb]; // USB back
        module.SyncDrives();
        Assert.Equal(["E: (KINGSTON)", "FS01\\Finance"], module.ActiveWatcherKeys.Order());
    }

    [Fact]
    public void A_watched_write_flows_through_to_metrics_on_flush()
    {
        using var dir = new TempDir();
        var factory = new FakeFolderWatcherFactory();
        var module = Build(dir, new FakeDriveEnumerator([Usb]), factory, new FakeOutlookWatcher(),
            out _, out var metrics, out _);
        module.SyncDrives();

        factory.Created.Single().Raise(new FileWriteNotice(@"E:\report.pdf", 2048));
        module.FlushNow();

        var row = Assert.Single(metrics.Snapshot());
        Assert.Equal("transfer.usb", row.Category);
        Assert.Equal(2048, row.Bytes);
    }

    [Fact]
    public async Task Email_sends_flow_through_once_started()
    {
        using var dir = new TempDir();
        var outlook = new FakeOutlookWatcher();
        var module = Build(dir, new FakeDriveEnumerator([]), new FakeFolderWatcherFactory(), outlook,
            out _, out var metrics, out var events);

        await module.StartAsync(CancellationToken.None);
        Assert.True(outlook.Started);
        outlook.Raise(new EmailSendNotice(1, 1234));
        await module.StopAsync(CancellationToken.None);

        Assert.Equal("transfer.email", Assert.Single(metrics.Snapshot()).Category);
        Assert.Equal(1, events.Count());
        Assert.True(outlook.Disposed);
    }

    [Fact]
    public async Task Disabled_module_watches_nothing()
    {
        using var dir = new TempDir();
        var factory = new FakeFolderWatcherFactory();
        var module = Build(dir, new FakeDriveEnumerator([Usb]), factory, new FakeOutlookWatcher(),
            out _, out _, out _, new DataTransferOptions { Enabled = false });

        await module.StartAsync(CancellationToken.None);
        Assert.Empty(module.ActiveWatcherKeys);
        Assert.Empty(factory.Created);
        await module.StopAsync(CancellationToken.None);
    }

    private sealed class FakeDriveEnumerator(IReadOnlyList<TransferTarget> targets) : IDriveEnumerator
    {
        public IReadOnlyList<TransferTarget> Targets { get; set; } = targets;
        public IReadOnlyList<TransferTarget> GetTargets() => Targets;
    }

    private sealed class FakeFolderWatcherFactory : IFolderWatcherFactory
    {
        public List<FakeFolderWatcher> Created { get; } = [];
        public IFolderWatcher Create(TransferTarget target)
        {
            var watcher = new FakeFolderWatcher(target);
            Created.Add(watcher);
            return watcher;
        }
    }

    private sealed class FakeFolderWatcher(TransferTarget target) : IFolderWatcher
    {
        public TransferTarget Target => target;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public event EventHandler<FileWriteNotice>? FileWritten;
        public void Start() => Started = true;
        public void Raise(FileWriteNotice notice) => FileWritten?.Invoke(this, notice);
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeOutlookWatcher : IOutlookAttachmentWatcher
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public event EventHandler<EmailSendNotice>? AttachmentsSent;
        public void Start() => Started = true;
        public void Raise(EmailSendNotice notice) => AttachmentsSent?.Invoke(this, notice);
        public void Dispose() => Disposed = true;
    }
}
