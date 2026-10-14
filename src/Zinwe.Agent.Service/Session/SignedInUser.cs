using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zinwe.Agent.Service.Session;

/// <summary>
/// Finds the person signed in to this PC, and their LocalAppData, from the LocalSystem service (Session 0 has no
/// user of its own). Needs SYSTEM's SeTcbPrivilege for <c>WTSQueryUserToken</c>; under a normal account it simply
/// finds nobody.
/// </summary>
internal static class SignedInUser
{
    private const uint NoSession = 0xFFFFFFFF;
    private const int WtsActive = 0;
    private static readonly Guid LocalAppDataFolderId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"); // FOLDERID_LocalAppData

    /// <summary>
    /// The signed-in user's primary token: the console (physical screen) session first, else any other active
    /// session (e.g. Remote Desktop). Null when nobody is signed in. The caller disposes it.
    /// </summary>
    public static SafeAccessTokenHandle? OpenToken()
    {
        var console = WTSGetActiveConsoleSessionId();
        if (console != NoSession && TryQueryToken(console, out var token))
        {
            return token;
        }
        foreach (var session in ActiveSessionIds())
        {
            if (session != console && TryQueryToken(session, out token))
            {
                return token;
            }
        }
        return null;
    }

    /// <summary>That user's %LOCALAPPDATA%, or null if their profile isn't loaded.</summary>
    public static string? LocalAppData(SafeAccessTokenHandle token)
    {
        var folderId = LocalAppDataFolderId;
        var hr = SHGetKnownFolderPath(ref folderId, 0, token.DangerousGetHandle(), out var path);
        try
        {
            return hr == 0 ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>
    /// Start <paramref name="exe"/> on that user's desktop, as them (used to reopen the tray after an update: the
    /// installer runs as SYSTEM in Session 0, which can't show anything to the member). Needs LocalSystem.
    /// </summary>
    public static bool Launch(SafeAccessTokenHandle token, string exe)
    {
        if (!CreateEnvironmentBlock(out var environment, token.DangerousGetHandle(), false))
        {
            environment = IntPtr.Zero;
        }
        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
            var ok = CreateProcessAsUser(
                token.DangerousGetHandle(), exe, $"\"{exe}\"", IntPtr.Zero, IntPtr.Zero, false,
                CreateUnicodeEnvironment, environment, Path.GetDirectoryName(exe), ref startup, out var process);
            if (ok)
            {
                CloseHandle(process.hProcess);
                CloseHandle(process.hThread);
            }
            return ok;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }
        }
    }

    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string? applicationName, string? commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool TryQueryToken(uint sessionId, out SafeAccessTokenHandle token)
    {
        if (WTSQueryUserToken(sessionId, out var raw))
        {
            token = new SafeAccessTokenHandle(raw);
            return true;
        }
        token = SafeAccessTokenHandle.InvalidHandle;
        return false;
    }

    private static List<uint> ActiveSessionIds()
    {
        var ids = new List<uint>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return ids;
        }
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (i * size));
                if (info.State == WtsActive)
                {
                    ids.Add(info.SessionId);
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
        return ids;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateSessionsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
