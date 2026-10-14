using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

public sealed class ConsentReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-consent-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void No_file_means_no_consent()
    {
        Assert.Null(new ConsentReader(_dir).Read());
    }

    [Fact]
    public void Reads_version_and_time()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "consent.json"),
            """{"Version":"2026-10-14","AcceptedAt":"2026-10-15T09:30:00+05:30"}""");
        var record = new ConsentReader(_dir).Read();
        Assert.NotNull(record);
        Assert.Equal("2026-10-14", record!.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.FromHours(5.5)), record.AcceptedAt);
    }

    [Fact]
    public void Garbage_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "consent.json"), "{ not json");
        Assert.Null(new ConsentReader(_dir).Read());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
