using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Service;

/// <summary>The latest <see cref="AgentStatus"/>: written by <see cref="AgentWorker"/>, read by <see cref="PipeServer"/>.</summary>
internal sealed class AgentStateHolder
{
    private readonly Lock _lock = new();
    private AgentStatus _status = new(AgentState.NotEnrolled, AgentInfo.Version, MachineInfo.Hostname, null, null, null);

    public AgentStatus Current => Volatile.Read(ref _status);

    public void Set(AgentState state, long? deviceId = null, DateTimeOffset? lastHeartbeatAt = null, string? message = null)
    {
        lock (_lock)
        {
            var previous = Current;
            Volatile.Write(ref _status, previous with
            {
                State = state,
                DeviceId = deviceId ?? (state == AgentState.NotEnrolled ? null : previous.DeviceId),
                LastHeartbeatAt = lastHeartbeatAt ?? previous.LastHeartbeatAt,
                Message = message,
            });
        }
    }

    /// <summary>The policy in force survives state changes: protection doesn't depend on being connected.</summary>
    public void SetPolicy(AgentPolicy? policy)
    {
        lock (_lock)
        {
            Volatile.Write(ref _status, Current with
            {
                PolicyVersion = policy?.Version,
                UsbMode = policy?.Usb.Mode.ToWire(),
            });
        }
    }
}
