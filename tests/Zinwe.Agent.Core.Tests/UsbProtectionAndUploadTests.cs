using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

public sealed class UsbProtectionAndUploadTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly EventQueue _queue;

    public UsbProtectionAndUploadTests() => _queue = new EventQueue(_dir.Path);

    // ---- UsbProtection -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(UsbMode.Allow, EventTypes.UsbConnected, "info")]
    [InlineData(UsbMode.ReadOnly, EventTypes.UsbConnected, "info")]
    [InlineData(UsbMode.Block, EventTypes.UsbBlocked, "warning")]
    public void Arrival_becomes_connected_or_blocked_depending_on_the_mode(UsbMode mode, string type, string severity)
    {
        var usb = Usb(new FakeEnforcer());
        usb.Apply(mode, report: false);

        usb.OnDeviceArrived(new UsbDeviceArrival("SanDisk Ultra", "E:", "SANDISK", "123"));

        var evt = Assert.Single(_queue.PeekBatch(10));
        Assert.Equal(type, evt.Type);
        Assert.Equal(severity, evt.Severity);
        Assert.Equal("SanDisk Ultra", JsonNode.Parse(evt.DataJson)!["device_name"]!.GetValue<string>());
    }

    [Fact]
    public void Blocked_event_in_test_mode_says_so()
    {
        var usb = Usb(new FakeEnforcer(dryRun: true));
        usb.Apply(UsbMode.Block, report: false);

        usb.OnDeviceArrived(new UsbDeviceArrival("SanDisk Ultra"));

        Assert.True(JsonNode.Parse(_queue.PeekBatch(1)[0].DataJson)!["dry_run"]!.GetValue<bool>());
    }

    [Fact]
    public void Arrival_before_any_policy_is_reported_as_connected()
    {
        Usb(new FakeEnforcer()).OnDeviceArrived(new UsbDeviceArrival("Stick"));

        Assert.Equal(EventTypes.UsbConnected, _queue.PeekBatch(1)[0].Type);
    }

    [Fact]
    public void Enforcer_failure_is_contained_and_reported_as_not_applied()
    {
        var usb = Usb(new FakeEnforcer { FailWith = new UnauthorizedAccessException("denied") });

        Assert.False(usb.Apply(UsbMode.Block, report: true));
        Assert.Null(usb.CurrentMode);
        Assert.Equal(0, _queue.Count());
    }

    [Fact]
    public void Applied_event_carries_mode_and_real_enforcement_flag()
    {
        var usb = Usb(new FakeEnforcer(dryRun: false));

        Assert.True(usb.Apply(UsbMode.ReadOnly, report: true));

        var data = JsonNode.Parse(_queue.PeekBatch(1)[0].DataJson)!;
        Assert.Equal("read_only", data["mode"]!.GetValue<string>());
        Assert.False(data["dry_run"]!.GetValue<bool>());
    }

    // ---- EventUploadPump -----------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_sends_batches_of_100_and_deletes_only_after_2xx()
    {
        Fill(250);
        var backend = new FakeBackend { Respond = Accept };

        var report = await Pump(backend).UploadPendingAsync("tok", CancellationToken.None);

        Assert.Equal(new UploadReport(UploadOutcome.Done, 250, 0), report);
        Assert.Equal([100, 100, 50], backend.Requests.Select(CountEvents));
        Assert.Equal(0, _queue.Count());
    }

    [Fact]
    public async Task Network_failure_keeps_everything_queued()
    {
        Fill(3);
        var backend = new FakeBackend { Respond = _ => throw new HttpRequestException("offline") };

        var report = await Pump(backend).UploadPendingAsync("tok", CancellationToken.None);

        Assert.Equal(UploadOutcome.NetworkError, report.Outcome);
        Assert.Equal(3, _queue.Count());
    }

    [Fact]
    public async Task Server_error_keeps_everything_queued()
    {
        Fill(3);
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.ServiceUnavailable, "{}") };

        Assert.Equal(UploadOutcome.NetworkError, (await Pump(backend).UploadPendingAsync("tok", CancellationToken.None)).Outcome);
        Assert.Equal(3, _queue.Count());
    }

    [Fact]
    public async Task Unauthorized_stops_and_keeps_events()
    {
        Fill(3);
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.Unauthorized, """{"detail": "removed"}""") };

        var report = await Pump(backend).UploadPendingAsync("tok", CancellationToken.None);

        Assert.Equal(UploadOutcome.DeviceRejected, report.Outcome);
        Assert.Single(backend.Requests);
        Assert.Equal(3, _queue.Count());
    }

    [Fact]
    public async Task A_refused_batch_is_retried_one_by_one_and_only_the_bad_event_is_dropped()
    {
        var events = Fill(3);
        var bad = events[1].Id;
        var backend = new FakeBackend
        {
            Respond = r => r.Body.Contains(bad, StringComparison.Ordinal)
                ? FakeBackend.Json(HttpStatusCode.BadRequest, """{"events": ["bad event"]}""")
                : Accept(r),
        };

        var report = await Pump(backend).UploadPendingAsync("tok", CancellationToken.None);

        Assert.Equal(new UploadReport(UploadOutcome.Done, 2, 1), report);
        Assert.Equal([3, 1, 1, 1], backend.Requests.Select(CountEvents));
        Assert.Equal(0, _queue.Count());
    }

    [Fact]
    public async Task Empty_queue_makes_no_request()
    {
        var backend = new FakeBackend();

        Assert.Equal(new UploadReport(UploadOutcome.Done, 0, 0), await Pump(backend).UploadPendingAsync("tok", CancellationToken.None));
        Assert.Empty(backend.Requests);
    }

    private UsbProtection Usb(FakeEnforcer enforcer) => new(enforcer, _queue, NullLogger<UsbProtection>.Instance);

    private EventUploadPump Pump(FakeBackend backend) => new(backend.Client(), _queue, NullLogger<EventUploadPump>.Instance);

    private List<AgentEvent> Fill(int count)
    {
        var events = Enumerable.Range(0, count).Select(_ => AgentEvents.UsbPolicyApplied(UsbMode.Allow, false)).ToList();
        events.ForEach(e => _queue.Enqueue(e));
        return events;
    }

    private static HttpResponseMessage Accept(SeenRequest r) =>
        FakeBackend.Json(HttpStatusCode.OK, $$"""{"accepted": {{CountEvents(r)}}, "duplicates": 0}""");

    private static int CountEvents(SeenRequest r)
    {
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.GetProperty("events").GetArrayLength();
    }

    public void Dispose() => _dir.Dispose();
}
