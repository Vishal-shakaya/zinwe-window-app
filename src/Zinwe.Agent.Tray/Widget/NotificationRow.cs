using System.Windows;
using System.Windows.Media;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>View model for one message row in the widget list (data-bound from XAML).</summary>
public sealed class NotificationRow
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>Relative time only ("Just now", "5 min ago", …).</summary>
    public string When { get; init; } = "";
    /// <summary>Who sent it, prefixed to the time in the bubble ("You · Just now" / "Admin · 5 min ago").</summary>
    public string Stamp => $"{(IsMine ? "You" : "Admin")} · {When}";
    public Brush ToneBrush { get; init; } = Brushes.Gray;
    /// <summary>True for the employee's own reply (shown right-aligned); false for an admin message.</summary>
    public bool IsMine { get; init; }
    public Visibility BodyVisibility => string.IsNullOrWhiteSpace(Body) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TitleVisibility => string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible;
    public HorizontalAlignment RowAlignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    /// <summary>Bubble fill: a warm tint for the member's own replies, light grey for admin messages.</summary>
    public Brush BubbleBrush => IsMine ? MineFill : AdminFill;

    private static readonly Brush MineFill = Freeze(Color.FromRgb(0xFF, 0xF4, 0xEE));
    private static readonly Brush AdminFill = Freeze(Color.FromRgb(0xF4, 0xF4, 0xF5));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static NotificationRow From(NotificationItem item)
    {
        var mine = string.Equals(item.Sender, "member", StringComparison.OrdinalIgnoreCase);
        return new NotificationRow
        {
            Title = item.Title,
            Body = item.Body ?? "",
            When = Relative(item.CreatedAtUtc),
            ToneBrush = Tone(item),
            IsMine = mine,
        };
    }

    private static Brush Tone(NotificationItem item)
    {
        // Read messages fade to muted grey; unread show their severity colour.
        string key = item.Read
            ? "Zinwe.Muted"
            : item.Severity?.ToLowerInvariant() switch
            {
                "critical" => "Zinwe.Bad",
                "warning" => "Zinwe.Warn",
                _ => "Zinwe.Orange",
            };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private static string Relative(DateTimeOffset at)
    {
        var ago = DateTimeOffset.UtcNow - at.ToUniversalTime();
        if (ago < TimeSpan.Zero)
        {
            ago = TimeSpan.Zero;
        }
        return ago.TotalSeconds < 60 ? "Just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
            : at.ToLocalTime().ToString("d MMM, h:mm tt");
    }
}
