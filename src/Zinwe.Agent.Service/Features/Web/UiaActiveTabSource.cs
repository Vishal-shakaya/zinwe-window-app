using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Features.Web;

namespace Zinwe.Agent.Service.Features.Web;

/// <summary>
/// Reads the foreground browser's address bar with UI Automation (read-only; the caller keeps only the domain).
/// Works only in the user's interactive session — a session-0 LocalSystem service can't see the desktop, so this
/// returns null there (website usage is then simply not recorded until the agent runs in-session). Never throws.
/// </summary>
public sealed class UiaActiveTabSource(ILogger<UiaActiveTabSource> logger) : IActiveTabSource
{
    // The address bar is an Edit control in the browser chrome; find the first one in the window.
    private static readonly Condition EditCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    public string? ReadAddressText()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            var window = AutomationElement.FromHandle(hwnd);
            var edit = window?.FindFirst(TreeScope.Descendants, EditCondition);
            if (edit is null)
            {
                return null;
            }

            if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value)
            {
                var text = value.Current.Value;
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            return null;
        }
        catch (Exception e) when (
            e is ElementNotAvailableException or COMException or InvalidOperationException
              or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(e, "Could not read the foreground browser's address bar");
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
