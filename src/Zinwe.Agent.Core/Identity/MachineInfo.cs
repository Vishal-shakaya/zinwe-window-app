using Microsoft.Win32;

namespace Zinwe.Agent.Core.Identity;

/// <summary>Facts about this PC that the backend uses to recognise it.</summary>
public static class MachineInfo
{
    public static string Hostname => Environment.MachineName;

    /// <summary>
    /// HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid: stable for the life of the Windows install,
    /// so an agent reinstall re-enrolls into the same Device row instead of creating a duplicate.
    /// </summary>
    public static string MachineId
    {
        get
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(guid)
                ? throw new InvalidOperationException("Windows MachineGuid is missing; cannot identify this PC.")
                : guid.Trim().ToLowerInvariant();
        }
    }

    /// <summary>e.g. "Windows 11 Pro 24H2 (26100)".</summary>
    public static string OsVersion
    {
        get
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string;
            var build = Environment.OSVersion.Version.Build;
            // Windows 11 still reports "Windows 10 ..." in ProductName; the build number is authoritative.
            if (build >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            }
            return string.IsNullOrEmpty(display) ? $"{product} ({build})" : $"{product} {display} ({build})";
        }
    }
}
