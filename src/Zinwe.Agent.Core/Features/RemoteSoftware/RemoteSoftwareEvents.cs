using System.Text.Json.Nodes;
using Zinwe.Agent.Core.Events;

namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>Event types this feature emits. Dotted-lowercase; the backend words titles for the owner later.</summary>
public static class RemoteSoftwareEventTypes
{
    /// <summary>A newly installed product appeared in the uninstall registry.</summary>
    public const string SoftwareInstalled = "software.installed";

    /// <summary>A known remote-access tool was seen running (or listening).</summary>
    public const string RemoteToolDetected = "remote.tool_detected";

    /// <summary>A listed remote tool was terminated (or, in test mode, would have been).</summary>
    public const string RemoteBlocked = "remote.blocked";
}

/// <summary>Metric categories this feature rolls up into <see cref="Metrics.MetricsStore"/>.</summary>
public static class RemoteSoftwareMetrics
{
    /// <summary>Count of newly installed products, keyed by product name.</summary>
    public const string Installed = "software.installed";

    /// <summary>Count of remote-tool appearances, keyed by tool display name.</summary>
    public const string RemoteTool = "remote.tool";

    /// <summary>Count of block actions taken/attempted, keyed by tool display name.</summary>
    public const string RemoteBlocked = "remote.blocked";
}

/// <summary>
/// Builders for the feature's events. Payloads carry NAMES, counts and (for tools) how it was detected only;
/// software version and a process id are added just when <c>IncludeDetails</c> is on. No install path, command
/// line, or transferred content is ever included — a remote tool's own channel is encrypted and unreadable here.
/// </summary>
public static class RemoteSoftwareEvents
{
    private const int MaxField = 200;

    /// <param name="version">Display version, included only when the caller passes it (details on).</param>
    public static AgentEvent SoftwareInstalled(
        string name, string? publisher, string? version = null, DateTimeOffset? at = null)
    {
        var clean = Clip(name);
        var summary = publisher is { Length: > 0 }
            ? $"New software installed: {clean} ({Clip(publisher)})"
            : $"New software installed: {clean}";
        var data = new JsonObject
        {
            ["summary"] = Clip(summary),
            ["product"] = clean,
        };
        if (!string.IsNullOrWhiteSpace(publisher))
        {
            data["publisher"] = Clip(publisher);
        }
        if (!string.IsNullOrWhiteSpace(version))
        {
            data["version"] = Clip(version);
        }
        // Warning (not info) so a new install surfaces in the owner's "alerts to action" feed — in a managed SMB,
        // software appearing on a PC is worth a look (we can't block it without WDAC/AppLocker).
        return AgentEvent.Create(RemoteSoftwareEventTypes.SoftwareInstalled, EventSeverity.Warning, data, at);
    }

    /// <param name="pid">Owning process id, included only when the caller passes it (details on).</param>
    public static AgentEvent RemoteToolDetected(
        string toolName, RemoteDetection detectedBy, int? pid = null, DateTimeOffset? at = null)
    {
        var clean = Clip(toolName);
        var how = detectedBy == RemoteDetection.Port ? "listening port" : "running process";
        var data = new JsonObject
        {
            ["summary"] = Clip($"Remote-access tool detected: {clean} ({how})"),
            ["tool"] = clean,
            ["detected_by"] = detectedBy == RemoteDetection.Port ? "port" : "process",
        };
        if (pid is { } p and > 0)
        {
            data["pid"] = p;
        }
        return AgentEvent.Create(RemoteSoftwareEventTypes.RemoteToolDetected, EventSeverity.Warning, data, at);
    }

    /// <param name="dryRun">True in test mode: the process was NOT actually terminated.</param>
    /// <param name="processCount">How many matching processes were targeted.</param>
    public static AgentEvent RemoteBlocked(
        string toolName, bool dryRun, int processCount, DateTimeOffset? at = null)
    {
        var clean = Clip(toolName);
        var noun = processCount == 1 ? "process" : "processes";
        var summary = dryRun
            ? $"Remote-access tool would be blocked: {clean} ({processCount} {noun}, test mode)"
            : $"Remote-access tool blocked: {clean} ({processCount} {noun} terminated)";
        var data = new JsonObject
        {
            ["summary"] = Clip(summary),
            ["tool"] = clean,
            ["process_count"] = processCount,
            ["dry_run"] = dryRun,
        };
        return AgentEvent.Create(RemoteSoftwareEventTypes.RemoteBlocked, EventSeverity.Warning, data, at);
    }

    private static string Clip(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return trimmed.Length <= MaxField ? trimmed : trimmed[..MaxField];
    }
}
