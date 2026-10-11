using System.Net;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

public sealed class BackendPolicyAndEventsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 10, 0, 0, 123, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData("allow", UsbMode.Allow)]
    [InlineData("read_only", UsbMode.ReadOnly)]
    [InlineData("block", UsbMode.Block)]
    public async Task GetPolicy_uses_device_auth_and_parses_snake_case_modes(string wire, UsbMode expected)
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $$$"""{"version": 3, "usb": {"mode": "{{{wire}}}"}}""") };

        var policy = await backend.Client().GetPolicyAsync("tok", CancellationToken.None);

        Assert.Equal(new AgentPolicy(3, new UsbPolicy(expected)), policy);
        var seen = Assert.Single(backend.Requests);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.Equal("/api/devices/policy/current/", seen.Path);
        Assert.Equal(("Device", "tok"), (seen.AuthScheme, seen.AuthParameter));
    }

    [Fact]
    public async Task GetPolicy_401_means_device_rejected()
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.Unauthorized, """{"detail": "Device has been removed."}""") };

        var e = await Assert.ThrowsAsync<DeviceRejectedException>(() => backend.Client().GetPolicyAsync("tok", CancellationToken.None));

        Assert.Equal("Device has been removed.", e.Message);
    }

    [Theory]
    [InlineData("""{"version": 3, "usb": {"mode": "explode"}}""")]
    [InlineData("""{"version": 3, "usb": {"mode": 7}}""")]
    [InlineData("""{"version": 3}""")]
    [InlineData("<html>proxy</html>")]
    public async Task GetPolicy_malformed_response_is_a_retryable_error_never_applied(string body)
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.OK, body) };

        await Assert.ThrowsAsync<HttpRequestException>(() => backend.Client().GetPolicyAsync("tok", CancellationToken.None));
    }

    [Fact]
    public async Task UploadEvents_sends_a_buffered_snake_case_body_with_data_as_an_object()
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"accepted": 2, "duplicates": 0}""") };
        var events = new[]
        {
            AgentEvents.UsbConnected(new UsbDeviceArrival("SanDisk Ultra", "E:", "SANDISK", "4C53"), At),
            AgentEvents.UsbPolicyApplied(UsbMode.ReadOnly, dryRun: true, At),
        };

        var result = await backend.Client().UploadEventsAsync("tok", events, CancellationToken.None);

        Assert.Equal(new EventUploadResponse(2, 0), result);
        var seen = Assert.Single(backend.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/api/devices/events/upload/", seen.Path);
        Assert.Equal(("Device", "tok"), (seen.AuthScheme, seen.AuthParameter));
        // Regression: a chunked body (no Content-Length) reaches Django's WSGI server as empty.
        Assert.Equal(seen.Body.Length, seen.ContentLength);

        using var doc = JsonDocument.Parse(seen.Body);
        var first = doc.RootElement.GetProperty("events")[0];
        Assert.Equal(events[0].Id, first.GetProperty("id").GetString());
        Assert.Equal("usb.connected", first.GetProperty("type").GetString());
        Assert.Equal("info", first.GetProperty("severity").GetString());
        Assert.Equal("2026-10-09T10:00:00.123+05:30", first.GetProperty("occurred_at").GetString());
        var data = first.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal("SanDisk Ultra", data.GetProperty("device_name").GetString());
        Assert.Equal("E:", data.GetProperty("drive_letter").GetString());

        var second = doc.RootElement.GetProperty("events")[1].GetProperty("data");
        Assert.Equal("read_only", second.GetProperty("mode").GetString());
        Assert.True(second.GetProperty("dry_run").GetBoolean());
    }

    [Fact]
    public async Task UploadEvents_401_means_device_rejected()
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.Unauthorized, """{"detail": "Invalid device token."}""") };

        await Assert.ThrowsAsync<DeviceRejectedException>(() => backend.Client().UploadEventsAsync(
            "tok", [AgentEvents.UsbPolicyApplied(UsbMode.Allow, false)], CancellationToken.None));
    }

    [Fact]
    public async Task UploadEvents_400_is_a_content_rejection()
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.BadRequest, """{"events": ["Ensure this field has no more than 100 elements."]}""") };

        var e = await Assert.ThrowsAsync<EventsRejectedException>(() => backend.Client().UploadEventsAsync(
            "tok", [AgentEvents.UsbPolicyApplied(UsbMode.Allow, false)], CancellationToken.None));

        Assert.Contains("events", e.Message);
    }

    // Real DRF 3.18 shapes for a refused batch (captured from the backend's EventUploadSerializer).
    [Theory]
    [InlineData("""{"events": {"1": {"severity": ["\"nope\" is not a valid choice."]}}}""", "\"nope\" is not a valid choice.")]
    [InlineData("""{"events": {"non_field_errors": ["This list may not be empty."]}}""", "This list may not be empty.")]
    [InlineData("""{"events": [{}, {"type": ["Use dotted lowercase names like usb.connected."]}]}""", "Use dotted lowercase names like usb.connected.")]
    [InlineData("""{"events": [7, null]}""", "The backend refused the request (400).")]
    public async Task UploadEvents_400_reads_nested_drf_errors_without_throwing(string body, string expectedMessage)
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.BadRequest, body) };

        var e = await Assert.ThrowsAsync<EventsRejectedException>(() => backend.Client().UploadEventsAsync(
            "tok", [AgentEvents.UsbPolicyApplied(UsbMode.Allow, false)], CancellationToken.None));

        Assert.Contains(expectedMessage, e.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task UploadEvents_enforces_the_batch_size_before_sending(int count)
    {
        var backend = new FakeBackend();
        var events = Enumerable.Range(0, count).Select(_ => AgentEvents.UsbPolicyApplied(UsbMode.Allow, false)).ToArray();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.Client().UploadEventsAsync("tok", events, CancellationToken.None));
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task Heartbeat_carries_the_policy_version()
    {
        var backend = new FakeBackend
        {
            Respond = _ => FakeBackend.Json(HttpStatusCode.OK,
                """{"device_id": 7, "server_time": "2026-10-09T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 4}"""),
        };

        var beat = await backend.Client().HeartbeatAsync("tok", new HeartbeatRequest("PC", "Win", "0.1.0", "working", 0, 0), CancellationToken.None);

        Assert.Equal(4, beat.PolicyVersion);
    }
}
