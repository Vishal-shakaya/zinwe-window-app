using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Features;

namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>
/// The "data_transfer" feature module: watches removable and network drives for file writes and (best-effort)
/// classic Outlook for sent attachments, feeding everything to <see cref="DataTransferLogger"/>. Visibility only —
/// it changes nothing on the PC, so there is no enforcement/dry-run path here. Never throws out of Start/Stop.
/// </summary>
public sealed class DataTransferModule(
    IDriveEnumerator drives,
    IFolderWatcherFactory watcherFactory,
    IOutlookAttachmentWatcher outlook,
    DataTransferLogger logger,
    IEmailLimitPublisher emailLimit,
    DataTransferOptions options,
    ILogger<DataTransferModule> log) : IFeatureModule
{
    private readonly Dictionary<string, IFolderWatcher> _watchers = [];
    private readonly Lock _watchersLock = new();
    private CancellationTokenSource? _cts;
    private Task? _driveLoop;
    private Task? _flushLoop;

    public string Name => "data_transfer";

    /// <summary>For tests: the target keys currently being watched.</summary>
    internal IReadOnlyList<string> ActiveWatcherKeys
    {
        get
        {
            lock (_watchersLock)
            {
                return [.. _watchers.Keys];
            }
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!options.Enabled)
        {
            log.LogInformation("data_transfer is disabled by config; not watching anything");
            return Task.CompletedTask;
        }
        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SyncDrives();
            StartOutlook();
            // Keep the classic-Outlook SendGuard add-in's block limit in step with the owner's email limit.
            try { emailLimit.Publish(options.EmailWarnBytes); }
            catch (Exception e) { log.LogWarning(e, "Could not publish the email limit for the Outlook add-in"); }
            _driveLoop = Task.Run(() => DriveLoopAsync(_cts.Token));
            _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));
            log.LogInformation("data_transfer watching for USB/LAN copies and Outlook attachments");
        }
        catch (Exception e)
        {
            log.LogError(e, "data_transfer failed to start; continuing without transfer visibility");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync();
            }
            await WhenAllSafe(_driveLoop, _flushLoop);

            lock (_watchersLock)
            {
                foreach (var watcher in _watchers.Values)
                {
                    SafeDispose(watcher);
                }
                _watchers.Clear();
            }
            SafeDispose(outlook);

            FlushNow(); // don't lose a partial window on shutdown
        }
        catch (Exception e)
        {
            log.LogError(e, "data_transfer did not stop cleanly");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Reconcile watchers with the drives mounted right now: start new ones, drop ones that went away.</summary>
    internal void SyncDrives()
    {
        IReadOnlyList<TransferTarget> targets;
        try
        {
            targets = drives.GetTargets();
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not enumerate drives for data_transfer");
            return;
        }

        var wanted = new HashSet<string>();
        foreach (var target in targets)
        {
            wanted.Add(target.Key);
            lock (_watchersLock)
            {
                if (_watchers.ContainsKey(target.Key))
                {
                    continue;
                }
            }
            StartWatcher(target);
        }

        List<KeyValuePair<string, IFolderWatcher>> gone;
        lock (_watchersLock)
        {
            gone = [.. _watchers.Where(kv => !wanted.Contains(kv.Key))];
            foreach (var kv in gone)
            {
                _watchers.Remove(kv.Key);
            }
        }
        foreach (var kv in gone)
        {
            log.LogInformation("Stopped watching {Key} (drive removed)", kv.Key);
            SafeDispose(kv.Value);
        }
    }

    /// <summary>Flush the logger, swallowing anything (tests and the shutdown path both call this).</summary>
    internal void FlushNow()
    {
        try
        {
            logger.Flush();
        }
        catch (Exception e)
        {
            log.LogError(e, "data_transfer flush failed");
        }
    }

    private void StartWatcher(TransferTarget target)
    {
        IFolderWatcher watcher;
        try
        {
            watcher = watcherFactory.Create(target);
            watcher.FileWritten += OnFileWritten;
            watcher.Start();
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not watch {Key} for copies; skipping it", target.Key);
            return;
        }
        lock (_watchersLock)
        {
            _watchers[target.Key] = watcher;
        }
        log.LogInformation("Watching {Key} for copies", target.Key);
    }

    private void StartOutlook()
    {
        try
        {
            outlook.AttachmentsSent += OnAttachmentsSent;
            outlook.Start();
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Outlook attachment watch unavailable; email transfers won't be reported");
        }
    }

    private void OnFileWritten(object? sender, FileWriteNotice notice)
    {
        if (sender is IFolderWatcher watcher)
        {
            logger.RecordFileWrite(watcher.Target, notice);
        }
    }

    private void OnAttachmentsSent(object? sender, EmailSendNotice notice) => logger.RecordEmailAttachments(notice);

    private async Task DriveLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, options.DrivePollSeconds));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                SyncDrives();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, options.FlushSeconds));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                FlushNow();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private static async Task WhenAllSafe(params Task?[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks.Where(t => t is not null)!);
        }
        catch (OperationCanceledException)
        {
            // expected on cancel
        }
    }

    private void SafeDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Disposing a data_transfer watcher failed");
        }
    }
}
