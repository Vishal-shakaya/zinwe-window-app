using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.Network;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Tests;

public sealed class NetworkUploadTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static NetworkUploadCollector Build(TempDir dir, NetworkFeatureOptions options, out MetricsStore metrics, out EventQueue events)
    {
        metrics = new MetricsStore(dir.Path);
        events = new EventQueue(dir.Path);
        return new NetworkUploadCollector(metrics, events, options, NullLogger<NetworkUploadCollector>.Instance);
    }

    [Fact]
    public void Upload_from_tracked_app_to_known_host_rolls_up_by_domain()
    {
        using var dir = new TempDir();
        var c = Build(dir, new NetworkFeatureOptions(), out var metrics, out _);

        c.OnDnsAnswer(new DnsAnswer("drive.google.com", "142.250.1.10"));
        c.OnUpload(new UploadSample("chrome", "142.250.1.10", 5_000_000));
        c.OnUpload(new UploadSample("chrome", "142.250.1.10", 3_000_000));
        c.Flush(T);

        var row = Assert.Single(metrics.Snapshot());
        Assert.Equal(NetworkMetrics.Upload, row.Category);
        Assert.Equal("drive.google.com", row.Key);
        Assert.Equal(8_000_000, row.Bytes);
    }

    [Fact]
    public void Upload_to_unknown_ip_is_skipped()
    {
        using var dir = new TempDir();
        var c = Build(dir, new NetworkFeatureOptions(), out var metrics, out _);

        c.OnUpload(new UploadSample("chrome", "9.9.9.9", 1_000_000)); // never resolved
        c.Flush(T);

        Assert.Empty(metrics.Snapshot());
    }

    [Fact]
    public void Upload_from_untracked_app_is_skipped()
    {
        using var dir = new TempDir();
        var c = Build(dir, new NetworkFeatureOptions(), out var metrics, out _);

        c.OnDnsAnswer(new DnsAnswer("example.com", "1.2.3.4"));
        c.OnUpload(new UploadSample("winword", "1.2.3.4", 1_000_000)); // not a browser/mail app
        c.Flush(T);

        Assert.Empty(metrics.Snapshot());
    }

    [Fact]
    public void Custom_app_allowlist_overrides_the_default_set()
    {
        using var dir = new TempDir();
        var options = new NetworkFeatureOptions { Apps = ["myapp"] };
        var c = Build(dir, options, out var metrics, out _);

        c.OnDnsAnswer(new DnsAnswer("api.example.com", "1.2.3.4"));
        c.OnUpload(new UploadSample("chrome", "1.2.3.4", 500)); // chrome not in the custom list → skipped
        c.OnUpload(new UploadSample("myapp", "1.2.3.4", 700));
        c.Flush(T);

        var row = Assert.Single(metrics.Snapshot());
        Assert.Equal("api.example.com", row.Key);
        Assert.Equal(700, row.Bytes);
    }

    [Fact]
    public void Over_limit_raises_one_warning_event_per_host()
    {
        using var dir = new TempDir();
        var options = new NetworkFeatureOptions { WarnBytes = 10_000_000 };
        var c = Build(dir, options, out _, out var events);

        c.OnDnsAnswer(new DnsAnswer("upload.box.com", "5.6.7.8"));
        c.OnUpload(new UploadSample("msedge", "5.6.7.8", 6_000_000));
        c.Flush(T);                      // 6 MB total — under the limit, no event
        Assert.Empty(events.PeekBatch(10));

        c.OnUpload(new UploadSample("msedge", "5.6.7.8", 6_000_000));
        c.Flush(T.AddMinutes(1));        // 12 MB total — crosses the limit once
        var evt = Assert.Single(events.PeekBatch(10));
        Assert.Equal(NetworkEventTypes.UploadOverLimit, evt.Type);

        c.OnUpload(new UploadSample("msedge", "5.6.7.8", 6_000_000));
        c.Flush(T.AddMinutes(2));        // still over — but no duplicate alert
        Assert.Single(events.PeekBatch(10));
    }
}
