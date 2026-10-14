using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// What the widget's update card shows, from the service's status. Pure, so it's unit-tested.
/// <list type="bullet">
/// <item>Required release: an orange card at the top; log in / break / log out stay paused until updated.</item>
/// <item>Optional release: a quiet card; work carries on.</item>
/// <item>The button runs the one-click update and then reports its progress; "Download manually" is the
///       fallback when no installer was published or the update failed.</item>
/// </list>
/// </summary>
public sealed record UpdateView(
    bool Show,
    bool Required,
    string Title,
    string Body,
    string? Notes,
    string Button,
    bool ButtonVisible,
    bool ButtonEnabled,
    bool Busy,
    string? Progress,
    bool ProgressIsError,
    bool ShowManual)
{
    public static readonly UpdateView Hidden = new(false, false, "", "", null, "", false, false, false, null, false, false);

    public static UpdateView From(AgentStatus? status)
    {
        if (status is null)
        {
            return Hidden;
        }
        var required = status.State == AgentState.UpdateRequired;
        if (!required && status.UpdateVersion is null)
        {
            return Hidden;
        }

        var version = string.IsNullOrWhiteSpace(status.UpdateVersion) ? "A new version of Zinwe" : $"Zinwe {status.UpdateVersion}";
        var title = required ? "Update Zinwe to keep working" : "Update available";
        var body = required
            ? $"{version} is required by your company. Log in, breaks and log out are paused until you update. Protection stays on."
            : $"{version} is ready. It installs in about a minute and Zinwe reopens by itself.";
        var notes = string.IsNullOrWhiteSpace(status.UpdateNotes) ? null : status.UpdateNotes.Trim();
        var installable = status.UpdateInstallable;

        return status.UpdateStage switch
        {
            UpdateStages.Downloading => new(true, required, title, body, notes, "Downloading…", true, false, true,
                status.UpdateMessage ?? "Downloading the update…", false, false),
            UpdateStages.Installing => new(true, required, title, body, notes, "Installing…", true, false, true,
                status.UpdateMessage ?? "Installing. Zinwe will reopen in a moment.", false, false),
            UpdateStages.Ready => new(true, required, title, body, notes, "Downloaded", true, false, false,
                status.UpdateMessage, false, false),
            UpdateStages.Failed => new(true, required, title, body, notes, "Try again", installable, installable, false,
                status.UpdateMessage ?? "The update didn't work.", true, true),
            _ => new(true, required, title, body, notes, "Update now", installable, installable, false,
                installable ? null : "Your admin hasn't published the installer here yet.", false, !installable),
        };
    }
}
