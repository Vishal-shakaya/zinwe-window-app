using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

public sealed class NotificationRowTests
{
    private static NotificationItem Item(DateTimeOffset at, string? body = null) =>
        new("id-1", at, "info", "A message", body);

    [Fact]
    public void Relative_time_buckets_read_naturally()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Just now", NotificationRow.From(Item(now.AddSeconds(-5))).When);
        Assert.Equal("5 min ago", NotificationRow.From(Item(now.AddMinutes(-5))).When);
        Assert.Equal("3 h ago", NotificationRow.From(Item(now.AddHours(-3))).When);
        // Older than a day falls back to an absolute date, not "27 h ago".
        Assert.DoesNotContain("ago", NotificationRow.From(Item(now.AddDays(-2))).When);
    }

    [Fact]
    public void A_future_timestamp_clamps_to_just_now()
    {
        Assert.Equal("Just now", NotificationRow.From(Item(DateTimeOffset.UtcNow.AddMinutes(5))).When);
    }

    [Fact]
    public void Empty_body_is_collapsed_in_the_row()
    {
        Assert.Equal(System.Windows.Visibility.Collapsed, NotificationRow.From(Item(DateTimeOffset.UtcNow)).BodyVisibility);
        Assert.Equal(System.Windows.Visibility.Visible, NotificationRow.From(Item(DateTimeOffset.UtcNow, "details")).BodyVisibility);
    }
}
