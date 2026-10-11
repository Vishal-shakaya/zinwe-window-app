using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>
/// The install-monitoring brain, with no OS dependencies so it is fully unit-testable. Diffs a snapshot of the
/// installed-software list against a persisted baseline and emits one <c>software.installed</c> event (info) +
/// one metric per genuinely new product. First run seeds the baseline silently. Never throws: a store failure is
/// logged and dropped.
/// </summary>
public sealed class InstallMonitor(
    MetricsStore metrics, EventQueue events, IInstallBaseline baseline, IMemberNotifier memberNotifier,
    RemoteSoftwareOptions options, ILogger<InstallMonitor> logger)
{
    private readonly Lock _lock = new();

    /// <summary>
    /// Diff <paramref name="installed"/> against the baseline; report new products; update the baseline. Returns
    /// how many new products were reported (0 on the first, seeding scan). Thread-safe.
    /// </summary>
    public int Scan(IReadOnlyList<InstalledProduct> installed, DateTimeOffset? at = null)
    {
        // One product per identity key; keep the first occurrence's display fields.
        var current = new Dictionary<string, InstalledProduct>(StringComparer.Ordinal);
        foreach (var product in installed)
        {
            if (string.IsNullOrWhiteSpace(product.Name))
            {
                continue;
            }
            var key = IdentityKey(product);
            current.TryAdd(key, product);
        }

        lock (_lock)
        {
            var known = baseline.Load();
            if (known is null)
            {
                baseline.Save(new HashSet<string>(current.Keys, StringComparer.Ordinal));
                logger.LogInformation("remote_software: seeded install baseline with {Count} product(s); " +
                                      "only software installed from now on will be reported", current.Count);
                return 0;
            }

            var when = at ?? DateTimeOffset.Now;
            var reported = 0;
            foreach (var (key, product) in current)
            {
                if (known.Contains(key))
                {
                    continue;
                }
                TryAddMetric(RemoteSoftwareMetrics.Installed, product.Name, when);
                var version = options.IncludeDetails ? product.Version : null;
                TryEnqueue(RemoteSoftwareEvents.SoftwareInstalled(product.Name, product.Publisher, version, when));
                TryNotifyMember(product.Name, when);
                reported++;
            }

            if (reported > 0)
            {
                // Union: a product that merely dropped out of one scan (per-user hive hiccup) is not re-reported
                // when it returns; a true re-install after a real removal is the rare, acceptable miss.
                var merged = new HashSet<string>(known, StringComparer.Ordinal);
                merged.UnionWith(current.Keys);
                baseline.Save(merged);
            }
            return reported;
        }
    }

    /// <summary>Opaque per-product key (name + version), lowercased. Internal only — never surfaced to the owner.</summary>
    private static string IdentityKey(InstalledProduct product)
    {
        var name = product.Name.Trim().ToLowerInvariant();
        var version = (product.Version ?? "").Trim().ToLowerInvariant();
        return $"{name}|{version}";
    }

    private void TryNotifyMember(string productName, DateTimeOffset when)
    {
        try
        {
            memberNotifier.Notify(
                "New software detected",
                $"{productName} was installed on this PC. Your company keeps a record of software installs.",
                EventSeverity.Info, when);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not notify the member about a new install");
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
            logger.LogError(e, "Could not record a software.installed event");
        }
    }
}
