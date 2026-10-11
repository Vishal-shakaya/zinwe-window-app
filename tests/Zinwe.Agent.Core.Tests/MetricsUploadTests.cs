using System.Net;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Tests;

public sealed class MetricsUploadTests
{
    private static readonly MetricUpload Row =
        new(new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), "web.domain", "news.example", 3, 2048, 75);

    [Fact]
    public async Task Uploads_snake_case_with_device_auth_and_parses_accepted()
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"accepted": 3}""") };
        var client = backend.Client();

        var res = await client.UploadMetricsAsync("tok", [Row], CancellationToken.None);

        Assert.Equal(3, res.Accepted);
        var seen = Assert.Single(backend.Requests);
        Assert.Equal("/api/devices/metrics/upload/", seen.Path);
        Assert.Equal("Device", seen.AuthScheme);
        Assert.Contains("\"bucket_start\"", seen.Body);
        Assert.Contains("\"category\":\"web.domain\"", seen.Body);
        Assert.Contains("\"seconds\":75", seen.Body);
    }

    [Fact]
    public async Task Rejects_an_empty_batch()
    {
        var client = new FakeBackend().Client();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.UploadMetricsAsync("tok", [], CancellationToken.None));
    }
}
