namespace Zinwe.Agent.Core.Features;

/// <summary>
/// One protection/monitoring feature (internet usage, data-transfer logging, remote-software watch…).
///
/// A module is self-contained: it owns its own collectors, enforcers and config section, writes to the shared
/// <see cref="Events.EventQueue"/> / <see cref="Metrics.MetricsStore"/>, and is started and stopped by the
/// service's feature host. New features add a module class plus a registration (see IFeatureRegistration in the
/// service project) and are picked up automatically — no edits to Program.cs or any other feature's files.
///
/// Implementations must never throw out of Start/Stop and must honour dry-run (change nothing on the PC unless
/// running as LocalSystem with enforcement enabled); the host logs and keeps the agent running regardless.
/// </summary>
public interface IFeatureModule
{
    /// <summary>Stable short id, dotted-lowercase (e.g. "web", "data_transfer", "remote_software").</summary>
    string Name { get; }

    /// <summary>Begin collecting/enforcing. Called once at service start.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stop and release resources. Called once at service shutdown (reverse start order).</summary>
    Task StopAsync(CancellationToken ct);
}
