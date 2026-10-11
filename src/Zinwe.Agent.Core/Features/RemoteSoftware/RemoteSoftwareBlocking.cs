using Microsoft.Extensions.Logging;

namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>What a block attempt did (or, in test mode, would have done).</summary>
/// <param name="Attempted">How many processes were targeted.</param>
/// <param name="Terminated">How many were actually terminated (always 0 in dry run).</param>
public sealed record RemoteBlockResult(int Attempted, int Terminated);

/// <summary>
/// Terminates a process by id. Abstracted (like <c>IRegistryWriter</c> / <c>IHostsFileWriter</c>) so no test and
/// no dev run can ever kill a real process: the only implementation that does lives in the service and is built
/// solely by <see cref="RemoteSoftwareBlockerSelector"/> when the agent is LocalSystem with dry run off.
/// </summary>
public interface IProcessTerminator
{
    /// <summary>Kill the process with id <paramref name="pid"/>. Throws on failure; the blocker catches and logs.</summary>
    void Terminate(int pid);
}

/// <summary>Prevents a listed remote tool from staying open by terminating its processes (dry-run aware).</summary>
public interface IRemoteSoftwareBlocker
{
    /// <summary>True when nothing on the PC is changed (test mode); events carry this as "dry_run".</summary>
    bool IsDryRun { get; }

    /// <summary>
    /// Attempt to block <paramref name="toolName"/> by terminating <paramref name="targets"/>. Never throws; a
    /// per-process failure is logged and counted as attempted-but-not-terminated.
    /// </summary>
    RemoteBlockResult Block(string toolName, IReadOnlyList<RunningProcess> targets);
}

/// <summary>
/// The real blocker: terminates matching processes. WDAC/AppLocker launch-prevention needs machine-policy writes
/// (HKLM), which the base app deliberately does NOT do, so termination is the honest control here. Only built by
/// <see cref="RemoteSoftwareBlockerSelector"/> as LocalSystem with dry run off.
/// </summary>
public sealed class ProcessKillRemoteSoftwareBlocker(IProcessTerminator terminator, ILogger<ProcessKillRemoteSoftwareBlocker> logger)
    : IRemoteSoftwareBlocker
{
    public bool IsDryRun => false;

    public RemoteBlockResult Block(string toolName, IReadOnlyList<RunningProcess> targets)
    {
        var terminated = 0;
        foreach (var target in targets)
        {
            try
            {
                terminator.Terminate(target.Pid);
                terminated++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The process may have exited, or be protected; count it as attempted and move on.
                logger.LogWarning(e, "Could not terminate {Tool} process {Pid}", toolName, target.Pid);
            }
        }
        logger.LogInformation("Blocked {Tool}: terminated {Terminated}/{Attempted} process(es)",
            toolName, terminated, targets.Count);
        return new RemoteBlockResult(targets.Count, terminated);
    }
}

/// <summary>Test mode: logs exactly what it would terminate and changes nothing.</summary>
public sealed class DryRunRemoteSoftwareBlocker(ILogger<DryRunRemoteSoftwareBlocker> logger) : IRemoteSoftwareBlocker
{
    public bool IsDryRun => true;

    public RemoteBlockResult Block(string toolName, IReadOnlyList<RunningProcess> targets)
    {
        var pids = string.Join(", ", targets.Select(t => t.Pid));
        logger.LogInformation("[dry run] would terminate {Count} {Tool} process(es): {Pids}",
            targets.Count, toolName, pids.Length == 0 ? "(none)" : pids);
        return new RemoteBlockResult(targets.Count, 0);
    }
}

/// <summary>
/// SAFETY RULE (identical to <c>UsbEnforcerSelector</c> / <c>WebBlockerSelector</c>): the process-killing blocker
/// is used only when the process is LocalSystem AND Agent:Enforcement:DryRun is false. A developer's
/// <c>dotnet run</c>, a test, or a misconfigured box always gets the dry-run blocker, so nothing short of the
/// installed service can terminate a process.
/// </summary>
public static class RemoteSoftwareBlockerSelector
{
    public static IRemoteSoftwareBlocker Select(
        bool runningAsSystem,
        bool dryRunConfigured,
        Func<IRemoteSoftwareBlocker> createRealBlocker,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(RemoteSoftwareBlockerSelector));
        if (dryRunConfigured)
        {
            logger.LogWarning("Agent:Enforcement:DryRun is on: remote-tool blocking will be logged, not applied.");
            return new DryRunRemoteSoftwareBlocker(loggers.CreateLogger<DryRunRemoteSoftwareBlocker>());
        }
        if (!runningAsSystem)
        {
            logger.LogWarning("Not running as LocalSystem: remote-tool blocking will be logged, not applied (test mode).");
            return new DryRunRemoteSoftwareBlocker(loggers.CreateLogger<DryRunRemoteSoftwareBlocker>());
        }
        return createRealBlocker();
    }
}
