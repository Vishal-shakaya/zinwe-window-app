using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

public sealed class WorkStatusReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-work-" + Guid.NewGuid().ToString("N"));
    private readonly WorkStatusReader _reader;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.Zero);

    public WorkStatusReaderTests() => _reader = new WorkStatusReader(_dir);

    private void WriteTiming(string date, string state, long worked, long brk)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "work-timing.json"),
            $$"""{"Date":"{{date}}","State":"{{state}}","WorkedSeconds":{{worked}},"BreakSeconds":{{brk}}}""");
    }

    [Fact]
    public void Missing_file_is_not_started()
    {
        Assert.Equal(WorkStatus.NotStarted, _reader.Read(Now));
    }

    [Fact]
    public void Reads_todays_working_state()
    {
        WriteTiming("2026-10-10", "Working", 120, 30);
        var s = _reader.Read(Now);
        Assert.Equal("working", s.State);
        Assert.Equal(120, s.WorkedSeconds);
        Assert.Equal(30, s.BreakSeconds);
    }

    [Theory]
    [InlineData("OnBreak", "on_break")]
    [InlineData("Ended", "ended")]
    public void Maps_state_names_to_wire(string stored, string wire)
    {
        WriteTiming("2026-10-10", stored, 0, 0);
        Assert.Equal(wire, _reader.Read(Now).State);
    }

    [Fact]
    public void Break_reports_when_it_began_and_why()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "work-timing.json"),
            """{"Date":"2026-10-10","State":"OnBreak","WorkedSeconds":60,"BreakSeconds":5,"LastTransitionAtUtc":"2026-10-10T13:30:00+00:00","BreakReason":"Lunch"}""");
        var s = _reader.Read(Now);
        Assert.Equal("on_break", s.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 13, 30, 0, TimeSpan.Zero), s.ChangedAt);
        Assert.Equal("Lunch", s.BreakReason);
    }

    [Fact]
    public void A_reason_is_only_reported_while_on_break()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "work-timing.json"),
            """{"Date":"2026-10-10","State":"Working","WorkedSeconds":60,"BreakSeconds":5,"BreakReason":"Lunch"}""");
        Assert.Null(_reader.Read(Now).BreakReason);
    }

    [Fact]
    public void A_stale_day_reads_as_not_started()
    {
        WriteTiming("2026-10-09", "Working", 999, 10);
        Assert.Equal(WorkStatus.NotStarted, _reader.Read(Now));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
