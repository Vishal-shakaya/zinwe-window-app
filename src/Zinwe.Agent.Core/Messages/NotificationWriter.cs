using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Messages;

// Mirrors the widget's notification file schema (Zinwe.Agent.Tray.Widget.NotificationStore). The service
// can't reference the tray project, so the shape is duplicated here. Keys are PascalCase; the widget reads
// them case-insensitively. DEV NOTE: the widget file lives in the per-user %LOCALAPPDATA%\Zinwe; under the
// console (dev) the agent is that same user, so this writes the right place. A production service running as
// LocalSystem would need to resolve each signed-in user's LocalAppData (tracked as a later milestone).
internal sealed record WidgetNotification(
    string Id, DateTimeOffset CreatedAtUtc, string Severity, string Title, string? Body, bool Read, string Sender = "owner");

internal sealed class WidgetNotificationFile
{
    public List<WidgetNotification> Items { get; set; } = [];
}

/// <summary>
/// Merges owner→member messages into the widget's notifications.json. New messages are appended with
/// Read=false; messages already present keep their (widget-set) Read flag, so delivery never un-reads them.
/// </summary>
public sealed class NotificationWriter
{
    private const int MaxItems = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <summary><paramref name="directory"/> defaults to %LOCALAPPDATA%\Zinwe (the widget's own folder).</summary>
    public NotificationWriter(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zinwe");
        _path = Path.Combine(dir, "notifications.json");
    }

    public string FilePath => _path;

    /// <summary>Adds any messages not already in the file. Returns how many were newly added.</summary>
    public int Merge(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count == 0)
        {
            return 0;
        }
        var file = Load();
        var seen = new HashSet<string>(file.Items.Select(i => i.Id), StringComparer.Ordinal);

        var added = 0;
        foreach (var m in messages)
        {
            var id = m.Id.ToString(CultureInfo.InvariantCulture);
            if (seen.Add(id))
            {
                // The member's own replies arrive "already read" (they wrote them); admin lines start unread.
                var isMember = string.Equals(m.Sender, "member", StringComparison.OrdinalIgnoreCase);
                file.Items.Add(new WidgetNotification(id, m.CreatedAt, m.Severity, m.Title, m.Body, Read: isMember, Sender: m.Sender));
                added++;
            }
        }
        if (added > 0)
        {
            // Keep the newest MaxItems so the file can't grow without bound.
            file.Items = [.. file.Items.OrderByDescending(i => i.CreatedAtUtc).Take(MaxItems)];
            Save(file);
        }
        return added;
    }

    private WidgetNotificationFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<WidgetNotificationFile>(File.ReadAllText(_path), Json) ?? new WidgetNotificationFile();
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable / mid-write: treat as empty; the next sync re-adds.
        }
        return new WidgetNotificationFile();
    }

    private void Save(WidgetNotificationFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
