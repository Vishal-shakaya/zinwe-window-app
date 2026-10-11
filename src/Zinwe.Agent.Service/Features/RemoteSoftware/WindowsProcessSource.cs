using System.Diagnostics;
using Zinwe.Agent.Core.Features.RemoteSoftware;

namespace Zinwe.Agent.Service.Features.RemoteSoftware;

/// <summary>
/// Lists the processes running now via <see cref="Process.GetProcesses"/> (read-only detection, safe to run
/// live). Only the process name and id are read; no command line, window title or module list is touched.
/// </summary>
internal sealed partial class WindowsProcessSource(ILogger<WindowsProcessSource> logger) : IProcessSource
{
    public IReadOnlyList<RunningProcess> GetProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogListFailed(e.Message);
            return [];
        }

        var result = new List<RunningProcess>(processes.Length);
        foreach (var process in processes)
        {
            try
            {
                var name = process.ProcessName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new RunningProcess(name, process.Id));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // process exited between enumeration and read; skip it
            }
            finally
            {
                process.Dispose();
            }
        }
        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not enumerate processes ({Error})")]
    private partial void LogListFailed(string error);
}
