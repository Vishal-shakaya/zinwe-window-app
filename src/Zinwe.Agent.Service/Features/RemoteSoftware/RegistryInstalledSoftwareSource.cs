using System.Runtime.Versioning;
using Microsoft.Win32;
using Zinwe.Agent.Core.Features.RemoteSoftware;

namespace Zinwe.Agent.Service.Features.RemoteSoftware;

/// <summary>
/// Reads the standard Windows "uninstall" registry (the same list Settings &gt; Apps shows) under HKLM (both the
/// 64-bit and 32-bit views) and HKCU. READ-ONLY detection: it opens keys read-only and never writes. Only the
/// display name, publisher and version are read — never the install location or any file list.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class RegistryInstalledSoftwareSource(ILogger<RegistryInstalledSoftwareSource> logger)
    : IInstalledSoftwareSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View)[] Roots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    public IReadOnlyList<InstalledProduct> GetInstalled()
    {
        // De-dupe across the three hives/views: the same product often appears in more than one.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var products = new List<InstalledProduct>();

        foreach (var (hive, view) in Roots)
        {
            try
            {
                ReadRoot(hive, view, seen, products);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                LogRootFailed(hive.ToString(), e.Message);
            }
        }
        return products;
    }

    private static void ReadRoot(
        RegistryHive hive, RegistryView view, HashSet<string> seen, List<InstalledProduct> products)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
        if (uninstall is null)
        {
            return;
        }

        foreach (var subName in uninstall.GetSubKeyNames())
        {
            using var entry = SafeOpen(uninstall, subName);
            if (entry is null)
            {
                continue;
            }

            var name = (entry.GetValue("DisplayName") as string)?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue; // updates/components with no display name are not "apps" the owner would recognise
            }
            // Skip system/OS update entries (KBxxxxxxx) and anything flagged as a system component or child update.
            if (IsHidden(entry))
            {
                continue;
            }

            var publisher = (entry.GetValue("Publisher") as string)?.Trim();
            var version = (entry.GetValue("DisplayVersion") as string)?.Trim();
            var key = $"{name.ToLowerInvariant()}|{(version ?? "").ToLowerInvariant()}";
            if (seen.Add(key))
            {
                products.Add(new InstalledProduct(name, NullIfEmpty(publisher), NullIfEmpty(version)));
            }
        }
    }

    private static bool IsHidden(RegistryKey entry)
    {
        if (entry.GetValue("SystemComponent") is int sys && sys == 1)
        {
            return true;
        }
        if (entry.GetValue("ParentKeyName") is string parent && parent.Length > 0)
        {
            return true; // a sub-update of another product
        }
        return false;
    }

    private static RegistryKey? SafeOpen(RegistryKey parent, string subName)
    {
        try
        {
            return parent.OpenSubKey(subName, writable: false);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read installed software from {Hive} ({Error})")]
    private partial void LogRootFailed(string hive, string error);
}
