using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Policy;

/// <summary>What the owner chose for USB storage on their PCs (dashboard → Rules).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsbMode>))]
public enum UsbMode
{
    /// <summary>USB drives work normally.</summary>
    [JsonStringEnumMemberName("allow")]
    Allow,

    /// <summary>Files can be opened from USB drives, but nothing can be copied onto them.</summary>
    [JsonStringEnumMemberName("read_only")]
    ReadOnly,

    /// <summary>USB drives can't be opened at all.</summary>
    [JsonStringEnumMemberName("block")]
    Block,
}

public static class UsbModeExtensions
{
    /// <summary>The backend's spelling ("allow" | "read_only" | "block").</summary>
    public static string ToWire(this UsbMode mode) => mode switch
    {
        UsbMode.Allow => "allow",
        UsbMode.ReadOnly => "read_only",
        UsbMode.Block => "block",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown USB mode."),
    };

    /// <summary>Employee-facing wording for the tray.</summary>
    public static string ToDisplay(this UsbMode mode) => mode switch
    {
        UsbMode.Allow => "Allowed",
        UsbMode.ReadOnly => "Read-only",
        UsbMode.Block => "Blocked",
        _ => "Unknown",
    };
}

public sealed record UsbPolicy(UsbMode Mode);

/// <summary>
/// The owner's rules for this PC, as returned by GET /api/devices/policy/current/ and cached in policy.json.
/// <see cref="Version"/> goes up by one each time the owner changes something.
/// </summary>
public sealed record AgentPolicy(int Version, UsbPolicy Usb)
{
    /// <summary>
    /// The serializer fills missing members with null and accepts integers for enums, so check the shape
    /// before acting on it. A malformed policy is never applied.
    /// </summary>
    public bool IsWellFormed => Version >= 0 && Usb is not null && Enum.IsDefined(Usb.Mode);
}
