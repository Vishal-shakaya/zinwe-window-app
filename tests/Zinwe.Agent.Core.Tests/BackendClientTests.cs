using System.Net;
using System.Text;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Tests;

public sealed class BackendClientTests
{
    private static readonly EnrollRequest Enroll = new("asha@acme.in", "ZM-7KQ2-9F3M", "machine-1", "PC-01", "Windows 11", "0.1.0");

    [Fact]
    public async Task Enroll_sends_snake_case_and_parses_response()
    {
        var handler = new StubHandler(HttpStatusCode.Created, """{"device_id": 7, "device_token": "tok", "re_enrolled": false}""");
        var client = Create(handler);

        var result = await client.EnrollAsync(Enroll, CancellationToken.None);

        Assert.Equal(new EnrollResponse(7, "tok", false), result);
        Assert.Equal("/api/devices/enroll/", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"asha@acme.in\"", handler.LastBody);
        Assert.Contains("\"member_id\":\"ZM-7KQ2-9F3M\"", handler.LastBody);
        Assert.Contains("\"machine_id\":\"machine-1\"", handler.LastBody);
        Assert.Contains("\"platform\":\"windows\"", handler.LastBody);
        // Regression: a chunked body (no Content-Length) reaches Django's WSGI server as empty.
        Assert.Equal(handler.LastBody.Length, handler.LastContentLength);
    }

    [Fact]
    public async Task Enroll_400_on_another_field_is_a_retryable_error_not_a_refused_credential()
    {
        var client = Create(new StubHandler(HttpStatusCode.BadRequest, """{"machine_id": ["This field is required."]}"""));

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.EnrollAsync(Enroll, CancellationToken.None));

        Assert.Contains("machine_id", e.Message);
    }

    [Theory]
    [InlineData("""{"member_id": ["That member id is not valid."]}""", "That member id is not valid.")]
    [InlineData("""{"email": ["No member with that email."]}""", "No member with that email.")]
    public async Task Enroll_400_on_the_credential_is_a_refusal(string body, string expected)
    {
        var client = Create(new StubHandler(HttpStatusCode.BadRequest, body));

        var e = await Assert.ThrowsAsync<EnrollmentRejectedException>(() => client.EnrollAsync(Enroll, CancellationToken.None));

        Assert.Equal(expected, e.Message);
    }

    [Fact]
    public async Task GetMessages_sends_device_auth_and_parses_snake_case()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"id": 5, "title": "USB blocked", "body": "No copying to USB.", "severity": "warning", "created_at": "2026-10-10T10:00:00+05:30"}]
            """);
        var client = Create(handler);

        var messages = await client.GetMessagesAsync("tok", CancellationToken.None);

        var m = Assert.Single(messages);
        Assert.Equal(5, m.Id);
        Assert.Equal("USB blocked", m.Title);
        Assert.Equal("warning", m.Severity);
        Assert.Equal("/api/devices/messages/", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Device", handler.LastRequest.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Heartbeat_sends_device_auth_and_parses_response()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"device_id": 7, "server_time": "2026-10-09T10:00:00+05:30", "next_heartbeat_seconds": 60, "policy_version": 0}""");
        var client = Create(handler);

        var beat = await client.HeartbeatAsync("tok", new HeartbeatRequest("PC-01", "Windows 11", "0.1.0", "working", 60, 10), CancellationToken.None);

        Assert.Equal(60, beat.NextHeartbeatSeconds);
        Assert.Equal(TimeSpan.FromHours(5.5), beat.ServerTime.Offset);
        Assert.Equal("Device", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("tok", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Heartbeat_401_means_device_rejected()
    {
        var client = Create(new StubHandler(HttpStatusCode.Unauthorized, """{"detail": "Device has been removed."}"""));

        var e = await Assert.ThrowsAsync<DeviceRejectedException>(
            () => client.HeartbeatAsync("tok", new HeartbeatRequest("a", "b", "c", "not_started", 0, 0), CancellationToken.None));

        Assert.Equal("Device has been removed.", e.Message);
    }

    [Fact]
    public async Task Server_errors_are_retryable_http_exceptions()
    {
        var client = Create(new StubHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html>"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.HeartbeatAsync("tok", new HeartbeatRequest("a", "b", "c", "not_started", 0, 0), CancellationToken.None));
    }

    [Theory]
    [InlineData("https://api.zinwe.io/", true)]
    [InlineData("http://127.0.0.1:8000/", true)]
    [InlineData("http://localhost:8000/", true)]
    [InlineData("http://api.zinwe.io/", false)]
    public void Plain_http_is_only_allowed_for_loopback(string url, bool allowed)
    {
        var act = () => BackendClient.EnsureSafeBaseAddress(new Uri(url));
        if (allowed)
        {
            act();
        }
        else
        {
            Assert.Throws<InvalidOperationException>(act);
        }
    }

    private static BackendClient Create(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") });

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";
        public long? LastContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastContentLength = request.Content?.Headers.ContentLength;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
