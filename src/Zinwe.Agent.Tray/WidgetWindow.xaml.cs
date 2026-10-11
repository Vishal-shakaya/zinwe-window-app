using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>
/// The employee's main interface after setup: protection status, work timing (Start/Break/End),
/// admin messages, and the consent card. Minimizing hides it to the background; the agent keeps running.
/// Replaces the old status-only card.
/// </summary>
public partial class WidgetWindow : Window
{
    private readonly WorkTimerStore _timerStore = new();
    private readonly NotificationStore _noteStore;
    private readonly NotificationWatcher _watcher;

    private WorkTimerState _timer;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private int _secondsSinceSave;

    // Forced upgrade (M12): when the backend requires a newer agent, work is paused and the
    // employee is prompted to install the update (from this link) until a newer agent reports in.
    private bool _updateRequired;
    private string? _updateDownloadUrl;

    /// <summary>Set by the tray on real shutdown so Closing stops hiding and actually closes.</summary>
    public bool AllowClose { get; set; }

    public WidgetWindow(NotificationStore noteStore, NotificationWatcher watcher)
    {
        _noteStore = noteStore;
        _watcher = watcher;
        InitializeComponent();

        _timer = _timerStore.Load(DateTimeOffset.Now);

        _tick.Tick += (_, _) => OnTick();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();

        _watcher.Refreshed += OnNotificationsRefreshed;

        Loaded += async (_, _) =>
        {
            RenderTimer();
            RenderNotifications(_watcher.Poll());
            await RefreshStatusAsync();
            _tick.Start();
            _statusTimer.Start();
        };

        Closing += OnClosing;
    }

    // ---- Work timing -------------------------------------------------------

    private void OnStart(object sender, RoutedEventArgs e) => Transition(t => t.Start(DateTimeOffset.Now), "start");

    private void OnBreak(object sender, RoutedEventArgs e)
    {
        // Label is "Break" while working and "Resume" while on break.
        if (_timer.State == WorkState.OnBreak)
        {
            Transition(t => t.ToggleBreak(DateTimeOffset.Now), "resume");
            return;
        }
        // Going on a break: ask why first (one tap). The break starts when a reason is picked.
        BreakReasonPanel.Visibility = BreakReasonPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBreakReason(object sender, RoutedEventArgs e)
    {
        var reason = (sender as FrameworkElement)?.Tag as string;
        BreakReasonPanel.Visibility = Visibility.Collapsed;
        Transition(t => t.ToggleBreak(DateTimeOffset.Now, reason), "break");
    }

    private void OnBreakCancel(object sender, RoutedEventArgs e) => BreakReasonPanel.Visibility = Visibility.Collapsed;

    private void OnEnd(object sender, RoutedEventArgs e) => Transition(t => t.End(DateTimeOffset.Now), "end");

    private void Transition(Func<WorkTimerState, bool> action, string logAction)
    {
        var now = DateTimeOffset.Now;
        _timer.RollOverIfNewDay(now);
        if (!action(_timer))
        {
            return;
        }
        _timerStore.Save(_timer);
        _timerStore.AppendLog(logAction, _timer, now);
        RenderTimer();
    }

    private void OnTick()
    {
        var now = DateTimeOffset.Now;
        if (_timer.RollOverIfNewDay(now))
        {
            _timerStore.Save(_timer);
        }
        RenderTimer();

        // Checkpoint periodically so a crash loses at most ~30s of the running segment.
        if (++_secondsSinceSave >= 30)
        {
            _secondsSinceSave = 0;
            _timerStore.Save(_timer);
        }
    }

    private void RenderTimer()
    {
        var now = DateTimeOffset.Now;
        WorkedTime.Text = Format(_timer.LiveWorked(now));

        var brk = _timer.LiveBreak(now);
        BreakTime.Text = _timer.State == WorkState.OnBreak && !string.IsNullOrEmpty(_timer.BreakReason)
            ? $"On break ({_timer.BreakReason}) {Format(brk)}"
            : $"On break {Format(brk)}";
        BreakTime.Visibility = brk > TimeSpan.Zero || _timer.State == WorkState.OnBreak
            ? Visibility.Visible : Visibility.Collapsed;

        TimerStateText.Text = _timer.State switch
        {
            WorkState.Working => "Working",
            WorkState.OnBreak => "On break",
            WorkState.Ended => "Ended for today",
            _ => "Not started",
        };

        // Work is paused while an update is required: the controls stay disabled until the agent is updated.
        StartButton.IsEnabled = _timer.CanStart && !_updateRequired;
        BreakButton.IsEnabled = _timer.CanToggleBreak && !_updateRequired;
        EndButton.IsEnabled = _timer.CanEnd && !_updateRequired;
        BreakButton.Content = _timer.State == WorkState.OnBreak ? "Resume" : "Break";
        if (!_timer.CanToggleBreak || _updateRequired)
        {
            BreakReasonPanel.Visibility = Visibility.Collapsed;
        }
        StartButton.Content = _timer.State == WorkState.Ended ? "Start again" : "Start";
    }

    private static string Format(TimeSpan t) =>
        $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    // ---- Notifications -----------------------------------------------------

    private void OnNotificationsRefreshed(NotificationFile file) => RenderNotifications(file);

    private void RenderNotifications(NotificationFile file)
    {
        var rows = file.Items.Select(NotificationRow.From).ToList();
        MessageList.ItemsSource = rows;
        NoMessages.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var unread = file.Items.Count(i => !i.Read);
        UnreadCount.Text = unread.ToString();
        UnreadBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMarkRead(object sender, RoutedEventArgs e) => RenderNotifications(_noteStore.MarkAllRead());

    private void OnClear(object sender, RoutedEventArgs e) => RenderNotifications(_noteStore.Clear());

    // ---- Reply (member → admin) --------------------------------------------
    // The reply is queued to the shared outbox; the agent posts it on its next check-in and the admin sees it
    // on the dashboard. The member's own line comes back into this list on the following sync.
    private readonly MessageOutbox _outbox = new();

    private void OnReplyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendReply();
        }
    }

