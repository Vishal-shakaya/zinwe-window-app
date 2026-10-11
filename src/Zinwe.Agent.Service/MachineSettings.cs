using Microsoft.Win32;
using Zinwe.Agent.Core;

namespace Zinwe.Agent.Service;

/// <summary>Values the MSI writes to HKLM\SOFTWARE\Zinwe\Agent.</summary>
internal static class MachineSettings
{
    public const string MemberEmailValue = "MemberEmail";
    public const string MemberIdValue = "MemberId";
    public const string BackendBaseUrlValue = "BackendBaseUrl";

    /// <summary>Registry → config keys. Only the backend URL is static config; the member credential is read live.</summary>
    public static Dictionary<string, string?> ReadConfigOverrides()
    {
        var overrides = new Dictionary<string, string?>();
        var url = ReadString(BackendBaseUrlValue);
        if (!string.IsNullOrWhiteSpace(url))
        {
            overrides[$"{AgentOptions.Section}:{nameof(AgentOptions.BackendBaseUrl)}"] = url.Trim();
        }
        return overrides;
    }

    public static string? ReadString(string name)
    {
        using var key = OpenKey(writable: false);
        return key?.GetValue(name) as string;
    }

    /// <summary>Best effort: false when the value is absent or we lack rights (e.g. running as a console app).</summary>
    public static bool TryDeleteValue(string name)
    {
        try
        {
            using var key = OpenKey(writable: true);
            if (key?.GetValue(name) is null)
            {
                return false;
            }
            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static RegistryKey? OpenKey(bool writable) =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(AgentInfo.RegistryKeyPath, writable);
}
