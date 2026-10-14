using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Metrics;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

public sealed class ConsentGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "zinwe-gate-data-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));

    private void Agree(string version = ConsentReader.CurrentVersion)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "consent.json"),
            $$"""{"Version":"{{version}}","AcceptedAt":"2026-10-15T09:30:00+05:30"}""");
    }

    private ConsentGate Gate() => new(new ConsentReader(_dir), _clock);

    [Fact]
    public void No_consent_means_not_granted()
    {
        var gate = Gate();
        Assert.False(gate.IsGranted());
        Assert.Null(gate.Current());
    }

    [Fact]
    public void Consent_to_the_current_notice_grants()
    {
        Agree();
        var gate = Gate();
        Assert.True(gate.IsGranted());
        Assert.Equal(ConsentReader.CurrentVersion, gate.Current()!.Version);
    }

    [Fact]
    public void Consent_to_an_older_notice_does_not_grant_but_is_still_reported()
    {
        Agree("2000-01-01");
        var gate = Gate();
        Assert.False(gate.IsGranted());
        Assert.Equal("2000-01-01", gate.Current()!.Version);
    }

    [Fact]
    public void Answer_is_cached_until_the_recheck_interval()
    {
        var gate = Gate();
        Assert.False(gate.IsGranted());
        Agree();
        Assert.False(gate.IsGranted()); // still the cached "no"
        _clock.Advance(ConsentGate.RecheckInterval);
        Assert.True(gate.IsGranted());
    }

    [Fact]
    public void Refresh_rereads_at_once()
    {
        var gate = Gate();
        Assert.False(gate.IsGranted());
        Agree();
        gate.Refresh();
        Assert.True(gate.IsGranted());
    }

    [Fact]
    public void Event_queue_records_nothing_until_consent()
    {
        var gate = Gate();
        var queue = new EventQueue(_data, recordingAllowed: gate.IsGranted);
        var raised = 0;
        queue.Enqueued += (_, _) => raised++;

        queue.Enqueue(AgentEvents.UsbPolicyApplied(UsbMode.Allow, dryRun: false));
        Assert.Equal(0, queue.Count());
        Assert.Equal(0, raised);

        Agree();
        gate.Refresh();
        queue.Enqueue(AgentEvents.UsbPolicyApplied(UsbMode.Allow, dryRun: false));
        Assert.Equal(1, queue.Count());
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Metrics_store_records_nothing_until_consent()
    {
        var gate = Gate();
        var store = new MetricsStore(_data, recordingAllowed: gate.IsGranted);

        store.Add("app", "notepad.exe", seconds: 60);
        Assert.Equal(0, store.Count());

        Agree();
        gate.Refresh();
        store.Add("app", "notepad.exe", seconds: 60);
        Assert.Equal(1, store.Count());
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _dir, _data })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
