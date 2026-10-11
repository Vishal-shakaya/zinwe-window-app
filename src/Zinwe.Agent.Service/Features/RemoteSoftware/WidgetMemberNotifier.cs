using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Service.Features.RemoteSoftware;

/// <summary>
/// Writes a member-facing note to the widget's per-user notifications file (via <see cref="NotificationWriter"/>).
/// Negative ids never collide with a server message id. Best-effort: a write failure is swallowed.
/// </summary>
internal sealed class WidgetMemberNotifier(NotificationWriter writer) : IMemberNotifier
{
    public void Notify(string title, string? body, string severity, DateTimeOffset? at = null)
    {
        try
        {
            var id = -DateTimeOffset.UtcNow.Ticks - Random.Shared.Next(1000);
            writer.Merge([new AgentMessage(id, title, body, severity, at ?? DateTimeOffset.Now)]);
        }
        catch
        {
            // the widget note is best-effort; never let it affect detection/enforcement
        }
    }
}
