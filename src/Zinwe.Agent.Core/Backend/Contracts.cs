using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Backend;

// Wire contracts for /api/devices/* (see backend z_devices). Django speaks snake_case;
// the source-generated context below maps PascalCase properties automatically.

// Enrollment now identifies a MEMBER: the owner adds a member (name + email) and gets a member id
// (e.g. ZM-XXXX-XXXX); the PC enrols with that member's email + id. No org-wide key.
public sealed record EnrollRequest(
    string Email,
    string MemberId,
    string MachineId,
    string Hostname,
    string OsVersion,
    string AgentVersion,
    string Platform = "windows");

public sealed record EnrollResponse(long DeviceId, string DeviceToken, bool ReEnrolled);

// Heartbeat now also carries the member's work status (read from the widget) so the owner sees working/on break.
// WorkChangedAt + BreakReason feed the owner's Activity timeline (start / break with reason / back / end).
public sealed record HeartbeatRequest(
    string Hostname,
    string OsVersion,
    string AgentVersion,
    string WorkState,
    long WorkedSeconds,
    long BreakSeconds,
    DateTimeOffset? WorkChangedAt = null,
    string? BreakReason = null);

/// <param name="UpdateRequired">The agent is below the owner/plan's minimum version and must be updated.</param>
public sealed record HeartbeatResponse(
    long DeviceId,
    DateTimeOffset ServerTime,
    int NextHeartbeatSeconds,
    int PolicyVersion,
    bool UpdateRequired = false,
    string? DownloadUrl = null);

/// <summary>One rolled-up usage metric uploaded via POST /api/devices/metrics/upload/.</summary>
public sealed record MetricUpload(
    DateTimeOffset BucketStart, string Category, string Key, long Count, long Bytes, long Seconds);

public sealed record MetricUploadRequest(MetricUpload[] Metrics);

public sealed record MetricUploadResponse(int Accepted);

// GET /api/devices/policy/current/ returns an AgentPolicy (Zinwe.Agent.Core.Policy): {"version": n, "usb": {"mode": "..."}}.

/// <summary>One event in POST /api/devices/events/upload/. <see cref="Data"/> is sent as a JSON object.</summary>
public sealed record EventUpload(string Id, string Type, DateTimeOffset OccurredAt, string Severity, JsonElement Data);

public sealed record EventUploadRequest(EventUpload[] Events);

/// <summary>One line of the member's conversation (GET /api/devices/messages/). Shown in the employee widget.</summary>
/// <param name="Sender">"owner" = an admin message/alert; "member" = the member's own reply. Trailing/optional for back-compat.</param>
public sealed record AgentMessage(
    long Id, string Title, string? Body, string Severity, DateTimeOffset CreatedAt, string Sender = "owner");

/// <summary>POST /api/devices/messages/ — the member replies in the thread.</summary>
public sealed record MemberReplyRequest(string Body);

/// <summary><see cref="Accepted"/> = newly stored; <see cref="Duplicates"/> = ids the backend already had (a retry).</summary>
public sealed record EventUploadResponse(int Accepted, int Duplicates);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(AgentPolicy))]
[JsonSerializable(typeof(EventUploadRequest))]
[JsonSerializable(typeof(EventUploadResponse))]
[JsonSerializable(typeof(AgentMessage[]))]
[JsonSerializable(typeof(MemberReplyRequest))]
[JsonSerializable(typeof(MetricUploadRequest))]
[JsonSerializable(typeof(MetricUploadResponse))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class BackendJsonContext : JsonSerializerContext;
