using Zinwe.Agent.Core.Features.DataTransfer;
using Zinwe.Agent.Core.Features.RemoteSoftware;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Tests;

public sealed class PolicyMapTests
{
    [Fact]
    public void Web_null_block_keeps_local_defaults()
    {
        var defaults = new WebFeatureOptions { Enabled = true, SampleIntervalSeconds = 15 };
        Assert.Same(defaults, WebPolicyMap.FromPolicy(defaults, null));
    }

    [Fact]
    public void Web_block_wins_for_the_owner_facing_settings()
    {
        var defaults = new WebFeatureOptions { SampleIntervalSeconds = 15, CollectForegroundUsage = true, IncludeDetails = false };
        var block = new WebPolicy(Enabled: false, WebMode.Block, ["malware", "piracy"], ["bad.example"]);

        var o = WebPolicyMap.FromPolicy(defaults, block);

        Assert.False(o.Enabled);
        Assert.True(o.BlockingEnabled);                 // mode == block
        Assert.Equal(["malware", "piracy"], o.Categories);
        Assert.Equal(["bad.example"], o.CustomDomains);
        Assert.Equal(15, o.SampleIntervalSeconds);       // from defaults
        Assert.True(o.CollectForegroundUsage);           // from defaults
        Assert.False(o.IncludeDetails);                  // detail stays off
    }

    [Fact]
    public void Web_monitor_mode_does_not_block()
    {
        var o = WebPolicyMap.FromPolicy(new WebFeatureOptions(), new WebPolicy(true, WebMode.Monitor, [], []));
        Assert.False(o.BlockingEnabled);
    }

    [Fact]
    public void DataTransfer_block_sets_switches_and_thresholds_keeps_cadence()
    {
        var defaults = new DataTransferOptions { FlushSeconds = 5, DrivePollSeconds = 10, MaxDetailNames = 20 };
        var block = new DataTransferPolicy(Enabled: true, UsbWarnBytes: 1, LanWarnBytes: 2, EmailWarnBytes: 3, IncludeDetails: true);

        var o = DataTransferPolicyMap.FromPolicy(defaults, block);

        Assert.True(o.Enabled);
        Assert.Equal(1, o.UsbWarnBytes);
        Assert.Equal(2, o.LanWarnBytes);
        Assert.Equal(3, o.EmailWarnBytes);
        Assert.True(o.IncludeDetails);
        Assert.Equal(5, o.FlushSeconds);        // cadence from defaults
        Assert.Equal(20, o.MaxDetailNames);
    }

    [Fact]
    public void RemoteSoftware_block_sets_blocking_and_tools_keeps_watch_defaults()
    {
        var defaults = new RemoteSoftwareOptions { WatchProcesses = true, WatchInstalls = true, ProcessPollSeconds = 15 };
        var block = new RemoteSoftwarePolicy(Enabled: true, BlockingEnabled: true, ["AnyDesk"], ["myrat"]);

        var o = RemoteSoftwarePolicyMap.FromPolicy(defaults, block);

        Assert.True(o.Enabled);
        Assert.True(o.BlockingEnabled);
        Assert.Equal(["AnyDesk"], o.BlockedTools);
        Assert.Equal(["myrat"], o.ToolExecutables);
        Assert.True(o.WatchProcesses);          // from defaults
        Assert.Equal(15, o.ProcessPollSeconds);  // from defaults
    }

    [Fact]
    public void FeaturePolicyState_holds_the_latest()
    {
        var state = new Zinwe.Agent.Core.Features.FeaturePolicyState();
        Assert.Null(state.Current);
        var p = new AgentPolicy(2, new UsbPolicy(UsbMode.Allow));
        state.Set(p);
        Assert.Same(p, state.Current);
    }
}
