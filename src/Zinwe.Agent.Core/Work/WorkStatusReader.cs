using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Work;

/// <summary>The member's current work status, as the owner's dashboard shows it.</summary>
/// <param name="ChangedAt">When the current state began (the owner's Activity timeline uses it).</param>
/// <param name="BreakReason">Why they're on a break, when they are and picked one.</param>
public sealed record WorkStatus(
    string State, long WorkedSeconds, long BreakSeconds,
    DateTimeOffset? ChangedAt = null, string? BreakReason = null)
{
    public static readonly WorkStatus NotStarted = new("not_started", 0, 0);
}

/// <summary>
/// Reads the widget's per-user work-timing.json so the service can report work status on each heartbeat.
/// The widget (Zinwe.Agent.Tray) owns the file; this only reads it. DEV NOTE: it lives in %LOCALAPPDATA%\Zinwe,
/// so under the console (dev) the agent is the same user and sees it; a LocalSystem service would need per-user
/// resolution (tracked with the same milestone as owner→member messages).
/// </summary>
public sealed class WorkStatusReader
{
    private readonly string _path;

    public WorkStatusReader(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zinwe");
        _path = Path.Combine(dir, "work-timing.json");
    }

    /// <summary>Current status, or <see cref="WorkStatus.NotStarted"/> when there's no (or a stale) file.</summary>
    public WorkStatus Read(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return WorkStatus.NotStarted;
            }
            var dto = JsonSerializer.Deserialize<TimingDto>(File.ReadAllText(_path), Json);
            if (dto is null)
            {
                return WorkStatus.NotStarted;
            }
            // A stale day (the widget rolls over at midnight) reads as a fresh, not-started day.
            if (!string.Equals(dto.Date, now.ToLocalTime().ToString("yyyy-MM-dd"), StringComparison.Ordinal))
            {
                return WorkStatus.NotStarted;
            }
            var wire = dto.State switch
            {
                "Working" => "working",
                "OnBreak" => "on_break",
                "Ended" => "ended",
                _ => "not_started",
            };
            return new WorkStatus(
                wire, Math.Max(0, dto.WorkedSeconds), Math.Max(0, dto.BreakSeconds),
                dto.LastTransitionAtUtc, wire == "on_break" ? dto.BreakReason : null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return WorkStatus.NotStarted;
        }
    }

    // Mirrors the fields the widget writes (Zinwe.Agent.Tray.Widget.WorkTimerState); read case-insensitively.
    private sealed class TimingDto
    {
        public string Date { get; set; } = "";
        public string State { get; set; } = "";
        public long WorkedSeconds { get; set; }
        public long BreakSeconds { get; set; }
        public DateTimeOffset? LastTransitionAtUtc { get; set; }
        public string? BreakReason { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
