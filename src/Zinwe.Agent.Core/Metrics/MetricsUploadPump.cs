using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Metrics;

/// <summary>
/// Ships rolled-up usage metrics to the backend. All current rows are uploaded (the backend replaces each
/// (bucket, category, key), so re-sending the still-accumulating current hour is safe and idempotent); once
/// uploaded, COMPLETED hours (strictly before the current one) are deleted locally. Best-effort: network
/// failures are logged and retried next round; nothing is deleted when the upload didn't succeed.
/// </summary>
public sealed class MetricsUploadPump(BackendClient backend, MetricsStore store, ILogger<MetricsUploadPump> logger)
{
    public async Task UploadAsync(string deviceToken, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<MetricRow> rows;
        try
        {
            rows = store.Snapshot();
        }
        catch (Exception e) when (e is IOException)
        {
            logger.LogDebug("Metrics snapshot unavailable: {Error}", e.Message);
            return;
        }
        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            for (var i = 0; i < rows.Count; i += BackendClient.MaxMetricsPerUpload)
            {
                var batch = rows.Skip(i).Take(BackendClient.MaxMetricsPerUpload)
                    .Select(r => new MetricUpload(r.BucketStart, r.Category, r.Key, r.Count, r.Bytes, r.Seconds))
                    .ToList();
                await backend.UploadMetricsAsync(deviceToken, batch, ct);
            }
            // Completed hours are final; drop them locally. The current hour stays and re-uploads next round.
            store.DeleteThrough(MetricsStore.HourBucket(now).AddHours(-1));
            logger.LogDebug("Uploaded {Count} metric row(s)", rows.Count);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug("Metrics upload deferred: {Error}", e.Message);
        }
    }
}
