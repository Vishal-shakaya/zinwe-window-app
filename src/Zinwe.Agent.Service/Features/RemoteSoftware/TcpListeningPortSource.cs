using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Zinwe.Agent.Core.Features.RemoteSoftware;

namespace Zinwe.Agent.Service.Features.RemoteSoftware;

/// <summary>
/// Lists TCP ports in the LISTEN state (read-only detection). Ports come from the fully-managed
/// <see cref="IPGlobalProperties.GetActiveTcpListeners"/> (IPv4 + IPv6); the owning process id/name is enriched
/// best-effort from the IPv4 extended TCP table (iphlpapi). A port with no resolvable owner is still reported
/// with pid 0 and no name.
/// </summary>
internal sealed partial class TcpListeningPortSource(ILogger<TcpListeningPortSource> logger) : IListeningPortSource
{
    public IReadOnlyList<ListeningPort> GetListeningPorts()
    {
        var ports = ListeningPorts();
        if (ports.Count == 0)
        {
            return [];
        }

        var pidByPort = OwningPidsByPort();
        var nameByPid = new Dictionary<int, string?>();
        var result = new List<ListeningPort>(ports.Count);
        foreach (var port in ports)
        {
            var pid = pidByPort.GetValueOrDefault(port, 0);
            string? name = null;
            if (pid > 0)
            {
                if (!nameByPid.TryGetValue(pid, out name))
                {
                    name = ResolveName(pid);
                    nameByPid[pid] = name;
                }
            }
            result.Add(new ListeningPort(port, pid, name));
        }
        return result;
    }

    private HashSet<int> ListeningPorts()
    {
        var ports = new HashSet<int>();
        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                ports.Add(endpoint.Port);
            }
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            LogListenersFailed(e.Message);
        }
        return ports;
    }

    private static string? ResolveName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---- best-effort IPv4 port → owning pid via GetExtendedTcpTable ----

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3; // MIB_TCP_TABLE_OWNER_PID_LISTENER
    private const int ErrorInsufficientBuffer = 122;

    private Dictionary<int, int> OwningPidsByPort()
    {
        var map = new Dictionary<int, int>();
        var buffer = IntPtr.Zero;
        try
        {
            var size = 0;
            var ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (ret != ErrorInsufficientBuffer || size <= 0)
            {
                return map;
            }
            buffer = Marshal.AllocHGlobal(size);
            ret = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (ret != 0)
            {
                return map;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr + (i * rowSize));
                var port = NetworkPort(row.LocalPort);
                map.TryAdd(port, row.OwningPid);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
        {
            LogPidMapFailed(e.Message);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return map;
    }

    /// <summary>Low 16 bits hold the port in network byte order; swap to host order.</summary>
    private static int NetworkPort(uint dwLocalPort)
    {
        var low = dwLocalPort & 0xFFFF;
        return (int)(((low & 0xFF) << 8) | ((low >> 8) & 0xFF));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }

#pragma warning disable SYSLIB1054
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);
#pragma warning restore SYSLIB1054

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not list TCP listeners ({Error})")]
    private partial void LogListenersFailed(string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not map listening ports to pids ({Error})")]
    private partial void LogPidMapFailed(string error);
}
