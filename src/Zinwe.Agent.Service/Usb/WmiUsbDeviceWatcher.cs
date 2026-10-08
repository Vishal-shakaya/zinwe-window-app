using System.Management;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Usb;

/// <summary>
/// WMI: a Win32_DiskDrive with InterfaceType 'USB' appearing = a USB storage device was plugged in.
/// The disk object appears even when Removable Storage Access denies reads, so blocked drives are reported too.
/// </summary>
internal sealed partial class WmiUsbDeviceWatcher(ILogger<WmiUsbDeviceWatcher> logger) : IUsbDeviceWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private ManagementEventWatcher? _watcher;

    public event EventHandler<UsbDeviceArrival>? DeviceArrived;

    public void Start()
    {
        var query = new WqlEventQuery(
            "__InstanceCreationEvent", PollInterval,
            "TargetInstance ISA 'Win32_DiskDrive' AND TargetInstance.InterfaceType = 'USB'");
        _watcher = new ManagementEventWatcher(new ManagementScope(@"root\cimv2"), query);
        _watcher.EventArrived += OnEventArrived;
        _watcher.Start();
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var disk = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pnpId = disk["PNPDeviceID"] as string;
            var serial = (disk["SerialNumber"] as string)?.Trim();
            var arrival = new UsbDeviceArrival(
                UsbDeviceNames.CleanModel(disk["Model"] as string),
                TryFindDriveLetter(disk["DeviceID"] as string),
                UsbDeviceNames.VendorFromPnpId(pnpId),
                string.IsNullOrEmpty(serial) ? UsbDeviceNames.SerialFromPnpId(pnpId) : serial);
            DeviceArrived?.Invoke(this, arrival);
        }
        catch (Exception ex)
        {
            LogArrivalFailed(ex); // WMI callback thread: never throw
        }
    }

    /// <summary>Disk → partitions → logical disks. Windows may mount a moment after the disk appears, so retry briefly.</summary>
    private string? TryFindDriveLetter(string? diskDeviceId)
    {
        if (string.IsNullOrEmpty(diskDeviceId))
        {
            return null;
        }
        var escapedDisk = diskDeviceId.Replace(@"\", @"\\", StringComparison.Ordinal);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var partitions = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escapedDisk}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                foreach (var partition in partitions.Get().Cast<ManagementObject>())
                {
                    using (partition)
                    {
                        using var volumes = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                        foreach (var volume in volumes.Get().Cast<ManagementObject>())
                        {
                            using (volume)
                            {
                                if (volume["DeviceID"] is string letter && letter.Length > 0)
                                {
                                    return letter;
                                }
                            }
                        }
                    }
                }
            }
            catch (ManagementException ex)
            {
                LogDriveLetterLookupFailed(ex.Message);
                return null;
            }
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
        return null;
    }

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }
        try
        {
            _watcher.EventArrived -= OnEventArrived;
            _watcher.Stop();
        }
        catch (ManagementException)
        {
            // already stopped
        }
        _watcher.Dispose();
        _watcher = null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read a USB arrival from WMI")]
    private partial void LogArrivalFailed(Exception e);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Drive-letter lookup failed: {Error}")]
    private partial void LogDriveLetterLookupFailed(string error);
}
