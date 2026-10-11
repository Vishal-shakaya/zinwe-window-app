using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>Turns the owner's <see cref="RemoteSoftwarePolicy"/> block into <see cref="RemoteSoftwareOptions"/>.</summary>
public static class RemoteSoftwarePolicyMap
{
    /// <summary>
    /// A null <paramref name="block"/> keeps the appsettings <paramref name="defaults"/>. Otherwise the owner
    /// sets the master switch, whether blocking is attempted and which tools to block / also watch for; the
    /// watch flags, poll cadences, built-in catalog toggles and detail flag stay on their local defaults.
    /// Blocking still only terminates for real as LocalSystem with DryRun off — the selector enforces that.
    /// </summary>
    public static RemoteSoftwareOptions FromPolicy(RemoteSoftwareOptions defaults, RemoteSoftwarePolicy? block)
    {
        if (block is null)
        {
            return defaults;
        }
        return new RemoteSoftwareOptions
        {
            Enabled = block.Enabled,
            WatchProcesses = defaults.WatchProcesses,
            WatchInstalls = defaults.WatchInstalls,
            ProcessPollSeconds = defaults.ProcessPollSeconds,
            InstallPollSeconds = defaults.InstallPollSeconds,
            UseBuiltInToolList = defaults.UseBuiltInToolList,
            UseBuiltInPortList = defaults.UseBuiltInPortList,
            ToolExecutables = [.. block.ExtraToolExecutables],
            MonitoredPorts = [.. defaults.MonitoredPorts],
            BlockingEnabled = block.BlockingEnabled,
            BlockedTools = [.. block.BlockedTools],
            IncludeDetails = defaults.IncludeDetails,
        };
    }
}
