namespace Zinwe.Agent.Core.Features.Web;

/// <summary>A blocked domain was requested on this PC.</summary>
/// <param name="Domain">The requested host.</param>
/// <param name="Category">The blocklist category it matched, when known.</param>
public sealed record BlockedAttempt(string Domain, string? Category = null);

/// <summary>
/// Source of real-time "a blocked site was requested" signals. Implementations raise <see cref="Attempted"/> on a
/// background thread. The agent subscribes and turns each into a <c>web.blocked_attempt</c> event.
/// </summary>
public interface IBlockedAttemptSource : IDisposable
{
    /// <summary>Raised on a background thread when a blocked domain is requested.</summary>
    event EventHandler<BlockedAttempt>? Attempted;

    /// <summary>Begin listening. Throws if the OS facility is unavailable (the caller logs and carries on).</summary>
    void Start();
}

/// <summary>
/// The shipped default for the base app. Hosts-file blocking is SILENT at the OS level (a blocked lookup simply
/// fails), and a reliable live attempt feed needs either the DNS-Client ETW/event-log channel (off by default,
/// needs elevation to enable) or a WFP/DNS-proxy component — both out of scope for the local base app. So this
/// source is a no-op: the event path is fully built and tested, and a real source is wired in a later milestone.
/// </summary>
public sealed class NullBlockedAttemptSource : IBlockedAttemptSource
{
    public event EventHandler<BlockedAttempt>? Attempted;

    public void Start()
    {
        // Intentionally does nothing; keeps the subscription contract alive for a future real source.
        _ = Attempted;
    }

    public void Dispose()
    {
    }
}
