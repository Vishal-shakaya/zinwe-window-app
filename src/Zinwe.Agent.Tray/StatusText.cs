using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Tray;

public enum StatusTone { Ok, Warn, Bad }

/// <summary>Plain-language wording for each agent state -- employees should never see raw state names.</summary>
public static class StatusText
{
    public static (string Headline, string Detail, StatusTone Tone) Describe(AgentStatus? status) => status?.State switch
    {
        null => ("Zinwe service isn't running",
                 "Protection is paused on this PC. Restart the computer or contact your admin.", StatusTone.Bad),
        AgentState.Connected => ("This PC is protected",
                 "Connected to your company's Zinwe account.", StatusTone.Ok),
        AgentState.Offline => ("Offline — protection stays on",
                 status.Message ?? "Zinwe will reconnect automatically.", StatusTone.Warn),
        AgentState.Enrolling => ("Setting up…",
                 "Connecting this PC to your company's Zinwe account.", StatusTone.Warn),
        AgentState.NotEnrolled => ("Not set up yet",
                 status.Message ?? "Waiting for your admin to finish setup.", StatusTone.Warn),
        AgentState.Rejected => ("Not connected",
                 status.Message ?? "Ask your admin for a new enrollment key.", StatusTone.Bad),
        AgentState.UpdateRequired => ("Update required to keep working",
                 status.Message ?? "A newer version of Zinwe is required. Download and install it to continue.", StatusTone.Bad),
        _ => ("Unknown status", "", StatusTone.Warn),
    };

    /// <summary>One or two words for the widget's status pill; the full sentence is its tooltip.</summary>
    public static string Short(AgentStatus? status) => status?.State switch
    {
        null => "Not running",
        AgentState.Connected => "Protected",
        AgentState.Offline => "Offline",
        AgentState.Enrolling => "Connecting",
        AgentState.NotEnrolled => "Not set up",
        AgentState.Rejected => "Not connected",
        AgentState.UpdateRequired => "Update needed",
        _ => "Unknown",
    };

    public static string LastSeen(DateTimeOffset? at)
    {
        if (at is null)
        {
            return "Never";
        }
        var ago = DateTimeOffset.UtcNow - at.Value;
        return ago.TotalSeconds < 60 ? "Just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : at.Value.ToLocalTime().ToString("d MMM, h:mm tt");
    }
}
