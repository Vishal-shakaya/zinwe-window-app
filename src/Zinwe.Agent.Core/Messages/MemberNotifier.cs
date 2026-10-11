namespace Zinwe.Agent.Core.Messages;

/// <summary>
/// Drops a short note into the member's own Zinwe widget (consent-first: the person sees what their company's
/// protection did on their PC). The service writes it to the widget's notifications file; Core/tests use the
/// no-op. Must never throw.
/// </summary>
public interface IMemberNotifier
{
    void Notify(string title, string? body, string severity, DateTimeOffset? at = null);
}

/// <summary>Default when there is no widget to write to (tests, or a context without the per-user file): no-op.</summary>
public sealed class NullMemberNotifier : IMemberNotifier
{
    public void Notify(string title, string? body, string severity, DateTimeOffset? at = null)
    {
    }
}
