using System.Windows;

namespace Zinwe.Agent.Tray;

/// <summary>
/// Starts once per signed-in user (HKLM\...\Run, written by the MSI). No main window:
/// lives in the notification area and opens <see cref="StatusWindow"/> on demand.
/// </summary>
public partial class App : Application
{
    // Per-session ("Local\") so every signed-in user on a shared PC gets their own tray.
    private const string SingleInstanceMutexName = @"Local\Zinwe.Agent.Tray";

    private Mutex? _singleInstance;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _tray = new TrayIcon();
        _tray.Start();

        // `Zinwe.Agent.Tray.exe --show` opens the status card immediately (Start-menu shortcut, support calls).
        if (e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase))
        {
            _tray.ShowStatus();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_singleInstance is not null)
        {
            if (_tray is not null)
            {
                _singleInstance.ReleaseMutex();
            }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
