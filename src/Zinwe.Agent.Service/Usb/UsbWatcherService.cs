using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Usb;

/// <summary>Hosts the <see cref="IUsbDeviceWatcher"/> and feeds arrivals to <see cref="UsbProtection"/>.</summary>
internal sealed partial class UsbWatcherService(
    IUsbDeviceWatcher watcher,
    UsbProtection usb,
    ILogger<UsbWatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        watcher.DeviceArrived += OnArrived;
        try
        {
            watcher.Start();
            LogStarted();
        }
        catch (Exception e)
        {
            // Enforcement doesn't depend on the watcher; only the activity feed loses USB events.
            LogStartFailed(e);
            return;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            watcher.DeviceArrived -= OnArrived;
        }
    }

    private void OnArrived(object? sender, UsbDeviceArrival device)
    {
        try
        {
            usb.OnDeviceArrived(device);
        }
        catch (Exception e)
        {
            LogArrivalFailed(e);
        }
    }

    public override void Dispose()
    {
        watcher.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching for USB storage devices")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Error, Message = "USB watcher could not start; USB activity won't be reported")]
    private partial void LogStartFailed(Exception e);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling a USB arrival failed")]
    private partial void LogArrivalFailed(Exception e);
}
