using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Tests;

public sealed class MetricsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-metrics-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset T = new(2026, 10, 9, 14, 20, 0, TimeSpan.Zero);

    [Fact]
    public void Add_rolls_up_into_the_same_hour_bucket()
    {
        var store = new MetricsStore(_dir);

        store.Add("web.domain", "news.example", count: 1, seconds: 30, at: T);
        store.Add("web.domain", "news.example", count: 1, seconds: 45, at: T.AddMinutes(20));
        store.Add("web.domain", "news.example", bytes: 2048, at: T.AddMinutes(39));

        var row = Assert.Single(store.Snapshot());
        Assert.Equal(MetricsStore.HourBucket(T), row.BucketStart);
        Assert.Equal("news.example", row.Key);
        Assert.Equal(3, row.Count);
        Assert.Equal(2048, row.Bytes);
        Assert.Equal(75, row.Seconds);
    }

    [Fact]
    public void Different_hours_categories_and_keys_are_separate_rows()
    {
        var store = new MetricsStore(_dir);

        store.Add("web.domain", "a.example", at: T);
        store.Add("web.domain", "b.example", at: T);
        store.Add("web.domain", "a.example", at: T.AddHours(1));
        store.Add("app.foreground", "a.example", at: T);

        Assert.Equal(4, store.Count());
    }

    [Fact]
    public void DeleteThrough_removes_whole_buckets_up_to_and_including_the_hour()
    {
        var store = new MetricsStore(_dir);
        store.Add("web.domain", "a.example", at: T);
        store.Add("web.domain", "a.example", at: T.AddHours(1));
        store.Add("web.domain", "a.example", at: T.AddHours(2));

        var removed = store.DeleteThrough(T.AddHours(1)); // any time within the 2nd hour

        Assert.Equal(2, removed);
        var row = Assert.Single(store.Snapshot());
        Assert.Equal(MetricsStore.HourBucket(T.AddHours(2)), row.BucketStart);
    }

    [Fact]
    public void Oldest_buckets_are_evicted_past_capacity()
    {
        var store = new MetricsStore(_dir, capacity: 2);

        Assert.Equal(0, store.Add("c", "k", at: T));
        Assert.Equal(0, store.Add("c", "k", at: T.AddHours(1)));
        var dropped = store.Add("c", "k", at: T.AddHours(2)); // 3rd bucket evicts the 1st

        Assert.Equal(1, dropped);
        Assert.Equal(2, store.Count());
        Assert.DoesNotContain(store.Snapshot(), r => r.BucketStart == MetricsStore.HourBucket(T));
    }

    [Fact]
    public void Survives_reopening_the_same_file()
    {
        new MetricsStore(_dir).Add("c", "k", count: 5, at: T);

        var reopened = new MetricsStore(_dir);

        Assert.Equal(5, Assert.Single(reopened.Snapshot()).Count);
    }

    [Fact]
    public void Blank_category_or_key_is_rejected()
    {
        var store = new MetricsStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Add(" ", "k"));
        Assert.Throws<ArgumentException>(() => store.Add("c", ""));
    }

    [Fact]
    public void Over_long_key_is_clipped()
    {
        var store = new MetricsStore(_dir);
        store.Add("c", new string('x', MetricsStore.MaxKeyLength + 50), at: T);
        Assert.Equal(MetricsStore.MaxKeyLength, Assert.Single(store.Snapshot()).Key.Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
