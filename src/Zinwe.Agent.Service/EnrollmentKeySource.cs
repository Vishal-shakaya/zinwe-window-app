using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;

namespace Zinwe.Agent.Service;

/// <summary>
/// Where the agent finds an enrollment key: the MSI's registry value (production), else
/// Agent:EnrollmentKey config (dev). Read live, so an admin can supply a key without a restart.
/// </summary>
internal sealed class EnrollmentKeySource(IOptionsMonitor<AgentOptions> options, ILogger<EnrollmentKeySource> logger)
{
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>The first key that hasn't already been used or refused, or null.</summary>
    public string? Current()
    {
        string?[] candidates = [MachineSettings.ReadString(MachineSettings.EnrollmentKeyValue), options.CurrentValue.EnrollmentKey];
        lock (_lock)
        {
            return candidates
                .Select(k => k?.Trim())
                .FirstOrDefault(k => !string.IsNullOrEmpty(k) && !_consumed.Contains(k));
        }
    }

    /// <summary>
    /// Called when the agent starts already enrolled: whatever key is still lying around is stale.
    /// Without this, a restarted agent that the owner then removes would re-join with the leftover key.
    /// </summary>
    public void ConsumeAnyPresent()
    {
        while (Current() is { } key)
        {
            Consume(key);
        }
    }

    /// <summary>
    /// Called after a successful enrollment, and after the key is refused. Removing the registry copy (and
    /// remembering the key for config-supplied ones) means a PC the owner later removes stays removed:
    /// it cannot silently re-join with a stale key.
    /// </summary>
    public void Consume(string key)
    {
        lock (_lock)
        {
            _consumed.Add(key.Trim());
        }
        if (MachineSettings.TryDeleteValue(MachineSettings.EnrollmentKeyValue))
        {
            logger.LogInformation("Removed the enrollment key from the registry.");
        }
    }
}
