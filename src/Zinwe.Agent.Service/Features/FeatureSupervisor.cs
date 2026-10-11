using Zinwe.Agent.Core.Features;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Service.Features;

/// <summary>
/// Runs the feature modules inside a dedicated DI scope and rebuilds that scope whenever the policy changes.
///
/// Why a scope: modules and their OS-facing sources are scoped, so disposing the scope cleanly stops and
/// disposes everything, and the next policy brings up FRESH instances whose options are rebuilt from the new
/// per-feature block. That sidesteps live-reconfiguration of long-lived singletons (a module's Stop disposes
/// its sources, which could never then restart). One failing module never stops the others.
///
/// Lifecycle: the host starts us (scope built from whatever <see cref="FeaturePolicyState"/> holds — null at
/// boot ⇒ appsettings defaults, so protection runs before the first sync). <see cref="PolicySync"/> then calls
/// <see cref="ApplyAsync"/> with the cached policy and on every change. Applies are serialized.
/// </summary>
internal sealed partial class FeatureSupervisor(
    IServiceProvider root,
    FeaturePolicyState state,
    ILogger<FeatureSupervisor> logger) : IHostedService, IFeaturePolicyApply
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AsyncServiceScope? _scope;
    private IReadOnlyList<IFeatureModule> _running = [];

    public Task StartAsync(CancellationToken cancellationToken) => RebuildAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Use an uncancelled wait so shutdown always tears the scope down, even if the token is already cancelled.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await StopCurrentAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAsync(AgentPolicy policy, CancellationToken ct)
    {
        state.Set(policy);
        await RebuildAsync(ct);
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await StopCurrentAsync(ct);

            var scope = root.CreateAsyncScope();
            var modules = scope.ServiceProvider.GetServices<IFeatureModule>().ToList();
            foreach (var module in modules)
            {
                try
                {
                    await module.StartAsync(ct);
                    LogStarted(module.Name);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogStartFailed(module.Name, e);
                }
            }
            _scope = scope;
            _running = modules;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCurrentAsync(CancellationToken ct)
    {
        for (var i = _running.Count - 1; i >= 0; i--)
        {
            try
            {
                await _running[i].StopAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogStopFailed(_running[i].Name, e);
            }
        }
        _running = [];

        if (_scope is { } scope)
        {
            _scope = null;
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception e)
            {
                LogScopeDisposeFailed(e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Feature module {Name} started")]
    private partial void LogStarted(string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Feature module {Name} failed to start; continuing without it")]
    private partial void LogStartFailed(string name, Exception e);

    [LoggerMessage(Level = LogLevel.Error, Message = "Feature module {Name} failed to stop cleanly")]
    private partial void LogStopFailed(string name, Exception e);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disposing the feature scope failed")]
    private partial void LogScopeDisposeFailed(Exception e);
}
