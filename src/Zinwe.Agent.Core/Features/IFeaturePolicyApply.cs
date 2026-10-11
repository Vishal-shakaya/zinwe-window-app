using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Features;

/// <summary>
/// Applies a freshly received policy to the feature modules. Implemented by the service's feature supervisor,
/// which records it in <see cref="FeaturePolicyState"/> and rebuilds the feature scope so each module restarts
/// with the new per-feature config. <see cref="PolicySync"/> calls this at startup and on every policy change.
/// Implementations never throw — a feature that fails to (re)start is logged and the rest carry on.
/// </summary>
public interface IFeaturePolicyApply
{
    Task ApplyAsync(AgentPolicy policy, CancellationToken ct);
}
