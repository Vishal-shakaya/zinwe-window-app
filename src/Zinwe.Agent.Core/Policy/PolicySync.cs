using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Policy;

/// <summary>
/// Keeps this PC on the owner's latest policy.
///   startup:          policy.json → apply           (no network; protection is on before the first heartbeat)
///   every heartbeat:  policy_version differs → GET policy/current/ → save policy.json → apply → usb.policy_applied
/// </summary>
public sealed class PolicySync(BackendClient backend, PolicyStore store, UsbProtection usb, ILogger<PolicySync> logger)
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
        logger.LogInformation("Applied cached policy v{Version} (USB {Mode})", cached.Version, cached.Usb.Mode.ToWire());
        return cached;
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
        logger.LogInformation("Policy v{Version} received (USB {Mode})", fetched.Version, fetched.Usb.Mode.ToWire());
        return true;
    }
}
