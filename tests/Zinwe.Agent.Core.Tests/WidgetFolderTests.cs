using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// Everything the agent exchanges with the widget goes through <see cref="IWidgetFolder"/>. When nobody is signed
/// in (the installed service at the login screen), nothing may be written, read as real, or pulled from the backend.
/// </summary>
public sealed class WidgetFolderTests : IDisposable
{
    private readonly TempDir _dir = new();

    /// <summary>No one signed in to the PC.</summary>
    private sealed class NobodySignedIn : IWidgetFolder
    {
        public int Calls { get; private set; }

        public T Use<T>(Func<string, T> op, T whenNobody)
        {
            Calls++;
            return whenNobody;
        }
    }

    /// <summary>Records that every operation went through the seam (the service impersonates inside it).</summary>
    private sealed class CountingFolder(string dir) : IWidgetFolder
    {
        public int Calls { get; private set; }

        public T Use<T>(Func<string, T> op, T whenNobody)
        {
            Calls++;
            return op(dir);
        }
    }

    [Fact]
    public void With_nobody_signed_in_nothing_is_written_or_read()
    {
        var nobody = new NobodySignedIn();
        var writer = new NotificationWriter(nobody);
        var outbox = new MessageOutbox(nobody);

        Assert.False(writer.HasSignedInUser);
        Assert.Equal(0, writer.MergeChat([new AgentMessage(1, "Hi", null, "info", DateTimeOffset.UtcNow)]));
        Assert.Equal(0, writer.AddNotices([new AgentMessage(-1, "USB", null, "warning", DateTimeOffset.UtcNow)]));
        Assert.False(writer.AllReadLocally([1]));
        Assert.Empty(outbox.Pending());
        Assert.Equal(WorkStatus.NotStarted, new WorkStatusReader(nobody).Read(DateTimeOffset.Now));
        Assert.Equal("", writer.FilePath);
    }

    [Fact]
    public async Task With_nobody_signed_in_the_chat_is_not_pulled()
    {
        var backend = new FakeBackend(); // throws if called
        var nobody = new NobodySignedIn();
        var sync = new MessageSync(backend.Client(), new NotificationWriter(nobody), new MessageOutbox(nobody),
            NullLogger<MessageSync>.Instance);

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Empty(backend.Requests); // no GET: "Delivered" must mean it reached a signed-in member
    }

    [Fact]
    public async Task Every_file_operation_goes_through_the_folder_seam()
    {
        var folder = new CountingFolder(_dir.Path);
        var backend = new FakeBackend
        {
            Respond = r => r.Method == HttpMethod.Get
                ? FakeBackend.Json(HttpStatusCode.OK, "[]")
                : FakeBackend.Json(HttpStatusCode.Created, "{}"),
        };
        var outbox = new MessageOutbox(folder);
        outbox.Append("hello");
        var before = folder.Calls;

        await new MessageSync(backend.Client(), new NotificationWriter(folder), outbox, NullLogger<MessageSync>.Instance)
            .SyncAsync("tok", CancellationToken.None);

        Assert.True(folder.Calls > before);
        Assert.Empty(outbox.Pending()); // the reply was posted and removed - through the seam
        Assert.Contains(backend.Requests, r => r.Method == HttpMethod.Post && r.Body.Contains("hello"));
    }

    [Fact]
    public void The_local_folder_defaults_to_the_users_LocalAppData_Zinwe()
    {
        Assert.EndsWith(Path.Combine("AppData", "Local", "Zinwe"), new LocalWidgetFolder().Directory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_dir.Path, new LocalWidgetFolder(_dir.Path).Directory);
    }

    public void Dispose() => _dir.Dispose();
}
