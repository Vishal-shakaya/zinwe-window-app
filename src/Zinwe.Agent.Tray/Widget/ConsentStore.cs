using System.IO;
using System.Text.Json;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// The member's consent to monitoring on this PC (per Windows user), written when they press
/// "I agree" on the widget's consent screen. The service reads it and reports it on the heartbeat.
/// A new <see cref="CurrentVersion"/> asks again; until the member agrees, the service records nothing.
/// </summary>
public sealed class ConsentStore
{
    /// <summary>The consent notice version; shared with the service's <see cref="ConsentGate"/>.</summary>
    public const string CurrentVersion = ConsentReader.CurrentVersion;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record ConsentFile(string Version, DateTimeOffset AcceptedAt);

    /// <summary>True when this user agreed to the current version.</summary>
    public bool HasConsented()
    {
        try
        {
            if (!File.Exists(WidgetPaths.Consent))
            {
                return false;
            }
            var file = JsonSerializer.Deserialize<ConsentFile>(File.ReadAllText(WidgetPaths.Consent));
            return file?.Version == CurrentVersion;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Record agreement now. Best effort: a failed write just asks again next time.</summary>
    public void Accept(DateTimeOffset now)
    {
        try
        {
            WidgetPaths.EnsureDirectory();
            var tmp = WidgetPaths.Consent + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new ConsentFile(CurrentVersion, now), Json));
            File.Move(tmp, WidgetPaths.Consent, overwrite: true);
        }
        catch
        {
            // Never crash the widget over a locked or roaming profile.
        }
    }
}
