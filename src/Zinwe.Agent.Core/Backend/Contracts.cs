using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Backend;

// Wire contracts for /api/devices/* (see backend z_devices). Django speaks snake_case;
// the source-generated context below maps PascalCase properties automatically.

public sealed record EnrollRequest(
    string EnrollmentKey,
    string MachineId,
    string Hostname,
    string OsVersion,
    string AgentVersion,
    string Platform = "windows");

public sealed record EnrollResponse(long DeviceId, string DeviceToken, bool ReEnrolled);

public sealed record HeartbeatRequest(string Hostname, string OsVersion, string AgentVersion);

public sealed record HeartbeatResponse(
    long DeviceId,
    DateTimeOffset ServerTime,
    int NextHeartbeatSeconds,
    int PolicyVersion);

// GET /api/devices/policy/current/ returns an AgentPolicy (Zinwe.Agent.Core.Policy): {"version": n, "usb": {"mode": "..."}}.

/// <summary>One event in POST /api/devices/events/upload/. <see cref="Data"/> is sent as a JSON object.</summary>
public sealed record EventUpload(string Id, string Type, DateTimeOffset OccurredAt, string Severity, JsonElement Data);

public sealed record EventUploadRequest(EventUpload[] Events);

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
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class BackendJsonContext : JsonSerializerContext;
