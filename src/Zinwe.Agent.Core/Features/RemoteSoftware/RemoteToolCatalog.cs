namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>
/// The built-in knowledge of what a remote-access / remote-control tool looks like, and a matcher built from it
/// plus admin config. Membership is a maintained list, exactly like the web blocklist: the agent makes no
/// judgement about intent, it only matches an executable name or a listening port against a named catalog.
/// </summary>
public static class RemoteToolCatalog
{
    /// <summary>Normalized executable name (lowercase, no ".exe") → display name.</summary>
    public static IReadOnlyDictionary<string, string> BuiltInTools { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anydesk"] = "AnyDesk",
            ["teamviewer"] = "TeamViewer",
            ["teamviewer_service"] = "TeamViewer",
            ["tv_w32"] = "TeamViewer",
            ["tv_x64"] = "TeamViewer",
            ["rustdesk"] = "RustDesk",
            ["winvnc"] = "VNC",
            ["winvnc4"] = "VNC",
            ["tvnserver"] = "VNC",
            ["uvnc_service"] = "VNC",
            ["vncserver"] = "VNC",
            ["ultraviewer"] = "UltraViewer",
            ["ultraviewer_desktop"] = "UltraViewer",
            ["aa_v3"] = "Ammyy Admin",
            ["supremo"] = "Supremo",
            ["supremosystem"] = "Supremo",
            ["quickassist"] = "Quick Assist",
            ["msra"] = "Windows Remote Assistance",
            ["g2mcomm"] = "GoToMyPC",
            ["g2mlauncher"] = "GoToMyPC",
            ["logmein"] = "LogMeIn",
            ["lmiguardiansvc"] = "LogMeIn",
            ["screenconnect.clientservice"] = "ScreenConnect",
            ["connectwisecontrol.client"] = "ScreenConnect",
            ["splashtop"] = "Splashtop",
            ["srservice"] = "Splashtop",
            ["dwagent"] = "DWService",
            ["dwservice"] = "DWService",
            ["rutserv"] = "Remote Utilities",
            ["rfusclient"] = "Remote Utilities",
            ["radmin"] = "Radmin",
            ["rserver3"] = "Radmin",
            ["remoting_host"] = "Chrome Remote Desktop",
        };

    /// <summary>Well-known listening port → display name (default direct-access ports of common remote tools).</summary>
    public static IReadOnlyDictionary<int, string> BuiltInPorts { get; } =
        new Dictionary<int, string>
        {
            [5938] = "TeamViewer",
            [7070] = "AnyDesk",
            [5900] = "VNC",
            [5901] = "VNC",
            [6129] = "DameWare",
            [3389] = "Remote Desktop (RDP)",
            [21116] = "RustDesk",
        };

    /// <summary>"C:\\x\\AnyDesk.exe", "AnyDesk.exe", "AnyDesk" → "anydesk". Null/blank → "".</summary>
    public static string Normalize(string? exeName)
    {
        var name = (exeName ?? "").Trim();
        if (name.Length == 0)
        {
            return "";
        }
        // Drop any directory part, however the path is spelled.
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>Builds the active matcher from options: built-in catalog (unless disabled) plus admin additions.</summary>
    public static RemoteToolMatcher FromOptions(RemoteSoftwareOptions options) => RemoteToolMatcher.Build(options);
}

/// <summary>
/// Immutable matcher: decides whether a process name or a port belongs to a named remote tool, and whether a
/// tool is on the block list. Built once from <see cref="RemoteSoftwareOptions"/>; has no OS dependencies.
/// </summary>
public sealed class RemoteToolMatcher
{
    private readonly Dictionary<string, string> _byName;
    private readonly Dictionary<int, string> _byPort;
    private readonly HashSet<string> _blocked;

    private RemoteToolMatcher(Dictionary<string, string> byName, Dictionary<int, string> byPort, HashSet<string> blocked)
    {
        _byName = byName;
        _byPort = byPort;
        _blocked = blocked;
    }

    /// <summary>Distinct tool display names this matcher knows, for logging/diagnostics.</summary>
    public IReadOnlyCollection<string> KnownTools =>
        _byName.Values.Concat(_byPort.Values).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The display name if <paramref name="processName"/> is a known remote tool, else null.</summary>
    public string? MatchProcess(string? processName)
    {
        var key = RemoteToolCatalog.Normalize(processName);
        return key.Length != 0 && _byName.TryGetValue(key, out var display) ? display : null;
    }

    /// <summary>The display name if <paramref name="port"/> is a known remote-tool port, else null.</summary>
    public string? MatchPort(int port) => _byPort.TryGetValue(port, out var display) ? display : null;

    /// <summary>True when a matched tool's display name is on the block list.</summary>
    public bool IsBlocked(string displayName) => _blocked.Contains(RemoteToolCatalog.Normalize(displayName));

    internal static RemoteToolMatcher Build(RemoteSoftwareOptions options)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.UseBuiltInToolList)
        {
            foreach (var (key, display) in RemoteToolCatalog.BuiltInTools)
            {
                byName[key] = display;
            }
        }
        foreach (var raw in options.ToolExecutables)
        {
            var key = RemoteToolCatalog.Normalize(raw);
            if (key.Length != 0)
            {
                // An admin-supplied executable is its own display name (trimmed, no path/.exe).
                byName[key] = key;
            }
        }

        var byPort = new Dictionary<int, string>();
        if (options.UseBuiltInPortList)
        {
            foreach (var (port, display) in RemoteToolCatalog.BuiltInPorts)
            {
                byPort[port] = display;
            }
        }
        foreach (var port in options.MonitoredPorts)
        {
            if (port is > 0 and <= 65535 && !byPort.ContainsKey(port))
            {
                byPort[port] = $"remote service (port {port})";
            }
        }

        // A block-list entry may name a display ("AnyDesk") or an executable ("anydesk.exe"); normalise both so
        // either way of writing it blocks the right tool.
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in options.BlockedTools)
        {
            var token = RemoteToolCatalog.Normalize(raw);
            if (token.Length == 0)
            {
                continue;
            }
            blocked.Add(token);
            // If the token is a known executable, also block by its display name.
            if (byName.TryGetValue(token, out var display))
            {
                blocked.Add(RemoteToolCatalog.Normalize(display));
            }
        }

        return new RemoteToolMatcher(byName, byPort, blocked);
    }
}
