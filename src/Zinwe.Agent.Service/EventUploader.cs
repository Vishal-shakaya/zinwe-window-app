using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Identity;

namespace Zinwe.Agent.Service;

/// <summary>
/// Uploads queued events every 30s, and straight away after a usb.policy_applied so the owner sees the rule land.
/// Network failures leave events queued for the next round. On a 401 it stops for that token: the
/// <see cref="AgentWorker"/>'s heartbeat owns the "removed" state and wipes the identity; uploads resume
/// automatically once the PC is enrolled again with a new token.
/// </summary>
internal sealed partial class EventUploader(
    EventUploadPump pump,
    EventQueue queue,
    IdentityStore identityStore,
    ILogger<EventUploader> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private string? _rejectedToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queue.Enqueued += OnEnqueued;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await UploadRoundAsync(stoppingToken);
                await _wake.WaitAsync(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            queue.Enqueued -= OnEnqueued;
        }
    }

    private async Task UploadRoundAsync(CancellationToken ct)
    {
        try
        {
            var token = identityStore.Load()?.DeviceToken;
            if (token is null || token == _rejectedToken)
            {
                return; // not enrolled (yet / any more): keep events queued
            }

            var report = await pump.UploadPendingAsync(token, ct);
            if (report.Sent > 0 || report.Dropped > 0)
            {
                LogUploaded(report.Sent, report.Dropped);
            }
            if (report.Outcome == UploadOutcome.DeviceRejected)
            {
                _rejectedToken = token;
                LogRejected();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A BackgroundService that throws stops the whole host. Never let event upload take protection down.
            LogRoundFailed(e);
        }
    }

    private void OnEnqueued(object? sender, AgentEvent e)
    {
        if (e.Type != EventTypes.UsbPolicyApplied)
        {
            return;
        }
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Uploaded {Sent} event(s); dropped {Dropped} refused by the backend")]
    private partial void LogUploaded(int sent, int dropped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event upload got 401; pausing uploads until this PC is enrolled again")]
    private partial void LogRejected();

    [LoggerMessage(Level = LogLevel.Error, Message = "Event upload round failed")]
    private partial void LogRoundFailed(Exception e);
}
