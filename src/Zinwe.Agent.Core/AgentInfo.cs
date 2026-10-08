using System.Reflection;

namespace Zinwe.Agent.Core;

/// <summary>Identity constants shared by the service, the tray app and the installer.</summary>
public static class AgentInfo
{
    /// <summary>Windows service name. Must match ServiceInstall/@Name in installer/Package.wxs.</summary>
    public const string ServiceName = "ZinweAgent";

    /// <summary>HKLM\SOFTWARE\Zinwe\Agent -- written by the MSI (EnrollmentKey, BackendBaseUrl).</summary>
    public const string RegistryKeyPath = @"SOFTWARE\Zinwe\Agent";

    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    /// <summary>%ProgramData%\Zinwe\Agent -- device identity and local state.</summary>
    public static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zinwe", "Agent");
}
