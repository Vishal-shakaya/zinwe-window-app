using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Events;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>
/// The website-blocking half of the web feature: applies the blocklist through the selected
/// <see cref="IWebBlocker"/> and turns blocked-domain requests into events. Never throws — an I/O failure is
/// logged and the agent keeps running (mirrors <c>UsbProtection</c>).
/// </summary>
public sealed class WebProtection(IWebBlocker blocker, EventQueue events, ILogger<WebProtection> logger)
{
    private Blocklist? _applied;

    public bool IsDryRun => blocker.IsDryRun;

    /// <summary>The last blocklist successfully applied, or null before the first apply.</summary>
    public Blocklist? Applied => Volatile.Read(ref _applied);

    /// <summary>Puts <paramref name="list"/> in effect. Returns false if it could not be applied (caller retries).</summary>
    /// <param name="report">Queue a web.blocklist_applied event (true on a real apply, false for a quiet re-apply).</param>
    public bool Apply(Blocklist list, bool report)
    {
        try
        {
            blocker.Apply(list);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            logger.LogError(e, "Could not apply the website blocklist");
            return false;
        }

        Volatile.Write(ref _applied, list);
        if (report)
        {
            TryEnqueue(() => WebEvents.BlocklistApplied(list, blocker.IsDryRun));
        }
        return true;
    }

    /// <summary>Called by the attempt source (any thread) when a blocked domain is requested.</summary>
    public void OnBlockedAttempt(string domain, string? category = null)
    {
        logger.LogInformation("Blocked website attempt: {Domain} ({Mode})", domain, blocker.IsDryRun ? "test mode" : "blocked");
        TryEnqueue(() => WebEvents.BlockedAttempt(domain, category, blocker.IsDryRun));
    }

    private void TryEnqueue(Func<AgentEvent> build)
    {
        try
        {
            var dropped = events.Enqueue(build());
            if (dropped > 0)
            {
                logger.LogWarning("Event queue full ({Capacity}); dropped the {Dropped} oldest event(s)", events.Capacity, dropped);
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError(e, "Could not record a web event");
        }
    }
}
