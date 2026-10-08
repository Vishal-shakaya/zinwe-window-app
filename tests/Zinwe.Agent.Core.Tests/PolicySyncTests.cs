using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

public sealed class PolicySyncTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private readonly FakeEnforcer _enforcer = new(dryRun: true);
    private readonly PolicyStore _store;
    private readonly EventQueue _queue;
    private readonly UsbProtection _usb;
    private readonly PolicySync _sync;

    public PolicySyncTests()
    {
        _store = new PolicyStore(_dir.Path);
        _queue = new EventQueue(_dir.Path);
        _usb = new UsbProtection(_enforcer, _queue, NullLogger<UsbProtection>.Instance);
        _sync = new PolicySync(_backend.Client(), _store, _usb, NullLogger<PolicySync>.Instance);
    }

    [Fact]
    public void Startup_applies_the_cached_policy_without_any_network_call()
    {
        _store.Save(new AgentPolicy(4, new UsbPolicy(UsbMode.Block)));
        // FakeBackend throws if called.

        var applied = _sync.ApplyCached();

        Assert.Equal(4, applied?.Version);
        Assert.Equal([UsbMode.Block], _enforcer.Applied);
        Assert.Equal(UsbMode.Block, _usb.CurrentMode);
        Assert.Empty(_backend.Requests);
        Assert.Equal(0, _queue.Count()); // a restart is not news for the owner's feed
    }

    [Fact]
    public void Startup_without_a_cache_leaves_usb_untouched()
    {
        Assert.Null(_sync.ApplyCached());
        Assert.Empty(_enforcer.Applied);
        Assert.Null(_usb.CurrentMode);
    }

    [Fact]
    public async Task Same_version_does_not_fetch()
    {
        _store.Save(new AgentPolicy(4, new UsbPolicy(UsbMode.Allow)));
        _sync.ApplyCached();

        Assert.False(await _sync.SyncAsync("tok", 4, CancellationToken.None));
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task New_version_is_fetched_cached_applied_and_reported()
    {
        _store.Save(new AgentPolicy(1, new UsbPolicy(UsbMode.Allow)));
        _sync.ApplyCached();
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"version": 2, "usb": {"mode": "read_only"}}""");

        Assert.True(await _sync.SyncAsync("tok", 2, CancellationToken.None));

        Assert.Equal("/api/devices/policy/current/", Assert.Single(_backend.Requests).Path);
        Assert.Equal(new AgentPolicy(2, new UsbPolicy(UsbMode.ReadOnly)), _store.Load());
        Assert.Equal(new AgentPolicy(2, new UsbPolicy(UsbMode.ReadOnly)), _sync.Current);
        Assert.Equal([UsbMode.Allow, UsbMode.ReadOnly], _enforcer.Applied);

        var evt = Assert.Single(_queue.PeekBatch(10));
        Assert.Equal(EventTypes.UsbPolicyApplied, evt.Type);
        var data = JsonNode.Parse(evt.DataJson)!;
        Assert.Equal("read_only", data["mode"]!.GetValue<string>());
        Assert.True(data["dry_run"]!.GetValue<bool>());
    }

    [Fact]
    public async Task First_sync_on_a_new_install_fetches()
    {
        _sync.ApplyCached();
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"version": 1, "usb": {"mode": "allow"}}""");

        Assert.True(await _sync.SyncAsync("tok", 1, CancellationToken.None));
        Assert.Equal([UsbMode.Allow], _enforcer.Applied);
    }

    [Fact]
    public async Task Network_failure_keeps_the_current_policy_and_retries_next_beat()
    {
        _store.Save(new AgentPolicy(1, new UsbPolicy(UsbMode.Block)));
        _sync.ApplyCached();
        _backend.Respond = _ => throw new HttpRequestException("connection refused");

        Assert.False(await _sync.SyncAsync("tok", 2, CancellationToken.None));
        Assert.Equal(1, _sync.Current?.Version);
        Assert.Equal(UsbMode.Block, _usb.CurrentMode);

        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"version": 2, "usb": {"mode": "allow"}}""");
        Assert.True(await _sync.SyncAsync("tok", 2, CancellationToken.None));
        Assert.Equal(UsbMode.Allow, _usb.CurrentMode);
    }

    [Fact]
    public async Task Removed_device_propagates_to_the_worker()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Unauthorized, """{"detail": "removed"}""");

        await Assert.ThrowsAsync<DeviceRejectedException>(() => _sync.SyncAsync("tok", 2, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_apply_is_retried_on_the_next_heartbeat()
    {
        _enforcer.FailWith = new UnauthorizedAccessException("no rights");
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"version": 3, "usb": {"mode": "block"}}""");

        await _sync.SyncAsync("tok", 3, CancellationToken.None);
        Assert.Null(_usb.CurrentMode);
        Assert.Equal(0, _queue.Count());

        _enforcer.FailWith = null;
        await _sync.SyncAsync("tok", 3, CancellationToken.None);

        Assert.Equal(UsbMode.Block, _usb.CurrentMode);
        Assert.Single(_backend.Requests); // same version: re-applied from memory, not re-fetched
        Assert.Equal(EventTypes.UsbPolicyApplied, Assert.Single(_queue.PeekBatch(10)).Type);
    }

    public void Dispose() => _dir.Dispose();
}
