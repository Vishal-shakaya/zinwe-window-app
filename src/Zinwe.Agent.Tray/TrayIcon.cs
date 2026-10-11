using System.Runtime.InteropServices;
using System.Windows.Threading;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Zinwe.Agent.Tray;

/// <summary>
/// Notification-area icon: polls the service for status, watches the local notifications store and raises
/// Windows toasts for new admin messages (even while the widget is hidden), and opens the widget on demand.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotificationPollInterval = TimeSpan.FromSeconds(10);

    private readonly Forms.NotifyIcon _icon = new();
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    private readonly NotificationStore _noteStore = new();
    private readonly NotificationWatcher _watcher;

    private Drawing.Icon? _glyph;
    private WidgetWindow? _window;
    private AgentState? _lastState;

    public TrayIcon()
    {
        _watcher = new NotificationWatcher(_noteStore, NotificationPollInterval);
    }

    public void Start()
    {
        _glyph = CreateGlyph();
        _icon.Icon = _glyph;
        _icon.Text = "Zinwe";
        _icon.DoubleClick += (_, _) => ShowStatus();
        _icon.BalloonTipClicked += (_, _) => ShowStatus();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Zinwe", null, (_, _) => ShowStatus());
#if DEBUG
        menu.Items.Add("Send test message (debug)", null, (_, _) => SeedTestMessage());
        menu.Items.Add(new Forms.ToolStripSeparator());
        // Employees can't quit the tray in release builds (closing it would not stop protection anyway).
        menu.Items.Add("Exit (debug only)", null, (_, _) => Shutdown());
#endif
        _icon.ContextMenuStrip = menu;
        _icon.Visible = true;

        // Toast for any message that arrives, regardless of whether the widget is open.
        _watcher.NewItems += OnNewNotifications;
        _watcher.Start();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnNewNotifications(IReadOnlyList<NotificationItem> items)
    {
        // One toast per arriving message (newest first); keep it short.
        foreach (var item in items)
        {
            var tone = item.Severity?.ToLowerInvariant() switch
            {
                "critical" => Forms.ToolTipIcon.Error,
                "warning" => Forms.ToolTipIcon.Warning,
                _ => Forms.ToolTipIcon.Info,
            };
            _icon.ShowBalloonTip(10_000, "Zinwe: " + item.Title, item.Body ?? "", tone);
        }
    }

    private async Task RefreshAsync()
    {
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(3));
        var (headline, detail, _) = StatusText.Describe(status);

        // NotifyIcon.Text is capped at 127 chars.
        var tip = $"Zinwe — {headline}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        // Only interrupt the user for a change that needs action: removal, or a forced update.
        var needsAction = status?.State is AgentState.Rejected or AgentState.UpdateRequired;
        if (needsAction && _lastState is not null && _lastState != status!.State)
        {
            _icon.ShowBalloonTip(10_000, "Zinwe: " + headline, detail, Forms.ToolTipIcon.Warning);
        }
        _lastState = status?.State;
    }

    public void ShowStatus()
    {
        if (_window is null)
        {
            _window = new WidgetWindow(_noteStore, _watcher);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        else if (!_window.IsVisible)
        {
            _window.Show();
        }
        if (_window.WindowState == System.Windows.WindowState.Minimized)
        {
            _window.WindowState = System.Windows.WindowState.Normal;
        }
        _window.Activate();
    }

#if DEBUG
    private void SeedTestMessage()
    {
        _noteStore.DebugAppend(new NotificationItem(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            "warning",
            "Test alert from admin",
            "This is a locally-seeded test message. The service writes real ones here in a later milestone."));
        _watcher.Poll();
    }

    private void Shutdown()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
        }
        System.Windows.Application.Current.Shutdown();
    }
#endif

    /// <summary>
    /// The notification-area icon: the real Zinwe mark (Assets/zinwe-mark.png), scaled to 32px with its white
    /// background keyed to transparent. Falls back to a drawn orange "Z" if the asset can't be loaded.
    /// </summary>
    private static Drawing.Icon CreateGlyph()
    {
        const int size = 32;
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            if (!TryDrawMark(g, size))
            {
                DrawFallbackZ(g, size);
            }
        }
        // Key the mark's white background to transparent so the tray shows just the orange Z.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.A > 0 && p is { R: >= 235, G: >= 235, B: >= 235 })
                {
                    bitmap.SetPixel(x, y, Drawing.Color.Transparent);
                }
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); // Clone owns its handle
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static bool TryDrawMark(Drawing.Graphics g, int size)
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/zinwe-mark.png", UriKind.Absolute));
            if (info is null)
            {
                return false;
            }
            using var stream = info.Stream;
            using var mark = new Drawing.Bitmap(stream);
            g.DrawImage(mark, 0, 0, size, size);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DrawFallbackZ(Drawing.Graphics g, int size)
    {
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFF, 0x5C, 0x1A));
        using var font = new Drawing.Font("Segoe UI", 22, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        g.DrawString("Z", font, orange, new Drawing.RectangleF(0, 0, size, size), format);
    }

    public void Dispose()
    {
        _timer.Stop();
        _watcher.NewItems -= OnNewNotifications;
        _watcher.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _glyph?.Dispose();
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
