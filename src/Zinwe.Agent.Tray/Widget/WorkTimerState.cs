using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>The employee's work-timing state for the day. Employee-initiated and fully visible.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkState>))]
public enum WorkState
{
    /// <summary>The workday has not been started yet today.</summary>
    NotStarted,
    /// <summary>Clocked in and working; worked time accumulates.</summary>
    Working,
    /// <summary>On a break; break time accumulates, worked time is paused.</summary>
    OnBreak,
    /// <summary>The workday has been ended; nothing accumulates.</summary>
    Ended,
}

/// <summary>
/// Pure work-timing state machine and accumulator. No I/O, no WPF, no clock of its own:
/// every mutator takes the current <see cref="DateTimeOffset"/>, so it is fully unit-testable.
///
/// Rules
/// - Worked seconds accrue only while <see cref="State"/> is <see cref="WorkState.Working"/>.
/// - Break seconds accrue only while <see cref="State"/> is <see cref="WorkState.OnBreak"/>.
/// - On any transition the elapsed time since <see cref="LastTransitionAtUtc"/> is settled into the
///   bucket for the state being LEFT, then the new state begins.
/// - A new calendar day (local date) resets the day via <see cref="RollOverIfNewDay"/>.
///
/// This type is the seam the verifier can reference from a test project (it has no Tray-exe dependency).
/// </summary>
public sealed class WorkTimerState
{
    /// <summary>Local calendar date this state belongs to (yyyy-MM-dd).</summary>
    public string Date { get; set; } = "";

    public WorkState State { get; set; } = WorkState.NotStarted;

    /// <summary>Settled worked seconds (does NOT include time since the last transition while Working).</summary>
    public long WorkedSeconds { get; set; }

    /// <summary>Settled break seconds (does NOT include time since the last transition while OnBreak).</summary>
    public long BreakSeconds { get; set; }

    /// <summary>When the current state began. null only before the first Start of the day.</summary>
    public DateTimeOffset? LastTransitionAtUtc { get; set; }

    /// <summary>
    /// Why the employee is on a break (e.g. "Lunch"), picked when they pressed Break; null otherwise.
    /// Reported to the owner's Activity page with the break.
    /// </summary>
    public string? BreakReason { get; set; }

    /// <summary>Longest break reason we keep (matches the backend's limit).</summary>
    public const int MaxBreakReasonLength = 120;

    /// <summary>True when the employee can press Start (fresh day or after End).</summary>
    [JsonIgnore]
    public bool CanStart => State is WorkState.NotStarted or WorkState.Ended;

    /// <summary>True when Break/Resume is meaningful (clocked in).</summary>
    [JsonIgnore]
    public bool CanToggleBreak => State is WorkState.Working or WorkState.OnBreak;

    /// <summary>True when the day can be ended.</summary>
    [JsonIgnore]
    public bool CanEnd => State is WorkState.Working or WorkState.OnBreak;

    /// <summary>Settles time elapsed in the current state into its bucket, up to <paramref name="now"/>.</summary>
    private void Settle(DateTimeOffset now)
    {
        if (LastTransitionAtUtc is not { } since)
        {
            return;
        }
        var elapsed = now - since;
        if (elapsed < TimeSpan.Zero)
        {
            // Clock went backwards (e.g. DST / manual change); don't credit negative time.
            elapsed = TimeSpan.Zero;
        }
        switch (State)
        {
            case WorkState.Working:
                WorkedSeconds += (long)elapsed.TotalSeconds;
                break;
            case WorkState.OnBreak:
                BreakSeconds += (long)elapsed.TotalSeconds;
                break;
        }
        LastTransitionAtUtc = now;
    }

    /// <summary>Begins the workday (or a new session after End). No-op if already clocked in.</summary>
    public bool Start(DateTimeOffset now)
    {
        if (!CanStart)
        {
            return false;
        }
        State = WorkState.Working;
        LastTransitionAtUtc = now;
        return true;
    }

    /// <summary>
    /// Toggles between Working and OnBreak. Going on a break records <paramref name="reason"/>
    /// (trimmed, capped); coming back clears it. Returns false when not clocked in.
    /// </summary>
    public bool ToggleBreak(DateTimeOffset now, string? reason = null)
    {
        if (!CanToggleBreak)
        {
            return false;
        }
        Settle(now);
        State = State == WorkState.Working ? WorkState.OnBreak : WorkState.Working;
        BreakReason = State == WorkState.OnBreak ? CleanReason(reason) : null;
        LastTransitionAtUtc = now;
        return true;
    }

    private static string? CleanReason(string? reason)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        return text.Length > MaxBreakReasonLength ? text[..MaxBreakReasonLength] : text;
    }

    /// <summary>Ends the workday. Returns false when not clocked in.</summary>
    public bool End(DateTimeOffset now)
    {
        if (!CanEnd)
        {
            return false;
        }
        Settle(now);
        State = WorkState.Ended;
        BreakReason = null;
        LastTransitionAtUtc = now;
        return true;
    }

    /// <summary>Live worked time including any unsettled time since the last transition.</summary>
    public TimeSpan LiveWorked(DateTimeOffset now) => Live(now, WorkState.Working, WorkedSeconds);

    /// <summary>Live break time including any unsettled time since the last transition.</summary>
    public TimeSpan LiveBreak(DateTimeOffset now) => Live(now, WorkState.OnBreak, BreakSeconds);

    private TimeSpan Live(DateTimeOffset now, WorkState accruingIn, long settledSeconds)
    {
        var total = TimeSpan.FromSeconds(settledSeconds);
        if (State == accruingIn && LastTransitionAtUtc is { } since)
        {
            var elapsed = now - since;
            if (elapsed > TimeSpan.Zero)
            {
                total += elapsed;
            }
        }
        return total;
    }

    /// <summary>
    /// If <paramref name="now"/> falls on a different local day than <see cref="Date"/>, settles the old
    /// day and resets to a fresh NotStarted day. Returns true when a reset happened.
    /// </summary>
    public bool RollOverIfNewDay(DateTimeOffset now)
    {
        var today = now.ToLocalTime().ToString("yyyy-MM-dd");
        if (Date == today)
        {
            return false;
        }
        // Settle whatever was running so the (now closed) day's totals are final before we discard them.
        Settle(now);
        Date = today;
        State = WorkState.NotStarted;
        WorkedSeconds = 0;
        BreakSeconds = 0;
        LastTransitionAtUtc = null;
        BreakReason = null;
        return true;
    }

    /// <summary>A fresh state stamped with today's local date.</summary>
    public static WorkTimerState NewForToday(DateTimeOffset now) => new()
    {
        Date = now.ToLocalTime().ToString("yyyy-MM-dd"),
        State = WorkState.NotStarted,
    };
}
