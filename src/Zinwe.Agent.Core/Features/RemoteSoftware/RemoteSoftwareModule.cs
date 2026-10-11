using Microsoft.Extensions.Logging;

namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>
/// The "remote_software" feature module: on start it begins two poll loops — one diffing installed software for
/// NEW installs, one matching running processes/ports against the remote-access tool catalog (and, for listed
/// tools, driving the dry-run-aware blocker). Detection is read-only and always live; the only mutating path is
/// blocking, which honours the LocalSystem + DryRun safety rule inside <see cref="IRemoteSoftwareBlocker"/>.
/// Never throws out of Start/Stop (the host logs and carries on).
/// </summary>
public sealed class RemoteSoftwareModule(
    IInstalledSoftwareSource installedSource,
    IProcessSource processSource,
    IListeningPortSource portSource,
    InstallMonitor installMonitor,
    RemoteToolMonitor toolMonitor,
    RemoteSoftwareOptions options,
    ILogger<RemoteSoftwareModule> logger) : IFeatureModule
{
    private CancellationTokenSource? _cts;
    private Task? _installLoop;
    private Task? _processLoop;

    public string Name => "remote_software";

    public Task StartAsync(CancellationToken ct)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("remote_software is disabled by config; not watching anything");
            return Task.CompletedTask;
        }
        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (options.WatchInstalls)
            {
                _installLoop = Task.Run(() => InstallLoopAsync(_cts.Token), CancellationToken.None);
            }
            if (options.WatchProcesses)
            {
                _processLoop = Task.Run(() => ProcessLoopAsync(_cts.Token), CancellationToken.None);
            }
            logger.LogInformation("remote_software watching installs and remote-access tools ({Mode})",
                toolMonitor.IsDryRun ? "detect/test-mode block" : "detect/enforced block");
        }
        catch (Exception e)
        {
            logger.LogError(e, "remote_software failed to start; continuing without it");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync();
            }
            await WhenAllSafe(_installLoop, _processLoop);
        }
        catch (Exception e)
        {
            logger.LogError(e, "remote_software did not stop cleanly");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>One install scan. Internal so a test can drive a single pass without the timer.</summary>
    internal void ScanInstallsOnce(DateTimeOffset? at = null)
    {
        try
        {
            installMonitor.Scan(installedSource.GetInstalled(), at);
        }
        catch (Exception e)
        {
            logger.LogError(e, "remote_software install scan failed");
        }
    }

    /// <summary>One process/port scan. Internal so a test can drive a single pass without the timer.</summary>
    internal void ScanProcessesOnce(DateTimeOffset? at = null)
    {
        try
        {
            toolMonitor.Scan(processSource.GetProcesses(), portSource.GetListeningPorts(), at);
        }
        catch (Exception e)
        {
            logger.LogError(e, "remote_software process scan failed");
        }
    }

    private async Task InstallLoopAsync(CancellationToken ct)
    {
        ScanInstallsOnce(); // seed the baseline immediately on start
        var period = TimeSpan.FromSeconds(Math.Max(5, options.InstallPollSeconds));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                ScanInstallsOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, options.ProcessPollSeconds));
        using var timer = new PeriodicTimer(period);
        ScanProcessesOnce();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                ScanProcessesOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private static async Task WhenAllSafe(params Task?[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks.Where(t => t is not null)!);
        }
        catch (OperationCanceledException)
        {
            // expected on cancel
        }
    }
}
