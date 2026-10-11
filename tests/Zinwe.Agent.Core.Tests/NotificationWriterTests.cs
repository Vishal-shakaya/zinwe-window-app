using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

public sealed class NotificationWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-notif-" + Guid.NewGuid().ToString("N"));
    private readonly NotificationWriter _writer;

    public NotificationWriterTests() => _writer = new NotificationWriter(_dir);

    private static AgentMessage Msg(long id, string title, string severity = "warning") =>
        new(id, title, "body " + id, severity, new DateTimeOffset(2026, 10, 10, (int)(id % 24), 0, 0, TimeSpan.Zero));

    private JsonElement Read() => JsonDocument.Parse(File.ReadAllText(_writer.FilePath)).RootElement;

    [Fact]
    public void Merge_writes_new_messages_in_the_widget_schema()
    {
        var added = _writer.Merge([Msg(1, "USB blocked"), Msg(2, "Heads up")]);

        Assert.Equal(2, added);
        var items = Read().GetProperty("Items");
        Assert.Equal(2, items.GetArrayLength());
        var first = items[0];
        Assert.False(string.IsNullOrEmpty(first.GetProperty("Id").GetString()));
        Assert.False(first.GetProperty("Read").GetBoolean());
        Assert.Contains("Title", first.ToString());
    }

    [Fact]
    public void Merge_is_idempotent_by_id()
    {
        _writer.Merge([Msg(1, "A")]);
        var addedAgain = _writer.Merge([Msg(1, "A"), Msg(2, "B")]);

        Assert.Equal(1, addedAgain); // only the new one
        Assert.Equal(2, Read().GetProperty("Items").GetArrayLength());
    }

    [Fact]
    public void Merge_preserves_a_read_flag_the_widget_set()
    {
        _writer.Merge([Msg(1, "A")]);
        // Simulate the widget marking it read.
        var text = File.ReadAllText(_writer.FilePath).Replace("\"Read\": false", "\"Read\": true");
        File.WriteAllText(_writer.FilePath, text);

        _writer.Merge([Msg(1, "A"), Msg(2, "B")]); // re-deliver 1, add 2

        var items = Read().GetProperty("Items").EnumerateArray().ToList();
        var one = items.First(i => i.GetProperty("Id").GetString() == "1");
        Assert.True(one.GetProperty("Read").GetBoolean()); // still read
    }

    [Fact]
    public void Empty_merge_writes_nothing()
    {
        Assert.Equal(0, _writer.Merge([]));
        Assert.False(File.Exists(_writer.FilePath));
    }

    [Fact]
    public void Member_reply_merges_as_read_and_carries_its_sender()
    {
        _writer.Merge([new AgentMessage(5, "", "my reply", "info", DateTimeOffset.UtcNow, "member")]);

        var item = Read().GetProperty("Items")[0];
        Assert.Equal("member", item.GetProperty("Sender").GetString());
        Assert.True(item.GetProperty("Read").GetBoolean()); // the member wrote it, so it's not "unread" to them
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
