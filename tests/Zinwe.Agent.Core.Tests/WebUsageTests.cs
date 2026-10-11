using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Tests;

internal sealed class FakeForegroundWindowSource : IForegroundWindowSource
{
    public ForegroundSample? Next { get; set; }
    public Func<ForegroundSample?>? OnSample { get; set; }
    public int Calls { get; private set; }

    public ForegroundSample? Sample()
    {
        Calls++;
        return OnSample is not null ? OnSample() : Next;
    }
}

internal sealed class FakeBlockedAttemptSource : IBlockedAttemptSource
{
    public int Starts { get; private set; }
    public int Disposes { get; private set; }

    public event EventHandler<BlockedAttempt>? Attempted;

    public void Start() => Starts++;

    public void Raise(string domain, string? category = null) => Attempted?.Invoke(this, new BlockedAttempt(domain, category));

    public void Dispose() => Disposes++;
}

internal sealed class FakeActiveTabSource : IActiveTabSource
{
    public string? Text { get; set; }
    public string? ReadAddressText() => Text;
}

public sealed class WebUsageTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 9, 14, 20, 0, TimeSpan.Zero);

    // ---- OmniboxDomain.Parse ----

    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData("https://www.youtube.com/watch?v=abc", "youtube.com")]
    [InlineData("http://GitHub.com/foo/bar", "github.com")]
    [InlineData("https://docs.google.com/", "docs.google.com")]
    [InlineData("localhost:5173/app", "localhost")]
    public void Parse_keeps_host_only(string raw, string expected)
    {
        Assert.Equal(expected, OmniboxDomain.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("how to reset windows")]   // a typed search (has spaces)
    [InlineData("newtab")]                   // single word, no dot
    [InlineData("settings")]
    [InlineData("chrome://settings")]        // not an http(s) page
    [InlineData("about:blank")]
    [InlineData("file:///C:/secret.txt")]
    public void Parse_rejects_non_sites(string? raw)
    {
        Assert.Null(OmniboxDomain.Parse(raw));
    }

    // ---- BrowserApps ----

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("MsEdge", true)]
    [InlineData("firefox", true)]
    [InlineData("winword", false)]
    [InlineData("explorer", false)]
    [InlineData("", false)]
    public void IsBrowser_matches_known_browsers(string name, bool expected)
    {
        Assert.Equal(expected, BrowserApps.IsBrowser(name));
    }

    // ---- BrowserDomainSampler ----

    [Fact]
    public void DomainSampler_credits_time_and_visits_to_the_domain()
    {
        using var dir = new TempDir();
        var metrics = new MetricsStore(dir.Path);
        var tabs = new FakeActiveTabSource { Text = "https://youtube.com/watch?v=abc" };
        var sampler = new BrowserDomainSampler(tabs, metrics, NullLogger<BrowserDomainSampler>.Instance);

        Assert.Equal("youtube.com", sampler.Collect(15, at: T));
        sampler.Collect(15, at: T.AddMinutes(1));

        var row = Assert.Single(metrics.Snapshot());
        Assert.Equal(WebMetrics.WebDomain, row.Category);
        Assert.Equal("youtube.com", row.Key);
        Assert.Equal(2, row.Count);
        Assert.Equal(30, row.Seconds);
    }

    [Fact]
    public void DomainSampler_records_nothing_for_a_search_or_unreadable_bar()
    {
        using var dir = new TempDir();
        var metrics = new MetricsStore(dir.Path);
        var tabs = new FakeActiveTabSource { Text = "how to reset windows" };
        var sampler = new BrowserDomainSampler(tabs, metrics, NullLogger<BrowserDomainSampler>.Instance);

        Assert.Null(sampler.Collect(15, at: T));
        tabs.Text = null;  // address bar couldn't be read
        Assert.Null(sampler.Collect(15, at: T));
        Assert.Empty(metrics.Snapshot());
    }

    // ---- ForegroundUsageSampler ----

    [Fact]
    public void Collect_credits_seconds_to_the_foreground_process()
    {
        using var dir = new TempDir();
        var metrics = new MetricsStore(dir.Path);
        var source = new FakeForegroundWindowSource { Next = new ForegroundSample("chrome") };
        var sampler = new ForegroundUsageSampler(source, metrics, NullLogger<ForegroundUsageSampler>.Instance);

        sampler.Collect(15, at: T);
        sampler.Collect(15, at: T.AddMinutes(1));

        var row = Assert.Single(metrics.Snapshot());
        Assert.Equal(WebMetrics.ForegroundApp, row.Category);
        Assert.Equal("chrome", row.Key);
        Assert.Equal(2, row.Count);
        Assert.Equal(30, row.Seconds);
    }

    [Fact]
    public void Collect_separates_different_apps()
    {
        using var dir = new TempDir();
        var metrics = new MetricsStore(dir.Path);
        var source = new FakeForegroundWindowSource();
        var sampler = new ForegroundUsageSampler(source, metrics, NullLogger<ForegroundUsageSampler>.Instance);

        source.Next = new ForegroundSample("chrome");
        sampler.Collect(15, at: T);
        source.Next = new ForegroundSample("winword");
        sampler.Collect(15, at: T);

        Assert.Equal(2, metrics.Snapshot().Count);
    }

    [Fact]
    public void Collect_records_nothing_when_there_is_no_foreground_sample()
    {
        using var dir = new TempDir();
        var metrics = new MetricsStore(dir.Path);
        var source = new FakeForegroundWindowSource { Next = null };
        var sampler = new ForegroundUsageSampler(source, metrics, NullLogger<ForegroundUsageSampler>.Instance);

        Assert.Null(sampler.Collect(15, at: T));
        Assert.Equal(0, metrics.Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Collect_ignores_non_positive_durations(long seconds)
    {
        using var dir = new TempDir();
        var metrics = new MetricsStore(dir.Path);
        var source = new FakeForegroundWindowSource { Next = new ForegroundSample("chrome") };
        var sampler = new ForegroundUsageSampler(source, metrics, NullLogger<ForegroundUsageSampler>.Instance);

        Assert.Null(sampler.Collect(seconds, at: T));
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, metrics.Count());
    }

    [Fact]
    public void Collect_returns_the_sample_including_a_title_when_present()
    {
        using var dir = new TempDir();
        var metrics = new MetricsStore(dir.Path);
        var source = new FakeForegroundWindowSource { Next = new ForegroundSample("chrome", "Budget - Excel") };
        var sampler = new ForegroundUsageSampler(source, metrics, NullLogger<ForegroundUsageSampler>.Instance);

        var sample = sampler.Collect(15, at: T);

        Assert.Equal("chrome", sample!.ProcessName);
        Assert.Equal("Budget - Excel", sample.WindowTitle);
    }

    // ---- Blocklist ----

    [Fact]
    public void FromOptions_builds_built_in_sample_and_custom_domains()
    {
        var options = new WebFeatureOptions
        {
            UseBuiltInSampleList = true,
            CustomDomains = ["https://Bad-Site.example/login?x=1", "bad-site.example"],
        };

        var list = Blocklist.FromOptions(options);

        Assert.Contains(list.Rules, r => r is { Domain: "malware-sample.test", Category: "malware" });
        // Scheme/path stripped and deduped to one custom rule.
        Assert.Single(list.Rules, r => r.Domain == "bad-site.example");
        Assert.Contains("custom", list.Categories);
    }

    [Fact]
    public void FromOptions_honours_a_category_filter()
    {
        var options = new WebFeatureOptions { UseBuiltInSampleList = true, Categories = ["phishing"] };

        var list = Blocklist.FromOptions(options);

        Assert.All(list.Rules, r => Assert.Equal("phishing", r.Category));
    }

    [Fact]
    public void FromOptions_without_the_built_in_list_only_has_custom_domains()
    {
        var options = new WebFeatureOptions { UseBuiltInSampleList = false, CustomDomains = ["a.example"] };

        var list = Blocklist.FromOptions(options);

        Assert.Equal(["custom"], list.Categories);
        Assert.Single(list.Rules);
    }

    [Fact]
    public void Hostnames_include_apex_and_www_and_are_deduped()
    {
        var list = new Blocklist([new BlockRule("a.test", "custom"), new BlockRule("www.a.test", "custom")]);

        Assert.Equal(["a.test", "www.a.test"], list.Hostnames);
    }

    [Theory]
    [InlineData("https://www.Example.com/path?q=1", "example.com")]
    [InlineData("user@Host.Example.COM:8443", "host.example.com")]
    [InlineData("  example.org.  ", "example.org")]
    [InlineData("www.example.net", "example.net")]
    public void NormalizeDomain_reduces_input_to_a_bare_host(string input, string expected)
    {
        Assert.Equal(expected, Blocklist.NormalizeDomain(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("no spaces allowed.com here")]
    [InlineData(null)]
    public void NormalizeDomain_rejects_non_hosts(string? input)
    {
        Assert.Null(Blocklist.NormalizeDomain(input));
    }

    // ---- WebFeatureModule wiring ----

    [Fact]
    public async Task Module_applies_the_blocklist_and_turns_a_blocked_attempt_into_an_event()
    {
        using var dir = new TempDir();
        var events = new EventQueue(dir.Path);
        var metrics = new MetricsStore(dir.Path);
        var protection = new WebProtection(new DryRunWebBlocker(NullLogger<DryRunWebBlocker>.Instance), events, NullLogger<WebProtection>.Instance);
        var sampler = new ForegroundUsageSampler(new FakeForegroundWindowSource(), metrics, NullLogger<ForegroundUsageSampler>.Instance);
        var domains = new BrowserDomainSampler(new NullActiveTabSource(), metrics, NullLogger<BrowserDomainSampler>.Instance);
        var attempts = new FakeBlockedAttemptSource();
        var blocklist = new Blocklist([new BlockRule("a.test", "malware")]);
        var options = new WebFeatureOptions { CollectForegroundUsage = false }; // no background loop during the test

        var module = new WebFeatureModule(protection, sampler, domains, attempts, blocklist, options, NullLogger<WebFeatureModule>.Instance);

        await module.StartAsync(CancellationToken.None);
        Assert.Equal(1, attempts.Starts);

        attempts.Raise("a.test", "malware");

        await module.StopAsync(CancellationToken.None);

        var types = events.PeekBatch(10).Select(e => e.Type).ToList();
        Assert.Contains(WebEventTypes.BlocklistApplied, types);
        Assert.Contains(WebEventTypes.BlockedAttempt, types);
        Assert.Equal(1, attempts.Disposes);
    }

    [Fact]
    public async Task Disabled_module_does_nothing()
    {
        using var dir = new TempDir();
        var events = new EventQueue(dir.Path);
        var metrics = new MetricsStore(dir.Path);
        var protection = new WebProtection(new DryRunWebBlocker(NullLogger<DryRunWebBlocker>.Instance), events, NullLogger<WebProtection>.Instance);
        var sampler = new ForegroundUsageSampler(new FakeForegroundWindowSource(), metrics, NullLogger<ForegroundUsageSampler>.Instance);
        var domains = new BrowserDomainSampler(new NullActiveTabSource(), metrics, NullLogger<BrowserDomainSampler>.Instance);
        var attempts = new FakeBlockedAttemptSource();
        var options = new WebFeatureOptions { Enabled = false };

        var module = new WebFeatureModule(protection, sampler, domains, attempts, new Blocklist([]), options, NullLogger<WebFeatureModule>.Instance);

        await module.StartAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);

        Assert.Equal(0, attempts.Starts);
        Assert.Equal(0, events.Count());
    }
}
