using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Metrics;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Service;

/// <summary>
/// The agent's control loop: enroll once, then heartbeat forever.
///
///   NotEnrolled ──key found──► Enrolling ──ok──► Connected ◄──► Offline (network errors, backoff)
///        ▲                         │ refused                │ 401
///        └──── new key ◄──── Rejected ◄─────────────────────┘
///
/// Enforcement hangs off this loop's policy_version, not off its health: the cached policy is applied at startup
/// before any network call, and losing the backend never switches protection off. After each successful
/// heartbeat a changed policy_version triggers <see cref="PolicySync.SyncAsync"/> (fetch → cache → apply → event).
/// </summary>
internal sealed partial class AgentWorker(
    BackendClient backend,
    IdentityStore identityStore,
    MemberCredentialSource keySource,
    PolicySync policy,
    MessageSync messages,
    MetricsUploadPump metrics,
    WorkStatusReader work,
    AgentStateHolder state,
    IOptions<AgentOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    // Short so a credential the member types in the widget is picked up within a few seconds.
    private static readonly TimeSpan WaitForKeyInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(AgentInfo.Version, options.Value.BackendBaseUrl);

        // Protection first: the last known policy goes on before we even try the network.
        state.SetPolicy(policy.ApplyCached());

        while (!stoppingToken.IsCancellationRequested)
        {
            var identity = identityStore.Load();
            if (identity is not null)
            {
                keySource.ConsumeAnyPresent();
            }
            else
            {
                identity = await EnrollAsync(stoppingToken);
            }

            if (identity is null)
            {
                await Task.Delay(WaitForKeyInterval, stoppingToken);
                continue;
            }
            await HeartbeatLoopAsync(identity, stoppingToken);
        }
    }

    /// <summary>Returns null when there is no usable member credential yet (caller waits and retries).</summary>
    private async Task<DeviceIdentity?> EnrollAsync(CancellationToken ct)
    {
        var credential = keySource.Current();
        if (credential is null)
        {
            if (state.Current.State != AgentState.Rejected)
            {
                state.Set(AgentState.NotEnrolled, message: "Waiting for your admin to finish setup.");
            }
            return null;
        }

        state.Set(AgentState.Enrolling);
        try
        {
            var response = await backend.EnrollAsync(
                new EnrollRequest(credential.Email, credential.MemberId, MachineInfo.MachineId, MachineInfo.Hostname, MachineInfo.OsVersion, AgentInfo.Version), ct);

            var identity = new DeviceIdentity(response.DeviceId, response.DeviceToken, options.Value.BackendBaseUrl, DateTimeOffset.UtcNow);
            identityStore.Save(identity);
            keySource.Consume(credential);
            LogEnrolled(response.DeviceId, response.ReEnrolled);
            return identity;
        }
        catch (EnrollmentRejectedException e)
        {
            keySource.Consume(credential); // a refused credential will never start working; wait for a new one
            state.Set(AgentState.Rejected, message: e.Message);
            LogEnrollmentRejected(e.Message);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            state.Set(AgentState.NotEnrolled, message: "Can't reach Zinwe right now. Will retry.");
            LogBackendUnreachable(e.Message);
            return null;
        }
    }

    private async Task HeartbeatLoopAsync(DeviceIdentity identity, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(options.Value.HeartbeatSeconds);
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ws = work.Read(DateTimeOffset.Now);
                var beat = await backend.HeartbeatAsync(
                    identity.DeviceToken,
                    new HeartbeatRequest(MachineInfo.Hostname, MachineInfo.OsVersion, AgentInfo.Version,
                        ws.State, ws.WorkedSeconds, ws.BreakSeconds, ws.ChangedAt, ws.BreakReason), ct);

                failures = 0;
                interval = TimeSpan.FromSeconds(Math.Clamp(beat.NextHeartbeatSeconds, 15, 3600));
                if (beat.UpdateRequired)
                {
                    // Forced upgrade (M12): protection stays on, but the widget pauses the employee's work
                    // and shows a "download the update" prompt until a newer agent reports in.
                    state.Set(AgentState.UpdateRequired, identity.DeviceId, DateTimeOffset.UtcNow,
                        message: "Update Zinwe to keep working. Protection is still on.",
                        downloadUrl: beat.DownloadUrl);
                    LogUpdateRequired(beat.DownloadUrl ?? "(none)");
                }
                else
                {
                    state.Set(AgentState.Connected, identity.DeviceId, DateTimeOffset.UtcNow);
                }
                LogHeartbeat(identity.DeviceId, beat.PolicyVersion, (int)interval.TotalSeconds);

                // Fetch failures are logged inside and retried next beat; a 401 here lands in the catch below.
                if (await policy.SyncAsync(identity.DeviceToken, beat.PolicyVersion, ct))
                {
                    state.SetPolicy(policy.Current);
                }
                await messages.SyncAsync(identity.DeviceToken, ct); // owner → member messages for the widget
                await metrics.UploadAsync(identity.DeviceToken, DateTimeOffset.Now, ct); // usage metrics for the dashboard
                await Task.Delay(interval, ct);
            }
            catch (DeviceRejectedException e)
            {
                identityStore.Delete();
                state.Set(AgentState.Rejected, message: "This PC was removed from Zinwe. Ask your admin for a new enrollment key.");
                LogDeviceRejected(identity.DeviceId, e.Message);
                return; // back to the enrollment loop, which waits for a fresh key
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failures++;
                state.Set(AgentState.Offline, identity.DeviceId, message: "Can't reach Zinwe. Protection stays on.");
                var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, interval.TotalSeconds * Math.Pow(2, failures - 1)));
                LogHeartbeatFailed(failures, (int)backoff.TotalSeconds, e.Message);
                await Task.Delay(backoff, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Zinwe agent {Version} starting; backend {BackendBaseUrl}")]
    private partial void LogStarting(string version, string backendBaseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled as device {DeviceId} (re-enrolled: {ReEnrolled})")]
    private partial void LogEnrolled(long deviceId, bool reEnrolled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment key refused: {Reason}")]
    private partial void LogEnrollmentRejected(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backend unreachable during enrollment: {Error}")]
    private partial void LogBackendUnreachable(string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat ok for device {DeviceId}; policy v{PolicyVersion}; next in {Seconds}s")]
    private partial void LogHeartbeat(long deviceId, int policyVersion, int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {DeviceId} rejected by backend ({Reason}); identity cleared")]
    private partial void LogDeviceRejected(long deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update required by backend; work paused until the agent is updated. Download: {DownloadUrl}")]
    private partial void LogUpdateRequired(string downloadUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat failed ({Failures} in a row); retrying in {Seconds}s: {Error}")]
    private partial void LogHeartbeatFailed(int failures, int seconds, string error);
}
