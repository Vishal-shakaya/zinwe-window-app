namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>
/// A product found in the Windows "uninstall" registry (the list Settings &gt; Apps shows). Carries only the
/// owner-recognisable identity — name, publisher, version — never an install path or any file list.
/// </summary>
/// <param name="Name">Display name, e.g. "AnyDesk" or "7-Zip 24.08".</param>
/// <param name="Publisher">Vendor as the installer recorded it, when present.</param>
/// <param name="Version">Display version, when present (surfaced on events only with IncludeDetails).</param>
public sealed record InstalledProduct(string Name, string? Publisher = null, string? Version = null);

/// <summary>A process running on this PC right now (read-only detection).</summary>
/// <param name="Name">Process name without ".exe", e.g. "anydesk", "teamviewer".</param>
/// <param name="Pid">OS process id; used only to target a (dry-run-aware) block, never recorded by default.</param>
public sealed record RunningProcess(string Name, int Pid);

/// <summary>A TCP endpoint in the LISTEN state, with the owning process when the OS let us resolve it.</summary>
/// <param name="Port">The local listening port.</param>
/// <param name="Pid">Owning process id, or 0 when it could not be resolved.</param>
/// <param name="ProcessName">Owning process name without ".exe", when resolved; else null.</param>
public sealed record ListeningPort(int Port, int Pid = 0, string? ProcessName = null);

/// <summary>How a remote-access tool was spotted on this scan.</summary>
public enum RemoteDetection
{
    /// <summary>A running process matched a known remote-tool executable name.</summary>
    Process,

    /// <summary>A known remote-tool listening port was open.</summary>
    Port,
}

/// <summary>
/// Lists the software currently recorded in the Windows uninstall registry. The service reads HKLM/HKCU;
/// tests use a fake. Read-only detection, safe to run live.
/// </summary>
public interface IInstalledSoftwareSource
{
    IReadOnlyList<InstalledProduct> GetInstalled();
}

/// <summary>Lists the processes running now. The service uses <c>Process.GetProcesses</c>; tests use a fake.</summary>
public interface IProcessSource
{
    IReadOnlyList<RunningProcess> GetProcesses();
}

/// <summary>Lists the TCP ports in LISTEN state. The service uses iphlpapi; tests use a fake. Read-only.</summary>
public interface IListeningPortSource
{
    IReadOnlyList<ListeningPort> GetListeningPorts();
}
