using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

/// <summary>The chat sync: replies out, conversation in, and "read" only once the member opened it.</summary>
public sealed class MessageSyncTests : IDisposable
{
    private const string MessagesPath = "/api/devices/messages/";
    private const string ReadPath = "/api/devices/messages/read/";

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private readonly NotificationWriter _writer;
    private readonly MessageOutbox _outbox;
    private readonly MessageSync _sync;

    private string _conversation = "[]";

    public MessageSyncTests()
    {
        _writer = new NotificationWriter(_dir.Path);
        _outbox = new MessageOutbox(_dir.Path);
        _sync = new MessageSync(_backend.Client(), _writer, _outbox, NullLogger<MessageSync>.Instance);
        _backend.Respond = r => (r.Method.Method, r.Path) switch
        {
            ("GET", MessagesPath) => FakeBackend.Json(HttpStatusCode.OK, _conversation),
            ("POST", MessagesPath) => FakeBackend.Json(HttpStatusCode.Created, "{}"),
            ("POST", ReadPath) => FakeBackend.Json(HttpStatusCode.OK, "{\"updated\": 1}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static string Line(long id, string sender, bool readByMember) =>
        $$"""{"id": {{id}}, "sender": "{{sender}}", "title": "Hi", "body": "Hello there", "severity": "info", "created_at": "2026-10-14T10:00:00Z", "read_by_owner": true, "read_by_member": {{(readByMember ? "true" : "false")}}}""";

    private int Calls(string method, string path) => _backend.Requests.Count(r => r.Method.Method == method && r.Path == path);

    [Fact]
    public async Task Delivering_a_message_does_not_mark_it_read_on_the_server()
    {
        _conversation = $"[{Line(7, "owner", readByMember: false)}]";

        await _sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(1, Calls("GET", MessagesPath));
        Assert.Equal(0, Calls("POST", ReadPath)); // reached the PC, but the member hasn't opened it
        Assert.False(_writer.AllReadLocally([7]));
    }

    [Fact]
    public async Task Once_the_member_opened_the_chat_the_server_is_told_once()
    {
        _conversation = $"[{Line(7, "owner", readByMember: false)}]";
        await _sync.SyncAsync("tok", CancellationToken.None);

        // The widget's chat marks it read.
        File.WriteAllText(_writer.FilePath, File.ReadAllText(_writer.FilePath).Replace("\"Read\": false", "\"Read\": true"));
        await _sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(1, Calls("POST", ReadPath));

        // The server now has it read: later rounds don't post again.
        _conversation = $"[{Line(7, "owner", readByMember: true)}]";
        await _sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(1, Calls("POST", ReadPath));
    }

    [Fact]
    public async Task Pending_replies_are_posted_before_the_pull_and_cleared()
    {
        _outbox.Append("On my way");
        Assert.True(_sync.HasPendingReplies);

        await _sync.SyncAsync("tok", CancellationToken.None);

        var post = Assert.Single(_backend.Requests, r => r.Method == HttpMethod.Post && r.Path == MessagesPath);
        Assert.Contains("On my way", post.Body);
        Assert.False(_sync.HasPendingReplies);
        Assert.True(_backend.Requests.IndexOf(post) < _backend.Requests.FindIndex(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task The_members_own_lines_never_trigger_a_read_post()
    {
        _conversation = $"[{Line(9, "member", readByMember: false)}]";

        await _sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(0, Calls("POST", ReadPath));
    }

    public void Dispose() => _dir.Dispose();
}
