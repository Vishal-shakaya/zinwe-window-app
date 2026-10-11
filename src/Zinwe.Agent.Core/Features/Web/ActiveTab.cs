using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>
/// Reads the raw address-bar text of the foreground browser. The service implements this with UI Automation;
/// tests use a fake. Consent-first: callers turn the result into a DOMAIN only (see <see cref="OmniboxDomain"/>),
/// never storing the full URL or query string.
/// </summary>
public interface IActiveTabSource
{
    /// <summary>The foreground browser's address-bar text, or null when it can't be read (not a browser, no UI, denied).</summary>
    string? ReadAddressText();
}

/// <summary>Default used in Core/tests and whenever no UI can be read (e.g. a session-0 service): records no domains.</summary>
public sealed class NullActiveTabSource : IActiveTabSource
{
    public string? ReadAddressText() => null;
}

/// <summary>The browser processes whose address bar we try to read. Matched case-insensitively, no ".exe".</summary>
public static class BrowserApps
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "chromium", "arc",
    };

    public static bool IsBrowser(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && Names.Contains(processName.Trim());
}

/// <summary>
/// Turns raw address-bar text into a bare domain, or null when it isn't a site to record. This is the whole of
/// what internet-usage tracking keeps about a page: the host, e.g. "youtube.com" — never the path, query or a
/// typed search. Pure and unit-tested; the UI reading that feeds it lives in the service.
/// </summary>
public static class OmniboxDomain
{
    public static string? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var text = raw.Trim();

        // A space means the omnibox is showing a typed search, not a navigated site — don't record it.
        if (text.Contains(' '))
        {
            return null;
        }

        // Give Uri a scheme to parse against; the omnibox often omits it.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }
        // Only ordinary web/loopback pages; skip chrome://, about:, file:, data:, etc.
        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var host = uri.Host;
        // localhost is the one dot-less host we keep (dev/intranet); NormalizeDomain would drop it.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "localhost";
        }
        // A real host has a dot (reject "newtab", "settings", other single words).
        if (!host.Contains('.'))
        {
            return null;
        }

        return Blocklist.NormalizeDomain(host);
    }
}

/// <summary>
/// Accumulates foreground-browser time per DOMAIN into <see cref="MetricsStore"/> under
/// <see cref="WebMetrics.WebDomain"/>. The caller only invokes this when a browser is in the foreground.
/// Never throws: a read or storage failure is logged and skipped.
/// </summary>
public sealed class BrowserDomainSampler(
    IActiveTabSource tabs,
    MetricsStore metrics,
    ILogger<BrowserDomainSampler> logger)
{
    /// <summary>
    /// Reads the foreground browser's domain and credits <paramref name="seconds"/> to it. Returns the domain
    /// recorded, or null when there was nothing to record (not a site, couldn't read).
    /// </summary>
    public string? Collect(long seconds, DateTimeOffset? at = null)
    {
        if (seconds <= 0)
        {
            return null;
        }
        try
        {
            var domain = OmniboxDomain.Parse(tabs.ReadAddressText());
            if (domain is null)
            {
                return null;
            }
            metrics.Add(WebMetrics.WebDomain, domain, count: 1, seconds: seconds, at: at);
            return domain;
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(e, "Could not record website usage");
            return null;
        }
    }
}
