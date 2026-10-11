using System.Diagnostics;
using Zinwe.Agent.Core.Features.RemoteSoftware;

namespace Zinwe.Agent.Service.Features.RemoteSoftware;

/// <summary>
/// The only code that actually kills a process. Lives in the service (not Core) so no test can construct it, and
/// is instantiated solely by <see cref="RemoteSoftwareBlockerSelector"/> when the process is LocalSystem and dry
/// run is off. Mirrors <c>HklmRegistryWriter</c> / <c>HostsFileWriter</c>: a thin OS wrapper, no policy here.
/// </summary>
internal sealed class ServiceProcessTerminator : IProcessTerminator
{
    public void Terminate(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
    }
}
