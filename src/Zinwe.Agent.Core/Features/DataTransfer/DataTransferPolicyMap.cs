using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>Turns the owner's <see cref="DataTransferPolicy"/> block into <see cref="DataTransferOptions"/>.</summary>
public static class DataTransferPolicyMap
{
    /// <summary>
    /// A null <paramref name="block"/> keeps the appsettings <paramref name="defaults"/>. Otherwise the owner
    /// sets the master switch, the three warn-size thresholds and the off-by-default filename-detail flag;
    /// the polling/flush cadences and the detail-name cap stay on their local defaults.
    /// </summary>
    public static DataTransferOptions FromPolicy(DataTransferOptions defaults, DataTransferPolicy? block)
    {
        if (block is null)
        {
            return defaults;
        }
        return new DataTransferOptions
        {
            Enabled = block.Enabled,
            FlushSeconds = defaults.FlushSeconds,
            DrivePollSeconds = defaults.DrivePollSeconds,
            EmailPollSeconds = defaults.EmailPollSeconds,
            UsbWarnBytes = block.UsbWarnBytes,
            LanWarnBytes = block.LanWarnBytes,
            EmailWarnBytes = block.EmailWarnBytes,
            IncludeDetails = block.IncludeDetails,
            UsbLockdownOverLimit = block.UsbLockdown,
            MaxDetailNames = defaults.MaxDetailNames,
        };
    }
}
