namespace Zinwe.Agent.Core;

/// <summary>
/// Bound from the "Agent" config section. Sources, lowest to highest priority:
/// appsettings.json, appsettings.{Environment}.json, HKLM\SOFTWARE\Zinwe\Agent, ZINWE_Agent__* env vars.
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Django backend root, e.g. https://api.zinwe.io (no /api suffix).</summary>
    public string BackendBaseUrl { get; set; } = "";

    /// <summary>Optional: dev/test only. Production keys come from the MSI via the registry.</summary>
    public string? EnrollmentKey { get; set; }

    /// <summary>Used until the backend tells us otherwise in a heartbeat response.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    public string DataDirectory { get; set; } = AgentInfo.DefaultDataDirectory;

    public EnforcementOptions Enforcement { get; set; } = new();
}

/// <summary>Agent:Enforcement.</summary>
public sealed class EnforcementOptions
{
    /// <summary>
    /// True = log what would change, change nothing (dev boxes: appsettings.Development.json sets it).
    /// Even when false, real enforcement also requires the process to be LocalSystem; see UsbEnforcerSelector.
    /// </summary>
    public bool DryRun { get; set; }
}
