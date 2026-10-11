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

    private readonly string _path;
    private readonly Lock _lock = new();

    public MessageOutbox(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zinwe");
        _path = Path.Combine(dir, "messages-outbox.json");
    }

    public string FilePath => _path;

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
            var file = Load();
            file.Items.Add(reply);
            Save(file);
        }
        return reply.Id;
    }

    /// <summary>Replies still waiting to be posted, oldest first.</summary>
    public IReadOnlyList<OutboxReply> Pending()
    {
        lock (_lock)
        {
            return [.. Load().Items.OrderBy(i => i.CreatedAtUtc)];
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
            var file = Load();
            var before = file.Items.Count;
            file.Items.RemoveAll(i => set.Contains(i.Id));
            if (file.Items.Count != before)
            {
                Save(file);
            }
        }
    }

    private OutboxFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var file = JsonSerializer.Deserialize<OutboxFile>(File.ReadAllText(_path), Json);
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

    private void Save(OutboxFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best-effort
        }
    }
}
