using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Features;

/// <summary>
/// The owner's current policy, shared so each feature module's options can be built from it when the feature
/// supervisor (re)creates its scope. A null policy means "no backend config yet" — modules fall back to their
/// local appsettings defaults, so protection still runs before the first sync.
/// </summary>
public sealed class FeaturePolicyState
{
    private AgentPolicy? _current;

    public AgentPolicy? Current => Volatile.Read(ref _current);

    public void Set(AgentPolicy? policy) => Volatile.Write(ref _current, policy);
}
