using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Events;

public enum UploadOutcome
{
    /// <summary>Everything queued was sent (or the per-round limit was reached; the rest goes next round).</summary>
    Done,
    /// <summary>Backend unreachable; events stay queued.</summary>
    NetworkError,
    /// <summary>401: this device token is no longer valid. Events stay queued; stop until the identity changes.</summary>
    DeviceRejected,
}

public sealed record UploadReport(UploadOutcome Outcome, int Sent, int Dropped);

/// <summary>
/// Drains <see cref="EventQueue"/> to the backend in batches of up to 100. A row is deleted only after a 2xx.
/// If the backend refuses a batch's content (400), the batch is retried one event at a time and only the events
/// refused on their own are dropped, so one bad event can never wedge the queue.
/// </summary>
public sealed class EventUploadPump(BackendClient backend, EventQueue queue, ILogger<EventUploadPump> logger)
{
    /// <summary>Caps one round at 2,000 events so a big backlog can't monopolise the connection.</summary>
    public const int MaxBatchesPerRound = 20;

    public async Task<UploadReport> UploadPendingAsync(string deviceToken, CancellationToken ct)
    {
        int sent = 0, dropped = 0;
        try
        {
            for (var round = 0; round < MaxBatchesPerRound; round++)
            {
                var batch = queue.PeekBatch(BackendClient.MaxEventsPerUpload);
                if (batch.Count == 0)
                {
                    break;
                }
                try
                {
                    await backend.UploadEventsAsync(deviceToken, batch, ct);
                    queue.Delete(batch.Select(e => e.Id));
                    sent += batch.Count;
                }
                catch (EventsRejectedException e)
                {
                    logger.LogWarning("Backend refused an event batch ({Error}); retrying one at a time", e.Message);
                    var (ok, bad) = await UploadOneByOneAsync(deviceToken, batch, ct);
                    sent += ok;
                    dropped += bad;
                }
            }
            return new UploadReport(UploadOutcome.Done, sent, dropped);
        }
        catch (DeviceRejectedException)
        {
            return new UploadReport(UploadOutcome.DeviceRejected, sent, dropped);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug("Event upload deferred: {Error}", e.Message);
            return new UploadReport(UploadOutcome.NetworkError, sent, dropped);
        }
    }

    private async Task<(int Sent, int Dropped)> UploadOneByOneAsync(string deviceToken, IReadOnlyList<AgentEvent> batch, CancellationToken ct)
    {
        int sent = 0, dropped = 0;
        foreach (var evt in batch)
        {
            try
            {
                await backend.UploadEventsAsync(deviceToken, [evt], ct);
                sent++;
            }
            catch (EventsRejectedException e)
            {
                logger.LogError("Dropping event {Id} ({Type}) the backend refused: {Error}", evt.Id, evt.Type, e.Message);
                dropped++;
            }
            queue.Delete([evt.Id]); // network errors / 401 propagate before this line, leaving the event queued
        }
        return (sent, dropped);
    }
}
