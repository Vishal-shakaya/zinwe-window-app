using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>One admin -> employee message shown in the widget.</summary>
/// <param name="Id">Stable unique id (the service should generate a UUID).</param>
/// <param name="CreatedAtUtc">When the message was raised.</param>
/// <param name="Severity">info | warning | critical (drives the dot colour; free-form tolerated).</param>
/// <param name="Title">Short headline.</param>
/// <param name="Body">Optional longer text.</param>
/// <param name="Read">Whether the employee has seen it (the widget sets this locally).</param>
public sealed record NotificationItem(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Severity,
    string Title,
    string? Body = null,
    bool Read = false,
    // "owner" = an admin message/alert; "member" = the employee's own reply (shown as outgoing).
    string Sender = "owner");

/// <summary>Root object persisted to <see cref="WidgetPaths.Notifications"/>.</summary>
public sealed record NotificationFile(List<NotificationItem> Items)
{
    public NotificationFile() : this([]) { }
}

/// <summary>
/// Reads and writes the per-user notifications JSON file.
///
/// SEAM (documented contract for the LATER milestone): the service -- when it receives an
/// admin-sent alert -- APPENDS a <see cref="NotificationItem"/> to the "items" array of the signed-in
/// user's <c>%LOCALAPPDATA%\Zinwe\notifications.json</c>. The service must:
///   - generate a fresh unique <c>id</c> per message (the widget de-dupes "new" by id),
///   - write atomically (temp file + move) to avoid a half-written read,
///   - leave <c>read=false</c>; the widget flips it to true once shown.
/// The widget owns marking items read and never deletes the service's entries except on the
/// employee's explicit "Clear" action. No pipe/PipeProtocol change is made for the base app.
/// </summary>
public sealed class NotificationStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public NotificationFile Load()
    {
        try
        {
            if (File.Exists(WidgetPaths.Notifications))
            {
                var text = File.ReadAllText(WidgetPaths.Notifications);
                var file = JsonSerializer.Deserialize<NotificationFile>(text, Json);
                if (file is not null)
                {
                    file.Items.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Id));
                    // Newest first for display.
                    file.Items.Sort((a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
                    return file;
                }
            }
        }
        catch
        {
            // Unreadable or mid-write: treat as empty; the next poll will pick it up.
        }
        return new NotificationFile();
    }

    public void Save(NotificationFile file)
    {
        try
        {
            WidgetPaths.EnsureDirectory();
            var tmp = WidgetPaths.Notifications + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, WidgetPaths.Notifications, overwrite: true);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Marks every item read and persists. Returns the updated file.</summary>
    public NotificationFile MarkAllRead()
    {
        var file = Load();
        for (var i = 0; i < file.Items.Count; i++)
        {
            if (!file.Items[i].Read)
            {
                file.Items[i] = file.Items[i] with { Read = true };
            }
        }
        Save(file);
        return file;
    }

    /// <summary>Removes all items (employee's explicit "Clear").</summary>
    public NotificationFile Clear()
    {
        var file = new NotificationFile();
        Save(file);
        return file;
    }

#if DEBUG
    /// <summary>DEBUG-only self-seed so the widget can be exercised before the service writes real alerts.</summary>
    public void DebugAppend(NotificationItem item)
    {
        var file = Load();
        file.Items.Add(item);
        Save(file);
    }
#endif
}
