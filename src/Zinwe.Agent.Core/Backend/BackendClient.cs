using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Backend;

/// <summary>The enrollment key was refused (unknown, rotated, or the account is disabled).</summary>
public sealed class EnrollmentRejectedException(string message) : Exception(message);

/// <summary>The backend no longer recognises this device's token (removed by the owner, or reinstalled elsewhere).</summary>
public sealed class DeviceRejectedException(string message) : Exception(message);

/// <summary>The backend refused an event batch's content (400). Re-sending the same batch will not help.</summary>
public sealed class EventsRejectedException(string message) : Exception(message);

/// <summary>
/// Typed HTTP client for the Django device API. Transport failures surface as
/// <see cref="HttpRequestException"/> / <see cref="TaskCanceledException"/> so callers can retry.
/// </summary>
public sealed class BackendClient
{
    private readonly HttpClient _http;

    public BackendClient(HttpClient http)
    {
        _http = http;
        EnsureSafeBaseAddress(http.BaseAddress);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZinweAgent", AgentInfo.Version));
        }
    }

    /// <summary>Device tokens must never cross the network in clear text; plain HTTP is allowed only for loopback (local dev).</summary>
    internal static void EnsureSafeBaseAddress(Uri? baseAddress)
    {
        if (baseAddress is null || !baseAddress.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Agent:BackendBaseUrl must be an absolute URL.");
        }
        if (baseAddress.Scheme != Uri.UriSchemeHttps && !baseAddress.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Agent:BackendBaseUrl must use https (got {baseAddress}). Plain http is only allowed for localhost.");
        }
    }

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
            "api/devices/enroll/", JsonBody(request, BackendJsonContext.Default.EnrollRequest), ct);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var (field, message) = await ReadFirstErrorAsync(response, ct);
            // Only a refused key is the admin's problem; any other 400 is an agent/backend contract bug -- retry & log.
            throw field == "enrollment_key"
                ? new EnrollmentRejectedException(message)
                : new HttpRequestException($"Enrollment request rejected ({field}: {message}).");
        }
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.EnrollResponse, ct);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(string deviceToken, HeartbeatRequest request, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, "api/devices/heartbeat/", deviceToken,
            JsonBody(request, BackendJsonContext.Default.HeartbeatRequest), ct);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.HeartbeatResponse, ct);
    }

    /// <summary>GET /api/devices/policy/current/ -- the owner's rules for this PC.</summary>
    public async Task<AgentPolicy> GetPolicyAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, "api/devices/policy/current/", deviceToken, null, ct);
        response.EnsureSuccessStatusCode();
        var policy = await ReadAsync(response, BackendJsonContext.Default.AgentPolicy, ct);
        return policy.IsWellFormed
            ? policy
            : throw new HttpRequestException("The backend returned a malformed policy.");
    }

    /// <summary>
    /// POST /api/devices/events/upload/ (1..100 events). Idempotent on event id: re-sent events come back as duplicates.
    /// A 400 (the backend refused the batch's content) throws <see cref="EventsRejectedException"/>.
    /// </summary>
    public async Task<EventUploadResponse> UploadEventsAsync(string deviceToken, IReadOnlyList<AgentEvent> events, CancellationToken ct)
    {
        if (events.Count is < 1 or > MaxEventsPerUpload)
        {
            throw new ArgumentOutOfRangeException(nameof(events), events.Count, $"Upload 1..{MaxEventsPerUpload} events at a time.");
        }
        var request = new EventUploadRequest([.. events.Select(ToUpload)]);

        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, "api/devices/events/upload/", deviceToken,
            JsonBody(request, BackendJsonContext.Default.EventUploadRequest), ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var (field, message) = await ReadFirstErrorAsync(response, ct);
            throw new EventsRejectedException(string.IsNullOrEmpty(field) ? message : $"{field}: {message}");
        }
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.EventUploadResponse, ct);
    }

    public const int MaxEventsPerUpload = 100;

    private static EventUpload ToUpload(AgentEvent e)
    {
        using var data = JsonDocument.Parse(e.DataJson);
        return new EventUpload(e.Id, e.Type, e.OccurredAt, e.Severity, data.RootElement.Clone());
    }

    /// <summary>Sends with "Authorization: Device &lt;token&gt;". A 401 means the owner removed this PC.</summary>
    private async Task<HttpResponseMessage> SendAsDeviceAsync(
        HttpMethod method, string path, string deviceToken, HttpContent? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, path) { Content = body };
        message.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);

        var response = await _http.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            using (response)
            {
                throw new DeviceRejectedException((await ReadFirstErrorAsync(response, ct)).Message);
            }
        }
        return response;
    }

    /// <summary>
    /// Buffered JSON body with a Content-Length. JsonContent streams with chunked transfer encoding,
    /// which WSGI servers (Django runserver, many gunicorn setups) silently read as an empty body.
    /// </summary>
    private static ByteArrayContent JsonBody<T>(T value, JsonTypeInfo<T> type)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>An unparseable 2xx (proxy page, contract drift) is a retryable transport problem, not a crash.</summary>
    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, ct)
                   ?? throw new HttpRequestException("The backend returned an empty response.");
        }
        catch (JsonException e)
        {
            throw new HttpRequestException($"The backend returned an unexpected response: {e.Message}", e);
        }
    }

    /// <summary>DRF errors are {"field": ["msg"]} or {"detail": "msg"}; return the first field and its message.</summary>
    private static async Task<(string Field, string Message)> ReadFirstErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = $"The backend refused the request ({(int)response.StatusCode}).";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        return (property.Name, property.Value.GetString() ?? fallback);
                    case JsonValueKind.Array when property.Value.GetArrayLength() > 0:
                        return (property.Name, property.Value[0].GetString() ?? fallback);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a proxy error page) -- fall through.
        }
        return ("", fallback);
    }
}
