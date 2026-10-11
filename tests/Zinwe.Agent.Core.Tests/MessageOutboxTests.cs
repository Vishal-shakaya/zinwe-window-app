using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

public sealed class MessageOutboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-outbox-" + Guid.NewGuid().ToString("N"));
    private readonly MessageOutbox _outbox;

    public MessageOutboxTests() => _outbox = new MessageOutbox(_dir);

    [Fact]
    public void Append_then_pending_returns_the_reply()
    {
        var id = _outbox.Append("Why is USB blocked?");

        var pending = _outbox.Pending();
        Assert.Single(pending);
        Assert.Equal(id, pending[0].Id);
        Assert.Equal("Why is USB blocked?", pending[0].Body);
    }

    [Fact]
    public void Remove_drops_only_the_posted_replies()
    {
        var first = _outbox.Append("one");
        _outbox.Append("two");

        _outbox.Remove([first]);

        var pending = _outbox.Pending();
        Assert.Single(pending);
        Assert.Equal("two", pending[0].Body);
    }

    [Fact]
    public void Pending_is_oldest_first()
    {
        _outbox.Append("first");
        _outbox.Append("second");

        Assert.Equal(["first", "second"], _outbox.Pending().Select(r => r.Body).ToArray());
    }

    [Fact]
    public void Survives_a_fresh_instance_on_the_same_file()
    {
        _outbox.Append("persisted");
        var reopened = new MessageOutbox(_dir);
        Assert.Single(reopened.Pending());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
