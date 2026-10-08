using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Ipc;

[JsonConverter(typeof(JsonStringEnumConverter<AgentState>))]
public enum AgentState
{
    /// <summary>No device identity and no enrollment key yet -- waiting for the installer/admin.</summary>
    NotEnrolled,
    Enrolling,
    /// <summary>Last heartbeat succeeded.</summary>
    Connected,
    /// <summary>Enrolled, but the backend is unreachable. Protection keeps running on cached policy.</summary>
    Offline,
    /// <summary>The owner removed this PC (or the key was refused). Needs a new enrollment key.</summary>
    Rejected,
}

/// <summary>
/// Snapshot the service hands to the tray app over the named pipe.
/// Add new members only as optional trailing parameters: an older tray must keep reading a newer service.
/// </summary>
/// <param name="PolicyVersion">Policy in force (cached or fetched); null before the first policy sync.</param>
/// <param name="UsbMode">"allow" | "read_only" | "block", or null when no policy is in force yet.</param>
public sealed record AgentStatus(
    AgentState State,
    string AgentVersion,
    string Hostname,
    long? DeviceId,
    DateTimeOffset? LastHeartbeatAt,
    string? Message,
    int? PolicyVersion = null,
    string? UsbMode = null);
