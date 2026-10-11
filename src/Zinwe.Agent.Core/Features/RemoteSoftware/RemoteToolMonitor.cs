using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>
/// The remote-tool brain, with no OS dependencies so it is fully unit-testable. Each scan it matches running
/// processes and listening ports against the <see cref="RemoteToolMatcher"/>; a tool newly PRESENT since the last
/// scan gets one <c>remote.tool_detected</c> event (warning) + one metric. A tool on the block list is passed to
/// the (dry-run-aware) <see cref="IRemoteSoftwareBlocker"/> while present, with one <c>remote.blocked</c> event
/// per appearance. De-dupes so a continuously running tool is reported once, not every poll. Never throws.
/// </summary>
public sealed class RemoteToolMonitor(
    RemoteToolMatcher matcher,
    IRemoteSoftwareBlocker blocker,
    MetricsStore metrics,
    EventQueue events,
    IMemberNotifier memberNotifier,
    RemoteSoftwareOptions options,
    ILogger<RemoteToolMonitor> logger)
{
    private readonly Lock _lock = new();

    // Tool display names present on the previous scan, so we report appearances (absent → present), not polls.
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDryRun => blocker.IsDryRun;

    /// <summary>Tool display names seen on the most recent scan (for tests/diagnostics).</summary>
    internal IReadOnlyCollection<string> Present
    {
        get
        {
            lock (_lock)
            {
                return [.. _present];
            }
        }
    }

    /// <summary>
    /// Match one snapshot of processes and ports; report new appearances; (attempt to) block listed tools.
    /// Returns how many tools newly appeared this scan. Thread-safe.
    /// </summary>
    public int Scan(IReadOnlyList<RunningProcess> processes, IReadOnlyList<ListeningPort> ports, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.Now;

        // Collapse the snapshot to one entry per tool, preferring a process match (it carries a pid we can block).
        var detections = new Dictionary<string, Detection>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in processes)
        {
            var display = matcher.MatchProcess(process.Name);
            if (display is null)
            {
                continue;
            }
            if (!detections.TryGetValue(display, out var det))
            {
                det = new Detection(RemoteDetection.Process);
                detections[display] = det;
            }
            det.By = RemoteDetection.Process;
            det.Processes.Add(process);
        }

        foreach (var port in ports)
        {
            var display = matcher.MatchPort(port.Port);
            if (display is null)
            {
                continue;
            }
            if (!detections.TryGetValue(display, out var det))
            {
                detections[display] = new Detection(RemoteDetection.Port);
            }
            // A process match already present stays the detection method; a port-only tool keeps Port.
        }

        var appeared = 0;
        lock (_lock)
        {
            foreach (var (display, det) in detections)
            {
                var isNew = !_present.Contains(display);
                if (isNew)
                {
                    appeared++;
                    TryAddMetric(RemoteSoftwareMetrics.RemoteTool, display, when);
                    var pid = options.IncludeDetails && det.Processes.Count > 0 ? det.Processes[0].Pid : (int?)null;
                    TryEnqueue(RemoteSoftwareEvents.RemoteToolDetected(display, det.By, pid, when));
                }

                if (options.BlockingEnabled && matcher.IsBlocked(display) && det.Processes.Count > 0)
                {
                    var result = SafeBlock(display, det.Processes);
                    if (isNew && result.Attempted > 0)
                    {
                        TryAddMetric(RemoteSoftwareMetrics.RemoteBlocked, display, when);
                        TryEnqueue(RemoteSoftwareEvents.RemoteBlocked(display, blocker.IsDryRun, result.Attempted, when));
                        if (!blocker.IsDryRun)
                        {
                            // Tell the member why the tool just closed (only when it was really blocked).
                            TryNotifyMember(
                                "A remote-access tool was blocked",
                                $"{display} was blocked by your company's policy on this PC.",
                                EventSeverity.Warning, when);
                        }
                    }
                }
            }

            _present.Clear();
            foreach (var display in detections.Keys)
            {
                _present.Add(display);
            }
        }
        return appeared;
    }

    private RemoteBlockResult SafeBlock(string display, IReadOnlyList<RunningProcess> targets)
    {
        try
        {
            return blocker.Block(display, targets);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Blocking {Tool} failed", display);
            return new RemoteBlockResult(0, 0);
        }
    }

    private void TryNotifyMember(string title, string body, string severity, DateTimeOffset when)
    {
        try
        {
            memberNotifier.Notify(title, body, severity, when);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not notify the member about a remote-tool block");
        }
    }

    private void TryAddMetric(string category, string key, DateTimeOffset when)
    {
        try
        {
            metrics.Add(category, key, count: 1, bytes: 0, seconds: 0, when);
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
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            logger.LogError(e, "Could not record a remote-software event");
        }
    }

    private sealed class Detection(RemoteDetection by)
    {
        public RemoteDetection By { get; set; } = by;

        public List<RunningProcess> Processes { get; } = [];
    }
}
