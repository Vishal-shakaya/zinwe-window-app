using System.Management;
using System.Runtime.InteropServices;
using Zinwe.Agent.Core.Features.DataTransfer;

namespace Zinwe.Agent.Service.Features.DataTransfer;

/// <summary>
/// Lists the removable (USB) and network destinations mounted right now. Primary source is WMI
/// <c>Win32_LogicalDisk</c> (gives the volume label and, for network drives, the UNC the letter maps to); if WMI
/// is unavailable it falls back to <see cref="DriveInfo"/> (removable drives only, no UNC). Read-only detection.
/// </summary>
internal sealed partial class SystemDriveEnumerator(ILogger<SystemDriveEnumerator> logger) : IDriveEnumerator
{
    private const uint DriveRemovable = 2;
    private const uint DriveNetwork = 4;

    public IReadOnlyList<TransferTarget> GetTargets()
    {
        try
        {
            return FromWmi();
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            LogWmiFailed(e.Message);
        }
        return FromDriveInfo();
    }

    private static List<TransferTarget> FromWmi()
    {
        var targets = new List<TransferTarget>();
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"root\cimv2"),
            new ObjectQuery("SELECT DeviceID, DriveType, VolumeName, ProviderName FROM Win32_LogicalDisk"));

        foreach (var disk in searcher.Get().Cast<ManagementObject>())
        {
            using (disk)
            {
                var driveType = Convert.ToUInt32(disk["DriveType"] ?? 0u, System.Globalization.CultureInfo.InvariantCulture);
                var deviceId = disk["DeviceID"] as string; // "E:"
                if (string.IsNullOrEmpty(deviceId))
                {
                    continue;
                }
                var root = deviceId.EndsWith(':') ? deviceId + "\\" : deviceId;
                var label = disk["VolumeName"] as string;

                if (driveType == DriveRemovable)
                {
                    targets.Add(new TransferTarget(TransferChannel.Usb, root, TransferKeys.RemovableKey(root, label)));
                }
                else if (driveType == DriveNetwork)
                {
                    var unc = disk["ProviderName"] as string;
                    targets.Add(new TransferTarget(TransferChannel.Lan, root, TransferKeys.NetworkKey(root, unc, label)));
                }
            }
        }
        return targets;
    }

    private static List<TransferTarget> FromDriveInfo()
    {
        var targets = new List<TransferTarget>();
        foreach (var drive in SafeDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }
            var root = drive.RootDirectory.FullName;
            switch (drive.DriveType)
            {
                case DriveType.Removable:
                    targets.Add(new TransferTarget(TransferChannel.Usb, root, TransferKeys.RemovableKey(root, SafeLabel(drive))));
                    break;
                case DriveType.Network:
                    targets.Add(new TransferTarget(TransferChannel.Lan, root, TransferKeys.NetworkKey(root, null, SafeLabel(drive))));
                    break;
            }
        }
        return targets;
    }

    private static DriveInfo[] SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "WMI drive enumeration failed ({Error}); falling back to DriveInfo")]
    private partial void LogWmiFailed(string error);
}