    private void OnReply(object sender, RoutedEventArgs e) => SendReply();

    private void SendReply()
    {
        var text = ReplyInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        try
        {
            _outbox.Append(text);
            ReplyInput.Clear();
            ReplyNote.Text = "Reply sent — it appears in the conversation shortly.";
        }
        catch (Exception)
        {
            ReplyNote.Text = "Couldn't send right now. Please try again.";
        }
        ReplyNote.Visibility = Visibility.Visible;
    }

    // ---- Protection status (same source as the tray tooltip) ---------------

    private async Task RefreshStatusAsync()
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
        UsbRule.Text = StatusText.UsbRule(status?.UsbMode);
        Version.Text = status is null ? AgentInfo.Version : status.AgentVersion;

        RenderConnectState(status);
    }

    /// <summary>Show the Connect form until the PC is enrolled; show the normal widget once it is.</summary>
    private void RenderConnectState(AgentStatus? status)
    {
        var state = status?.State;
        // An enrolled PC (connected, offline, or update-required) shows the normal widget body.
        var enrolled = state is AgentState.Connected or AgentState.Offline or AgentState.UpdateRequired;
        var needsConnect = state is AgentState.NotEnrolled or AgentState.Rejected or AgentState.Enrolling;

        ConnectedContent.Visibility = enrolled ? Visibility.Visible : Visibility.Collapsed;
        ConnectPanel.Visibility = needsConnect ? Visibility.Visible : Visibility.Collapsed;

        _updateRequired = state == AgentState.UpdateRequired;
        _updateDownloadUrl = status?.DownloadUrl;
        UpdatePanel.Visibility = _updateRequired ? Visibility.Visible : Visibility.Collapsed;
        // Re-gate the work buttons now that the pause flag may have changed.
        if (enrolled)
        {
            RenderTimer();
        }

        switch (state)
        {
            case AgentState.Enrolling:
                ConnectButton.IsEnabled = false;
                ShowConnectMessage("Connecting…", error: false);
                break;
            case AgentState.Rejected:
                ConnectButton.IsEnabled = true;
                ShowConnectMessage(status?.Message ?? "That didn't work — check the ID and try again.", error: true);
                break;
        }
    }

    /// <summary>Open the update download in the default browser; the employee installs it manually.</summary>
    private void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_updateDownloadUrl) ? "https://zinwe.io/download" : _updateDownloadUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser / blocked: leave the link visible in the panel text; nothing else to do.
        }
    }

    // ---- Connect this PC ---------------------------------------------------

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var email = EmailInput.Text.Trim();
        var memberId = MemberIdInput.Text.Trim();
        if (email.Length == 0 || memberId.Length == 0)
        {
            ShowConnectMessage("Enter both your work email and your Member ID.", error: true);
            return;
        }

        ConnectButton.IsEnabled = false;
        ShowConnectMessage("Connecting…", error: false);

        var ack = await AgentPipeClient.SubmitEnrollmentAsync(email, memberId, TimeSpan.FromSeconds(5));
        if (ack is null)
        {
            ShowConnectMessage("Zinwe isn't running yet. Please try again in a moment.", error: true);
            ConnectButton.IsEnabled = true;
            return;
        }
        if (!ack.Accepted)
        {
            ShowConnectMessage(ack.Message, error: true);
            ConnectButton.IsEnabled = true;
            return;
        }
        // Accepted: the service is enrolling. The status poll flips us to Connected (or back to the form on a bad id).
        await RefreshStatusAsync();
    }

    private void ShowConnectMessage(string message, bool error)
    {
        ConnectMessage.Text = message;
        ConnectMessage.Foreground = (Brush)FindResource(error ? "Zinwe.Bad" : "Zinwe.Muted");
        ConnectMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Window lifetime ---------------------------------------------------

    private void OnMinimize(object sender, RoutedEventArgs e) => HideToBackground();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            _tick.Stop();
            _statusTimer.Stop();
            _watcher.Refreshed -= OnNotificationsRefreshed;
            _timerStore.Save(_timer);
            return;
        }
        // Employees can't quit the widget; the X just tucks it away. Protection keeps running.
        e.Cancel = true;
        HideToBackground();
    }

    private void HideToBackground()
    {
        _timerStore.Save(_timer);
        Hide();
    }
}
