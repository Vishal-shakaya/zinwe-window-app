using System.Text.RegularExpressions;

namespace Zinwe.Agent.Core.Usb;

/// <summary>A USB storage device was plugged in.</summary>
/// <param name="DeviceName">Human name, e.g. "SanDisk Ultra".</param>
/// <param name="DriveLetter">e.g. "E:" when Windows has mounted it in time; often null for a blocked drive.</param>
public sealed record UsbDeviceArrival(string DeviceName, string? DriveLetter = null, string? Vendor = null, string? Serial = null);

/// <summary>Reports USB storage arrivals. The service uses WMI; tests use a fake.</summary>
public interface IUsbDeviceWatcher : IDisposable
{
    /// <summary>Raised on a background thread.</summary>
    event EventHandler<UsbDeviceArrival>? DeviceArrived;

    /// <summary>Throws if the OS facility is unavailable (the caller logs and carries on without it).</summary>
    void Start();
}

/// <summary>Turns raw Windows device strings into what an owner would recognise.</summary>
public static partial class UsbDeviceNames
{
    /// <summary>"SanDisk Ultra USB Device" → "SanDisk Ultra".</summary>
    public static string CleanModel(string? model)
    {
        var name = (model ?? "").Trim();
        foreach (var suffix in new[] { " USB Device", " USB Disk Device" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].TrimEnd();
            }
        }
        return name.Length == 0 ? "USB drive" : name;
    }

    /// <summary>"USBSTOR\DISK&amp;VEN_SANDISK&amp;PROD_ULTRA&amp;REV_1.00\4C53…&amp;0" → "SANDISK".</summary>
    public static string? VendorFromPnpId(string? pnpDeviceId)
    {
        var match = pnpDeviceId is null ? Match.Empty : VendorPattern().Match(pnpDeviceId);
        if (!match.Success)
        {
            return null;
        }
        var vendor = match.Groups[1].Value.Replace('_', ' ').Trim();
        return vendor.Length == 0 ? null : vendor;
    }

    /// <summary>The instance segment of a USBSTOR id (the drive's serial plus "&amp;0"), or null.</summary>
    public static string? SerialFromPnpId(string? pnpDeviceId)
    {
        if (pnpDeviceId is null || !pnpDeviceId.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var last = pnpDeviceId[(pnpDeviceId.LastIndexOf('\\') + 1)..];
        var amp = last.LastIndexOf('&');
        var serial = (amp > 0 ? last[..amp] : last).Trim();
        return serial.Length == 0 ? null : serial;
    }

    [GeneratedRegex(@"VEN_([^&\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorPattern();
}
