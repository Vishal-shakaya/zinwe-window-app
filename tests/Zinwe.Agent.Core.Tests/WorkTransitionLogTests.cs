using System.Net;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

/// <summary>Every change in the widget reaches the backend once, even short ones and ones made offline.</summary>
public sealed class WorkTransitionLogTests : IDisposable
{
    private readonly TempDir _widget = new();
    private readonly TempDir _agent = new();
    private readonly WorkTransitionLog _log;
    private static readonly DateTimeOffset Today = DateTimeOffset.Now.Date.AddHours(9); // local 09:00 today

    public WorkTransitionLogTests()
    {
        Directory.CreateDirectory(_widget.Path);
        _log = new WorkTransitionLog(new LocalWidgetFolder(_widget.Path), _agent.Path);
    }

    private void Press(DateTimeOffset at, string action, string state, string? breakReason = null, string? breakNote = null,
        string? endReason = null, string? endNote = null) =>
        File.AppendAllText(Path.Combine(_widget.Path, "work-timing-log.jsonl"), JsonSerializer.Serialize(new
        {
            At = at, Action = action, State = state, WorkedSeconds = 0, BreakSeconds = 0,
            BreakReason = breakReason, BreakNote = breakNote, EndReason = endReason, EndNote = endNote,
        }) + Environment.NewLine);

    [Fact]
    public void A_quick_sequence_is_sent_in_full_with_reasons_where_they_belong()
    {
        Press(Today, "start", "Working");
        Press(Today.AddSeconds(20), "break", "OnBreak", "Lunch", "quick bite");
        Press(Today.AddSeconds(40), "resume", "Working");
        Press(Today.AddSeconds(60), "end", "Ended", endReason: "Leaving early", endNote: "Doctor");

        var events = _log.Pending(Today.AddMinutes(2));

        Assert.Equal(["working", "on_break", "working", "ended"], events.Select(e => e.State));
        Assert.Equal(("Lunch", "quick bite"), (events[1].Reason, events[1].Note));
        Assert.Null(events[2].Reason);
        Assert.Equal(("Leaving early", "Doctor"), (events[3].Reason, events[3].Note));
    }

    [Fact]
    public void Once_confirmed_they_are_not_sent_again_but_later_ones_are()
    {
        Press(Today, "start", "Working");
        var first = _log.Pending(Today.AddMinutes(1));
        _log.Acknowledge(first);

        Assert.Empty(_log.Pending(Today.AddMinutes(1)));
        Press(Today.AddMinutes(5), "break", "OnBreak", "Tea / coffee");
        Assert.Equal("on_break", Assert.Single(_log.Pending(Today.AddMinutes(6))).State);
    }

    [Fact]
    public void Unconfirmed_changes_stay_pending_like_while_offline()
    {
        Press(Today, "start", "Working");
        Press(Today.AddMinutes(30), "break", "OnBreak", "Meeting");
        // No Acknowledge (the heartbeat failed): both are still pending next time.
        Assert.Equal(2, _log.Pending(Today.AddHours(1)).Count);
        Assert.Equal(2, _log.Pending(Today.AddHours(2)).Count);
    }

    [Fact]
    public void A_first_run_sends_only_today_and_skips_a_half_written_line()
    {
        Press(Today.AddDays(-1), "start", "Working");                       // yesterday: reported the old way
        Press(Today, "start", "Working");
        File.AppendAllText(Path.Combine(_widget.Path, "work-timing-log.jsonl"), "{\"At\":\"2026-");  // mid-write

        Assert.Single(_log.Pending(Today.AddMinutes(1)));
    }

    [Fact]
    public void The_fingerprint_changes_when_the_widget_logs_a_press()
    {
        var before = _log.Fingerprint();
        Press(Today, "start", "Working");
        Assert.NotEqual(before, _log.Fingerprint());
    }

    [Fact]
    public async Task The_heartbeat_carries_them_as_work_events()
    {
        var backend = new FakeBackend
        {
            Respond = _ => FakeBackend.Json(HttpStatusCode.OK,
                """{"device_id":1,"server_time":"2026-10-15T09:00:00Z","next_heartbeat_seconds":60,"policy_version":1}"""),
        };
        Press(Today, "break", "OnBreak", "Lunch");
        await backend.Client().HeartbeatAsync("tok",
            new HeartbeatRequest("pc", "win", "0.2.0", "on_break", 0, 0, WorkEvents: _log.Pending(Today.AddMinutes(1))),
            CancellationToken.None);

        var body = JsonDocument.Parse(backend.Requests.Single().Body).RootElement;
        var sent = body.GetProperty("work_events")[0];
        Assert.Equal("on_break", sent.GetProperty("state").GetString());
        Assert.Equal("Lunch", sent.GetProperty("reason").GetString());
        Assert.True(sent.TryGetProperty("at", out _));
    }

    public void Dispose()
    {
        _widget.Dispose();
        _agent.Dispose();
    }
}
