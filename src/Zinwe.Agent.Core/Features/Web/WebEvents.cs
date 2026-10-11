using System.Text.Json.Nodes;
using Zinwe.Agent.Core.Events;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>Event types this feature emits. Dotted-lowercase; the backend titles unknown types generically.</summary>
public static class WebEventTypes
{
    public const string BlocklistApplied = "web.blocklist_applied";
    public const string BlockedAttempt = "web.blocked_attempt";
}

/// <summary>Metric categories this feature rolls up into <see cref="Metrics.MetricsStore"/>.</summary>
public static class WebMetrics
{
    /// <summary>Foreground time per app, keyed by process name (seconds accumulated per sample).</summary>
    public const string ForegroundApp = "app.foreground";

    /// <summary>Time + visits per visited domain (host only), from the foreground browser's address bar.</summary>
    public const string WebDomain = "web.domain";
}

/// <summary>Builders for the web feature's events. Payloads are small, counts/identifiers only (no full URLs).</summary>
public static class WebEvents
{
    private const int MaxField = 200;

    /// <summary>Fired once when the blocklist is put in effect (dry-run aware), so the feed shows it is active.</summary>
    public static AgentEvent BlocklistApplied(Blocklist list, bool dryRun, DateTimeOffset? at = null)
    {
        var categories = new JsonArray();
        foreach (var category in list.Categories)
        {
            categories.Add(Clip(category));
        }

        var summary = list.IsEmpty
            ? "Website blocklist cleared"
            : $"Website blocking active: {list.Rules.Count} domain(s) across {list.Categories.Count} categor"
              + (list.Categories.Count == 1 ? "y" : "ies") + (dryRun ? " (test mode)" : "");

        var data = new JsonObject
        {
            ["summary"] = Clip(summary),
            ["blocked_count"] = list.Rules.Count,
            ["categories"] = categories,
            ["dry_run"] = dryRun,
        };
        return AgentEvent.Create(WebEventTypes.BlocklistApplied, EventSeverity.Info, data, at);
    }

    /// <summary>
    /// Fired when a blocked domain is actually requested. The domain is the metric itself; no full URL or query
    /// string is recorded. <paramref name="dryRun"/> true means the attempt was observed but NOT really blocked.
    /// </summary>
    public static AgentEvent BlockedAttempt(string domain, string? category, bool dryRun, DateTimeOffset? at = null)
    {
        var clean = Blocklist.NormalizeDomain(domain) ?? Clip(domain);
        var summary = $"Blocked website attempt: {clean}" + (dryRun ? " (test mode, not blocked)" : "");
        var data = new JsonObject
        {
            ["summary"] = Clip(summary),
            ["domain"] = Clip(clean),
            ["dry_run"] = dryRun,
        };
        if (!string.IsNullOrWhiteSpace(category))
        {
            data["category"] = Clip(category);
        }
        return AgentEvent.Create(WebEventTypes.BlockedAttempt, EventSeverity.Warning, data, at);
    }

    private static string Clip(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return trimmed.Length <= MaxField ? trimmed : trimmed[..MaxField];
    }
}
