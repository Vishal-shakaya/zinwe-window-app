using System.Runtime.InteropServices;
using System.Windows.Threading;
using Zinwe.Agent.Core.Ipc;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Zinwe.Agent.Tray;

/// <summary>Notification-area icon: polls the service, keeps the tooltip current, opens the status window.</summary>
internal sealed partial class TrayIcon : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly Forms.NotifyIcon _icon = new();
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private Drawing.Icon? _glyph;
    private StatusWindow? _window;
    private AgentState? _lastState;

    public void Start()
    {
        _glyph = CreateGlyph();
        _icon.Icon = _glyph;
        _icon.Text = "Zinwe";
        _icon.DoubleClick += (_, _) => ShowStatus();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Zinwe status", null, (_, _) => ShowStatus());
#if DEBUG
        // Employees can't quit the tray in release builds (closing it would not stop protection anyway).
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit (debug only)", null, (_, _) => System.Windows.Application.Current.Shutdown());
#endif
        _icon.ContextMenuStrip = menu;
        _icon.Visible = true;

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(3));
        var (headline, detail, _) = StatusText.Describe(status);

        // NotifyIcon.Text is capped at 127 chars.
        var tip = $"Zinwe — {headline}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        // Only interrupt the user for a change that needs action.
        if (status?.State == AgentState.Rejected && _lastState is not null && _lastState != AgentState.Rejected)
        {
            _icon.ShowBalloonTip(10_000, "Zinwe: " + headline, detail, Forms.ToolTipIcon.Warning);
        }
        _lastState = status?.State;
    }

    public void ShowStatus()
    {
        if (_window is null)
        {
            _window = new StatusWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        _window.Activate();
    }

    /// <summary>Orange rounded square with a white "Z", drawn at runtime so the repo carries no binary icon yet.</summary>
    private static Drawing.Icon CreateGlyph()
    {
        const int size = 32;
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            const int r = 8;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(size - r - 1, 0, r, r, 270, 90);
            path.AddArc(size - r - 1, size - r - 1, r, r, 0, 90);
            path.AddArc(0, size - r - 1, r, r, 90, 90);
            path.CloseFigure();
            using var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFF, 0x5C, 0x1A));
            g.FillPath(orange, path);
            using var font = new Drawing.Font("Segoe UI", 17, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString("Z", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 1, size, size), format);
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

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _glyph?.Dispose();
        _window?.Close();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
