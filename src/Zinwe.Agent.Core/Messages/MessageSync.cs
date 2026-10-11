using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Messages;

/// <summary>
/// Pulls owner→member messages after each heartbeat and merges them into the widget's notification file.
/// Best-effort: network/HTTP failures are logged and retried next beat (a real 401 is handled by the
/// heartbeat path, which owns the device-rejected state). Never throws to the caller.
/// </summary>
public sealed class MessageSync(BackendClient backend, NotificationWriter writer, MessageOutbox outbox, ILogger<MessageSync> logger)
{
    public async Task SyncAsync(string deviceToken, CancellationToken ct)
    {
        try
        {
            // 1) Push any replies the member typed in the widget, so they reach the admin...
            await DrainOutboxAsync(deviceToken, ct);

            // 2) ...then pull the whole conversation (both directions) into the widget.
            var messages = await backend.GetMessagesAsync(deviceToken, ct);
            var added = writer.Merge(messages);
            if (added > 0)
            {
                logger.LogInformation("Delivered {Count} new message(s) to the widget", added);
            }

            // 3) Tell the backend the admin's messages reached this PC (for the admin's "seen" state).
            await backend.MarkMessagesReadAsync(deviceToken, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug("Message sync deferred: {Error}", e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Could not write delivered messages to the widget file");
        }
    }

    /// <summary>Post each pending member reply; remove the ones that went through. Stops on the first network error.</summary>
    private async Task DrainOutboxAsync(string deviceToken, CancellationToken ct)
    {
        var pending = outbox.Pending();
        if (pending.Count == 0)
        {
            return;
        }
        var posted = new List<string>();
        try
        {
            foreach (var reply in pending)
            {
                await backend.ReplyAsync(deviceToken, reply.Body, ct);
                posted.Add(reply.Id); // only after a confirmed 2xx, so we never double-send
            }
        }
        finally
        {
            // Remove everything confirmed-sent even if a later one failed (prevents re-sending duplicates).
            outbox.Remove(posted);
            if (posted.Count > 0)
            {
                logger.LogInformation("Sent {Count} member reply(ies)", posted.Count);
            }
        }
    }
}
