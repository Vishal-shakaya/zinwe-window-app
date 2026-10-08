using System.Text.Json.Nodes;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

public sealed class EventQueueTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Events_round_trip_oldest_first_and_survive_a_restart()
    {
        var at = new DateTimeOffset(2026, 10, 9, 10, 0, 0, 5, TimeSpan.FromHours(5.5));
        var first = AgentEvents.UsbConnected(new UsbDeviceArrival("SanDisk Ultra", "E:"), at);
        var second = AgentEvents.UsbPolicyApplied(UsbMode.Block, dryRun: false, at.AddSeconds(1));
        var queue = new EventQueue(_dir.Path);
        queue.Enqueue(first);
        queue.Enqueue(second);

        var reopened = new EventQueue(_dir.Path);

        Assert.Equal(Path.Combine(_dir.Path, "events.db"), reopened.FilePath);
        Assert.Equal([first, second], reopened.PeekBatch(10));
        Assert.Equal(TimeSpan.FromHours(5.5), reopened.PeekBatch(1)[0].OccurredAt.Offset);
    }

    [Fact]
    public void PeekBatch_returns_at_most_max_and_does_not_remove()
    {
        var queue = new EventQueue(_dir.Path);
        var events = Enqueue(queue, 7);

        var batch = queue.PeekBatch(5);

        Assert.Equal(events.Take(5), batch);
        Assert.Equal(7, queue.Count());
    }

    [Fact]
    public void Delete_removes_only_the_given_ids()
    {
        var queue = new EventQueue(_dir.Path);
        var events = Enqueue(queue, 4);

        var removed = queue.Delete([events[0].Id, events[2].Id, Guid.NewGuid().ToString()]);

        Assert.Equal(2, removed);
        Assert.Equal([events[1], events[3]], queue.PeekBatch(10));
    }

    [Fact]
    public void Past_capacity_the_oldest_events_are_dropped_and_reported()
    {
        var queue = new EventQueue(_dir.Path, capacity: 5);
        var events = Enqueue(queue, 5);

        var dropped = new[] { Make(), Make(), Make() }.Select(queue.Enqueue).ToArray();

        Assert.Equal([1, 1, 1], dropped);
        Assert.Equal(5, queue.Count());
        Assert.DoesNotContain(queue.PeekBatch(10), e => events.Take(3).Contains(e));
        Assert.Equal(events[3], queue.PeekBatch(1)[0]);
    }

    [Fact]
    public void Default_capacity_is_ten_thousand()
    {
        Assert.Equal(10_000, new EventQueue(_dir.Path).Capacity);
    }

    [Fact]
    public void A_repeated_id_is_stored_once()
    {
        var queue = new EventQueue(_dir.Path);
        var evt = Make();

        queue.Enqueue(evt);
        queue.Enqueue(evt);

        Assert.Equal(1, queue.Count());
    }

    [Fact]
    public void Enqueued_is_raised_after_the_event_is_stored()
    {
        var queue = new EventQueue(_dir.Path);
        var seen = new List<int>();
        queue.Enqueued += (_, _) => seen.Add(queue.Count());

        queue.Enqueue(Make());

        Assert.Equal([1], seen);
    }

    [Theory]
    [InlineData("usb", "info")]          // needs at least two dotted segments
    [InlineData("USB.Connected", "info")] // lowercase only
    [InlineData("usb.connected", "loud")] // unknown severity
    public void Invalid_events_are_refused_before_they_can_poison_an_upload(string type, string severity)
    {
        var queue = new EventQueue(_dir.Path);
        var evt = new AgentEvent(Guid.NewGuid().ToString(), type, DateTimeOffset.Now, severity, "{}");

        Assert.Throws<ArgumentException>(() => queue.Enqueue(evt));
        Assert.Equal(0, queue.Count());
    }

    [Fact]
    public void Data_over_4096_bytes_is_refused()
    {
        Assert.Throws<ArgumentException>(() => AgentEvent.Create(
            "test.big", EventSeverity.Info, new JsonObject { ["blob"] = new string('x', 4100) }));
    }

    [Fact]
    public void Long_device_names_are_clipped()
    {
        var evt = AgentEvents.UsbConnected(new UsbDeviceArrival(new string('n', 1000)));

        Assert.Equal(200, JsonNode.Parse(evt.DataJson)!["device_name"]!.GetValue<string>().Length);
    }

    private static AgentEvent Make() => AgentEvents.UsbPolicyApplied(UsbMode.Allow, dryRun: false);

    private static List<AgentEvent> Enqueue(EventQueue queue, int count)
    {
        var events = Enumerable.Range(0, count).Select(_ => Make()).ToList();
        events.ForEach(e => queue.Enqueue(e));
        return events;
    }

    public void Dispose() => _dir.Dispose();
}
