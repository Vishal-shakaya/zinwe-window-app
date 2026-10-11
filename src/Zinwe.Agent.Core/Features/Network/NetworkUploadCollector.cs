using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.Network;

/// <summary>
/// Correlates DNS answers with per-connection SEND bytes to attribute UPLOAD volume to a destination DOMAIN, and
/// rolls it into <see cref="MetricsStore"/> under <see cref="NetworkMetrics.Upload"/> (key = host). Pure of any OS
/// dependency (the ETW plumbing lives in the service), so it is fully unit-testable with fake samples.
///
/// Consent-first: only byte COUNTS per host are kept — never a URL, a payload, or anything typed. An upload whose
/// remote IP has no known host (never resolved) is skipped, so only named sites are recorded. Never throws.
/// </summary>
public sealed class NetworkUploadCollector(
    MetricsStore metrics, EventQueue events, NetworkFeatureOptions options, ILogger<NetworkUploadCollector> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _ipToHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _ipOrder = new(); // FIFO eviction to honour MaxTrackedHosts
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase); // host → bytes this window
    private readonly Dictionary<string, long> _sessionTotal = new(StringComparer.OrdinalIgnoreCase); // host → bytes since start
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember which host an IP belongs to (latest DNS answer wins).</summary>
    public void OnDnsAnswer(DnsAnswer answer)
    {
        var host = Normalize(answer.Host);
        if (host is null || string.IsNullOrWhiteSpace(answer.IpAddress))
        {
            return;
        }
        lock (_lock)
        {
            if (!_ipToHost.ContainsKey(answer.IpAddress))
            {
                _ipOrder.Enqueue(answer.IpAddress);
                while (_ipOrder.Count > Math.Max(1, options.MaxTrackedHosts))
                {
                    _ipToHost.Remove(_ipOrder.Dequeue());
                }
            }
            _ipToHost[answer.IpAddress] = host;
        }
    }

    /// <summary>Credit a tracked app's upload bytes to the host its remote IP resolved to (unknown IP → skipped).</summary>
    public void OnUpload(UploadSample sample)
    {
        if (sample.BytesSent <= 0 || !IsTrackedApp(sample.ProcessName))
        {
            return;
        }
        lock (_lock)
        {
            if (!_ipToHost.TryGetValue(sample.RemoteIpAddress, out var host))
            {
                return; // only named destinations are recorded
            }
            _pending[host] = _pending.GetValueOrDefault(host) + sample.BytesSent;
        }
    }

    /// <summary>Write accumulated per-host upload bytes to the metrics store and raise any over-limit alerts.</summary>
    public void Flush(DateTimeOffset? at = null)
    {
        List<KeyValuePair<string, long>> ready;
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            ready = [.. _pending];
            _pending.Clear();
        }

        var when = at ?? DateTimeOffset.Now;
        foreach (var (host, bytes) in ready)
        {
            TryAddMetric(host, bytes, when);

            // Advance the running total and alert once when a host first crosses the limit.
            if (options.WarnBytes > 0)
            {
                long total;
                bool fire;
                lock (_lock)
                {
                    total = _sessionTotal[host] = _sessionTotal.GetValueOrDefault(host) + bytes;
                    fire = total >= options.WarnBytes && _alerted.Add(host);
                }
                if (fire)
                {
                    TryEnqueue(UploadOverLimitEvent(host, total, options.WarnBytes, when));
                }
            }
        }
    }

    private void TryAddMetric(string host, long bytes, DateTimeOffset when)
    {
        try
        {
            metrics.Add(NetworkMetrics.Upload, host, count: 1, bytes: bytes, seconds: 0, at: when);
        }
        catch (Exception e) when (e is SqliteException or IOException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(e, "Could not record an upload metric for {Host}", host);
        }
    }

    private void TryEnqueue(AgentEvent evt)
    {
        try
        {
            events.Enqueue(evt);
        }
        catch (Exception e) when (e is SqliteException or IOException or ArgumentException)
        {
            logger.LogError(e, "Could not record an upload-over-limit event");
        }
    }

    private bool IsTrackedApp(string? process)
    {
        if (string.IsNullOrWhiteSpace(process))
        {
            return false;
        }
        var allow = options.Apps.Count > 0 ? (IReadOnlyCollection<string>)options.Apps : NetworkApps.Default;
        return allow.Contains(process.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.Length == 0 || !h.Contains('.') ? null : h;
    }

    private static AgentEvent UploadOverLimitEvent(string host, long totalBytes, long limitBytes, DateTimeOffset when)
    {
        var data = new JsonObject
        {
            ["summary"] = $"Large upload to {host}: over the {HumanSize(limitBytes)} limit",
            ["host"] = host,
            ["total_bytes"] = totalBytes,
            ["limit_bytes"] = limitBytes,
        };
        return AgentEvent.Create(NetworkEventTypes.UploadOverLimit, EventSeverity.Warning, data, when);
    }

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{(unit == 0 ? value : Math.Round(value, 1))} {units[unit]}";
    }
}
