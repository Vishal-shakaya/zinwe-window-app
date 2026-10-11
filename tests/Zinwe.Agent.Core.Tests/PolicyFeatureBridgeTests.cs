using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

/// <summary>Records each policy the bridge is asked to apply (stands in for the feature supervisor).</summary>
internal sealed class FakeFeaturePolicyApply : IFeaturePolicyApply
{
    public List<AgentPolicy> Applied { get; } = [];
    public AgentPolicy? Last => Applied.Count > 0 ? Applied[^1] : null;

    public Task ApplyAsync(AgentPolicy policy, CancellationToken ct)
    {
        Applied.Add(policy);
        return Task.CompletedTask;
    }
}

public sealed class PolicyFeatureBridgeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private readonly PolicyStore _store;
    private readonly EventQueue _queue;
    private readonly UsbProtection _usb;
    private readonly FakeFeaturePolicyApply _features = new();
    private readonly PolicySync _sync;

    public PolicyFeatureBridgeTests()
    {
        _store = new PolicyStore(_dir.Path);
        _queue = new EventQueue(_dir.Path);
        _usb = new UsbProtection(new FakeEnforcer(dryRun: true), _queue, NullLogger<UsbProtection>.Instance);
        _sync = new PolicySync(_backend.Client(), _store, _usb, NullLogger<PolicySync>.Instance, _features);
    }

    [Fact]
    public void Policy_json_round_trips_every_feature_block()
    {
        var policy = new AgentPolicy(
            7,
            new UsbPolicy(UsbMode.Block),
            new WebPolicy(true, WebMode.Block, ["malware"], ["bad.example"]),
            new DataTransferPolicy(true, 10, 20, 30, IncludeDetails: true),
            new RemoteSoftwarePolicy(true, BlockingEnabled: true, ["AnyDesk"], ["myrat"]));

        _store.Save(policy);
        var loaded = _store.Load();

        Assert.Equal(7, loaded?.Version);
        Assert.Equal(UsbMode.Block, loaded?.Usb.Mode);
        Assert.Equal(WebMode.Block, loaded?.Web?.Mode);
        Assert.Equal(["malware"], loaded?.Web?.Categories);
        Assert.Equal(["bad.example"], loaded?.Web?.CustomDomains);
        Assert.Equal(30, loaded?.DataTransfer?.EmailWarnBytes);
        Assert.True(loaded?.DataTransfer?.IncludeDetails);
        Assert.Equal(["AnyDesk"], loaded?.RemoteSoftware?.BlockedTools);
    }

    [Fact]
    public void A_policy_without_feature_blocks_loads_with_nulls()
    {
        // What today's backend sends; blocks stay null so each feature keeps its local defaults.
        File.WriteAllText(_store.FilePath, """{"version": 3, "usb": {"mode": "allow"}}""");

        var loaded = _store.Load();

        Assert.Equal(3, loaded?.Version);
        Assert.Null(loaded?.Web);
        Assert.Null(loaded?.DataTransfer);
        Assert.Null(loaded?.RemoteSoftware);
    }

    [Fact]
    public void Startup_applies_the_cached_policy_to_the_features()
    {
        _store.Save(new AgentPolicy(4, new UsbPolicy(UsbMode.Allow),
            new WebPolicy(true, WebMode.Monitor, [], [])));

        _sync.ApplyCached();

        Assert.Equal(4, _features.Last?.Version);
        Assert.NotNull(_features.Last?.Web);
    }

    [Fact]
    public async Task A_changed_policy_is_pushed_to_the_features_with_its_blocks_parsed_from_the_wire()
    {
        _store.Save(new AgentPolicy(1, new UsbPolicy(UsbMode.Allow)));
        _sync.ApplyCached();
        _features.Applied.Clear();

        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """
            {"version": 2, "usb": {"mode": "block"},
             "web": {"enabled": true, "mode": "block", "categories": ["piracy"], "custom_domains": ["x.example"]},
             "remote_software": {"enabled": true, "blocking_enabled": true, "blocked_tools": ["AnyDesk"], "extra_tool_executables": []}}
            """);

        Assert.True(await _sync.SyncAsync("tok", 2, CancellationToken.None));

        var applied = Assert.Single(_features.Applied);
        Assert.Equal(2, applied.Version);
        Assert.Equal(WebMode.Block, applied.Web?.Mode);
        Assert.Equal(["piracy"], applied.Web?.Categories);
        Assert.True(applied.RemoteSoftware?.BlockingEnabled);
        Assert.Equal(["AnyDesk"], applied.RemoteSoftware?.BlockedTools);
    }

    [Fact]
    public async Task An_unchanged_version_does_not_re_push_to_the_features()
    {
        _store.Save(new AgentPolicy(5, new UsbPolicy(UsbMode.Allow)));
        _sync.ApplyCached();
        _features.Applied.Clear();

        Assert.False(await _sync.SyncAsync("tok", 5, CancellationToken.None));
        Assert.Empty(_features.Applied);
    }

    public void Dispose() => _dir.Dispose();
}
