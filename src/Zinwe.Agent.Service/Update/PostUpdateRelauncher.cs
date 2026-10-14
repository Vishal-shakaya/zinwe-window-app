using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Service.Session;

namespace Zinwe.Agent.Service.Update;

/// <summary>
/// After a one-click update, the freshly installed service reopens the tray for the signed-in member: the
/// installer ran as SYSTEM, and the tray (closed for the install) would otherwise only return at the next
/// sign-in. Triggered by the marker <see cref="AgentUpdater"/> leaves in the data folder; also runs when the MSI
/// rolled back and restarted the old service, so the member is never left without their widget.
/// </summary>
internal sealed partial class PostUpdateRelauncher(IOptions<AgentOptions> options, ILogger<PostUpdateRelauncher> logger)
    : BackgroundService
{
    private const string MarkerFile = "update-pending.txt";

    public static void MarkPending(string dataDirectory, string version)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, MarkerFile), version);
    }

    public static void ClearPending(string dataDirectory)
    {
        try
        {
            File.Delete(Path.Combine(dataDirectory, MarkerFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // harmless: the tray is just reopened once more on the next start
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dataDirectory = Environment.ExpandEnvironmentVariables(options.Value.DataDirectory);
        var marker = Path.Combine(dataDirectory, MarkerFile);
        if (!File.Exists(marker))
        {
            return;
        }
        var target = (await File.ReadAllTextAsync(marker, stoppingToken)).Trim();
        ClearPending(dataDirectory);
        LogAfterUpdate(target, AgentInfo.Version);
        // A moment for the member's session to settle after the installer.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        TrayLauncher.TryLaunch(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started after an update to {Target}; now running {Version}. Reopening the tray.")]
    private partial void LogAfterUpdate(string target, string version);
}

/// <summary>Starts the installed tray on the signed-in member's desktop (no-op in dev / with nobody signed in).</summary>
internal static partial class TrayLauncher
{
    public static bool TryLaunch(ILogger logger)
    {
        if (!Core.Usb.UsbEnforcerSelector.IsRunningAsSystem())
        {
            return false; // dev console agent: the start script runs the widget
        }
        // Installed layout: Program Files\Zinwe Agent\{service,tray}\
        var tray = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tray", "Zinwe.Agent.Tray.exe"));
        if (!File.Exists(tray))
        {
            return false;
        }
        using var token = SignedInUser.OpenToken();
        if (token is null)
        {
            return false; // nobody signed in: the HKLM Run key starts it at the next sign-in
        }
        var ok = SignedInUser.Launch(token, tray);
        LogLaunch(logger, tray, ok);
        return ok;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reopening the tray {Path}: {Ok}")]
    private static partial void LogLaunch(ILogger logger, string path, bool ok);
}
