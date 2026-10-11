namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>
/// Settings for the suspicious / remote-access software feature, bound from config section
/// <c>Agent:Features:remote_software</c>. Every value has a sensible default in code, so the feature works with
/// nothing in appsettings. Detection is read-only and always live; BLOCKING is dry-run aware and, even when
/// enabled, only ever terminates a process for real when the agent runs as LocalSystem with
/// <c>Agent:Enforcement:DryRun=false</c> (see <see cref="RemoteSoftwareBlockerSelector"/>).
/// </summary>
public sealed class RemoteSoftwareOptions
{
    /// <summary>The config path this feature reads (under the root "Agent" section).</summary>
    public const string Section = "Agent:Features:remote_software";

    /// <summary>Master switch; false makes the module a clean no-op.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Watch running processes/ports for remote-access tools.</summary>
    public bool WatchProcesses { get; set; } = true;

    /// <summary>Diff the uninstall registry for newly installed software.</summary>
    public bool WatchInstalls { get; set; } = true;

    /// <summary>How often running processes/ports are scanned, in seconds.</summary>
    public int ProcessPollSeconds { get; set; } = 15;

    /// <summary>How often the installed-software list is diffed, in seconds. Installs change slowly; default 5 min.</summary>
    public int InstallPollSeconds { get; set; } = 300;

    /// <summary>Seed the remote-tool match list with the built-in catalog (AnyDesk, TeamViewer, …).</summary>
    public bool UseBuiltInToolList { get; set; } = true;

    /// <summary>Seed the listening-port watch list with the built-in catalog (5938, 7070, …).</summary>
    public bool UseBuiltInPortList { get; set; } = true;

    /// <summary>
    /// Extra remote-tool executable names to treat as remote-access tools (without ".exe"). Added to (or, with
    /// <see cref="UseBuiltInToolList"/> off, replacing) the built-in catalog. The display name is the entry itself.
    /// </summary>
    public List<string> ToolExecutables { get; set; } = [];

    /// <summary>
    /// Extra TCP ports to flag as remote-access listeners. Added to (or, with <see cref="UseBuiltInPortList"/>
    /// off, replacing) the built-in port catalog.
    /// </summary>
    public List<int> MonitoredPorts { get; set; } = [];

    /// <summary>
    /// Whether blocking is attempted at all. Even true only LOGS unless the agent is LocalSystem with DryRun off.
    /// The real control here is "the tool is terminated/left running" — data sent inside a remote tool's own
    /// encrypted channel cannot be read, so no transferred content is ever claimed.
    /// </summary>
    public bool BlockingEnabled { get; set; } = true;

    /// <summary>
    /// Tool names to block when seen (display name such as "AnyDesk", or an executable name). Empty by default:
    /// detect-only. Admin block/unblock will arrive from policy later; for the base app this is the block list.
    /// </summary>
    public List<string> BlockedTools { get; set; } = [];

    /// <summary>
    /// OFF by default (consent-first / DPDP): when false only the product/tool NAME, publisher and counts are
    /// recorded. Turn on to also attach a software version (installs) and the process id (remote tools) to events.
    /// Install paths and full command lines are never recorded, on or off.
    /// </summary>
    public bool IncludeDetails { get; set; }
}
