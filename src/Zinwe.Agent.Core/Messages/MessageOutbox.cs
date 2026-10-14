using System.Text.Json;

namespace Zinwe.Agent.Core.Messages;

/// <summary>One pending member reply waiting to be posted. Id is a client guid (for removal after posting).</summary>
public sealed record OutboxReply(string Id, string Body, DateTimeOffset CreatedAtUtc);

/// <summary>
/// The member's outbound replies, written by the WIDGET and drained by the AGENT via a shared per-user file
/// (<c>%LOCALAPPDATA%\Zinwe\messages-outbox.json</c>) — the same cross-process pattern as the notifications file.
/// Both ends go through this type. Every operation is locked + best-effort (a read/write fault is swallowed).
/// </summary>
public sealed class MessageOutbox
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private const string FileName = "messages-outbox.json";

    private readonly IWidgetFolder _folder;
    private readonly Lock _lock = new();

    /// <summary>The current user's own outbox (the widget; tests). <paramref name="directory"/> defaults to %LOCALAPPDATA%\Zinwe.</summary>
    public MessageOutbox(string? directory = null) : this(new LocalWidgetFolder(directory))
    {
    }

    /// <summary>The outbox in whichever user's folder <paramref name="folder"/> resolves (the service: the signed-in member).</summary>
    public MessageOutbox(IWidgetFolder folder) => _folder = folder;

    /// <summary>The file path right now ("" when nobody is signed in).</summary>
    public string FilePath => _folder.Use(dir => Path.Combine(dir, FileName), "");

    private sealed class OutboxFile
    {
        public List<OutboxReply> Items { get; set; } = [];
    }

    /// <summary>Queue a reply to be posted by the agent. Returns the new item's id.</summary>
    public string Append(string body)
    {
        var reply = new OutboxReply(Guid.NewGuid().ToString("N"), body, DateTimeOffset.UtcNow);
        lock (_lock)
        {
            _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                file.Items.Add(reply);
                Save(path, file);
                return true;
            }, false);
        }
        return reply.Id;
    }

    /// <summary>Replies still waiting to be posted, oldest first.</summary>
    public IReadOnlyList<OutboxReply> Pending()
    {
        lock (_lock)
        {
            return _folder.Use<IReadOnlyList<OutboxReply>>(
                dir => [.. Load(Path.Combine(dir, FileName)).Items.OrderBy(i => i.CreatedAtUtc)], []);
        }
    }

    /// <summary>Remove the given ids after they've been posted.</summary>
    public void Remove(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return;
        }
        lock (_lock)
        {
            _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                if (file.Items.RemoveAll(i => set.Contains(i.Id)) > 0)
                {
                    Save(path, file);
                }
                return true;
            }, false);
        }
    }

    private static OutboxFile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<OutboxFile>(File.ReadAllText(path), Json);
                if (file is not null)
                {
                    file.Items.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Id));
                    return file;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // unreadable / mid-write: treat as empty
        }
        return new OutboxFile();
    }

    private static void Save(string path, OutboxFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best-effort
        }
    }
}
