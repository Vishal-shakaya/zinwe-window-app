using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>What was in the foreground at one sample instant.</summary>
/// <param name="ProcessName">The owning process, e.g. "chrome", "msedge", "winword" (no ".exe").</param>
/// <param name="WindowTitle">The window title, only when IncludeDetails is on; else null (consent-first).</param>
public sealed record ForegroundSample(string ProcessName, string? WindowTitle = null);

/// <summary>Reports what is in the foreground now. The service uses Win32; tests use a fake.</summary>
public interface IForegroundWindowSource
{
    /// <summary>The current foreground app, or null when it can't be resolved (lock screen, no window, denied).</summary>
    ForegroundSample? Sample();
}

/// <summary>
/// Reads the foreground window via Win32 (read-only detection, safe to run live) and maps it to a process name.
/// Resolving a tab's DOMAIN this way is not possible cheaply/reliably (that needs UI Automation per-browser or a
/// browser extension), so this reports the APP only; domain usage is intentionally left to a future milestone.
/// </summary>
public sealed class Win32ForegroundWindowSource(WebFeatureOptions options) : IForegroundWindowSource
{
    public ForegroundSample? Sample()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return null;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new ForegroundSample(name, options.IncludeDetails ? ReadTitle(hwnd) : null);
    }

    private static string? ReadTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }
        var buffer = new StringBuilder(length + 1);
        var copied = GetWindowText(hwnd, buffer, buffer.Capacity);
        return copied <= 0 ? null : buffer.ToString();
    }

    // Classic DllImport (not LibraryImport) so no unsafe/csproj change is needed in the shared Core project.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
#pragma warning restore SYSLIB1054
}

/// <summary>
/// Accumulates foreground time into <see cref="MetricsStore"/> under <see cref="WebMetrics.ForegroundApp"/>,
/// keyed by process name. Never throws: a sampling or storage failure is logged and skipped.
/// </summary>
public sealed class ForegroundUsageSampler(
    IForegroundWindowSource source,
    MetricsStore metrics,
    ILogger<ForegroundUsageSampler> logger)
{
    /// <summary>
    /// Takes one sample and credits <paramref name="seconds"/> to the current foreground app. Returns the sample
    /// (so a caller can observe it) or null when nothing was recorded.
    /// </summary>
    public ForegroundSample? Collect(long seconds, DateTimeOffset? at = null)
    {
        if (seconds <= 0)
        {
            return null;
        }
        try
        {
            var sample = source.Sample();
            if (sample is null || string.IsNullOrWhiteSpace(sample.ProcessName))
            {
                return null;
            }
            metrics.Add(WebMetrics.ForegroundApp, sample.ProcessName, count: 1, seconds: seconds, at: at);
            return sample;
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(e, "Could not record foreground usage");
            return null;
        }
    }
}
