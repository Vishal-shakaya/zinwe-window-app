using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Tray;

/// <summary>Status card. Refreshes every few seconds while open; does nothing while closed.</summary>
public partial class StatusWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public StatusWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(3));
        var (headline, detail, tone) = StatusText.Describe(status);

        Headline.Text = headline;
        Detail.Text = detail;
        StatusDot.Fill = (Brush)FindResource(tone switch
        {
            StatusTone.Ok => "Zinwe.Ok",
            StatusTone.Warn => "Zinwe.Warn",
            _ => "Zinwe.Bad",
        });
        Hostname.Text = status?.Hostname ?? Environment.MachineName;
        LastSeen.Text = StatusText.LastSeen(status?.LastHeartbeatAt);
        UsbRule.Text = StatusText.UsbRule(status?.UsbMode);
        Version.Text = status is null ? AgentInfo.Version : $"{status.AgentVersion}";
    }
}
