using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Features;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Policy;

/// <summary>
/// Keeps this PC on the owner's latest policy.
///   startup:          policy.json → apply USB + feature config   (no network; protection is on before the first heartbeat)
///   every heartbeat:  policy_version differs → GET policy/current/ → save policy.json → apply USB + features → usb.policy_applied
///
/// USB is applied directly (<see cref="UsbProtection"/>); the other features are (re)configured through
/// <see cref="IFeaturePolicyApply"/>, which rebuilds their scope so each restarts on the new per-feature block.
/// </summary>
public sealed class PolicySync(
    BackendClient backend,
    PolicyStore store,
    UsbProtection usb,
    ILogger<PolicySync> logger,
    IFeaturePolicyApply? features = null)
{
    private AgentPolicy? _current;
    private volatile bool _applyPending;

    /// <summary>The policy in force (cached or freshly fetched), or null before the first sync on a new install.</summary>
    public AgentPolicy? Current => Volatile.Read(ref _current);

    /// <summary>Call once at startup, before any network call.</summary>
    public AgentPolicy? ApplyCached()
    {
        var cached = store.Load();
        if (cached is null)
        {
            logger.LogInformation("No cached policy yet; USB settings stay as they are until the first sync.");
            return null;
        }
        Volatile.Write(ref _current, cached);
        _applyPending = !usb.Apply(cached.Usb.Mode, report: false);
        ApplyToFeatures(cached, CancellationToken.None);
        logger.LogInformation("Applied cached policy v{Version} (USB {Mode})", cached.Version, cached.Usb.Mode.ToWire());
        return cached;
    }

    /// <summary>
    /// (Re)configure the non-USB feature modules for <paramref name="policy"/>. Startup blocks briefly here;
    /// module start is quick (the real work runs on background loops). Never throws to the caller.
    /// </summary>
    private void ApplyToFeatures(AgentPolicy policy, CancellationToken ct)
    {
        if (features is null)
        {
            return;
        }
        try
        {
            features.ApplyAsync(policy, ct).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Applying feature policy v{Version} failed", policy.Version);
        }
    }

    /// <summary>
    /// Call after every successful heartbeat. Returns true when a new policy was fetched and applied.
    /// Network failures are logged and retried on the next heartbeat (the current policy stays in force);
    /// <see cref="DeviceRejectedException"/> propagates so the worker's removal path handles it.
    /// </summary>
    public async Task<bool> SyncAsync(string deviceToken, int serverVersion, CancellationToken ct)
    {
        var current = Current;
        if (current is not null && current.Version == serverVersion)
        {
            if (_applyPending)
            {
                _applyPending = !usb.Apply(current.Usb.Mode, report: true);
            }
            return false;
        }

        AgentPolicy fetched;
        try
        {
            fetched = await backend.GetPolicyAsync(deviceToken, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not fetch policy v{Version}; keeping v{Current}: {Error}",
                serverVersion, current?.Version ?? 0, e.Message);
            return false;
        }

        try
        {
            store.Save(fetched);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still apply it now; a restart before the next successful save would fall back to the older cache.
            logger.LogError(e, "Could not cache policy v{Version}", fetched.Version);
        }

        Volatile.Write(ref _current, fetched);
        _applyPending = !usb.Apply(fetched.Usb.Mode, report: true);
        ApplyToFeatures(fetched, ct);
        logger.LogInformation("Policy v{Version} received (USB {Mode})", fetched.Version, fetched.Usb.Mode.ToWire());
        return true;
    }
}
