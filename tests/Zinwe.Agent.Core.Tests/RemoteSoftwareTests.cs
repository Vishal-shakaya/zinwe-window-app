using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.RemoteSoftware;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Tests;

// ---- test doubles for the remote_software feature ----

internal sealed class FakeMemberNotifier : IMemberNotifier
{
    public int Count { get; private set; }
    public string? LastTitle { get; private set; }
    public string? LastBody { get; private set; }

    public void Notify(string title, string? body, string severity, DateTimeOffset? at = null)
    {
        Count++;
        LastTitle = title;
        LastBody = body;
    }
}

internal sealed class FakeInstalledSoftwareSource(IReadOnlyList<InstalledProduct> installed) : IInstalledSoftwareSource
{
    public IReadOnlyList<InstalledProduct> Installed { get; set; } = installed;

    public IReadOnlyList<InstalledProduct> GetInstalled() => Installed;
}

internal sealed class FakeProcessSource(IReadOnlyList<RunningProcess> processes) : IProcessSource
{
    public IReadOnlyList<RunningProcess> Processes { get; set; } = processes;

    public IReadOnlyList<RunningProcess> GetProcesses() => Processes;
}

internal sealed class FakePortSource(IReadOnlyList<ListeningPort> ports) : IListeningPortSource
{
    public IReadOnlyList<ListeningPort> Ports { get; set; } = ports;

    public IReadOnlyList<ListeningPort> GetListeningPorts() => Ports;
}

/// <summary>In-memory baseline so install tests never touch disk.</summary>
internal sealed class FakeInstallBaseline : IInstallBaseline
{
    private HashSet<string>? _keys;

    public int Saves { get; private set; }

    public IReadOnlySet<string>? Load() => _keys;

    public void Save(IReadOnlySet<string> keys)
    {
        Saves++;
        _keys = [.. keys];
    }
}

/// <summary>Records the processes a block targeted; never kills anything.</summary>
internal sealed class RecordingBlocker(bool dryRun) : IRemoteSoftwareBlocker
{
    public List<(string Tool, int Count)> Calls { get; } = [];

    public bool IsDryRun => dryRun;

    public RemoteBlockResult Block(string toolName, IReadOnlyList<RunningProcess> targets)
    {
        Calls.Add((toolName, targets.Count));
        return new RemoteBlockResult(targets.Count, dryRun ? 0 : targets.Count);
    }
}

/// <summary>Records kills instead of making them; used to test the real blocker without touching a process.</summary>
internal sealed class FakeProcessTerminator : IProcessTerminator
{
    public List<int> Terminated { get; } = [];
    public Exception? FailWith { get; set; }

    public void Terminate(int pid)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
        Terminated.Add(pid);
    }
}

public sealed class RemoteSoftwareCatalogTests
{
    [Theory]
    [InlineData(@"C:\Program Files\AnyDesk\AnyDesk.exe", "anydesk")]
    [InlineData("TeamViewer.exe", "teamviewer")]
    [InlineData("RustDesk", "rustdesk")]
    [InlineData("  winVNC.EXE  ", "winvnc")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_reduces_to_a_bare_lowercase_name(string? input, string expected)
    {
        Assert.Equal(expected, RemoteToolCatalog.Normalize(input));
    }

    [Fact]
    public void Built_in_catalog_matches_known_tools_by_process_name()
    {
        var matcher = RemoteToolCatalog.FromOptions(new RemoteSoftwareOptions());

        Assert.Equal("AnyDesk", matcher.MatchProcess("anydesk.exe"));
        Assert.Equal("TeamViewer", matcher.MatchProcess("TeamViewer"));
        Assert.Null(matcher.MatchProcess("notepad"));
    }

    [Fact]
    public void Built_in_catalog_matches_known_ports()
    {
        var matcher = RemoteToolCatalog.FromOptions(new RemoteSoftwareOptions());

        Assert.Equal("AnyDesk", matcher.MatchPort(7070));
        Assert.Equal("TeamViewer", matcher.MatchPort(5938));
        Assert.Null(matcher.MatchPort(443));
    }

    [Fact]
    public void Admin_additions_extend_the_catalog()
    {
        var options = new RemoteSoftwareOptions
        {
            ToolExecutables = ["mycorp_remote.exe"],
            MonitoredPorts = [9999],
        };
        var matcher = RemoteToolCatalog.FromOptions(options);

        Assert.Equal("mycorp_remote", matcher.MatchProcess("mycorp_remote.exe"));
        Assert.Equal("remote service (port 9999)", matcher.MatchPort(9999));
    }

    [Fact]
    public void Disabling_the_built_in_lists_leaves_only_admin_entries()
    {
        var options = new RemoteSoftwareOptions
        {
            UseBuiltInToolList = false,
            UseBuiltInPortList = false,
            ToolExecutables = ["only_this.exe"],
        };
        var matcher = RemoteToolCatalog.FromOptions(options);

        Assert.Null(matcher.MatchProcess("anydesk"));
        Assert.Null(matcher.MatchPort(7070));
        Assert.Equal("only_this", matcher.MatchProcess("only_this"));
    }

    [Fact]
    public void Blocked_list_matches_by_display_name_or_executable()
    {
        var byDisplay = RemoteToolCatalog.FromOptions(new RemoteSoftwareOptions { BlockedTools = ["AnyDesk"] });
        var byExe = RemoteToolCatalog.FromOptions(new RemoteSoftwareOptions { BlockedTools = ["teamviewer.exe"] });

        Assert.True(byDisplay.IsBlocked("AnyDesk"));
        Assert.False(byDisplay.IsBlocked("TeamViewer"));
        Assert.True(byExe.IsBlocked("TeamViewer")); // exe entry resolves to the display name
    }
}

public sealed class InstallMonitorTests
{
    private static InstallMonitor Build(TempDir dir, FakeInstallBaseline baseline, out EventQueue events, out MetricsStore metrics, RemoteSoftwareOptions? options = null, IMemberNotifier? notifier = null)
    {
        events = new EventQueue(dir.Path);
        metrics = new MetricsStore(dir.Path);
        return new InstallMonitor(metrics, events, baseline, notifier ?? new NullMemberNotifier(), options ?? new RemoteSoftwareOptions(), NullLogger<InstallMonitor>.Instance);
    }

    [Fact]
    public void First_scan_seeds_the_baseline_and_reports_nothing()
    {
        using var dir = new TempDir();
        var baseline = new FakeInstallBaseline();
        var monitor = Build(dir, baseline, out var events, out _);

        var reported = monitor.Scan([new InstalledProduct("7-Zip", "Igor Pavlov", "24.08")]);

        Assert.Equal(0, reported);
        Assert.Equal(0, events.Count());
        Assert.Equal(1, baseline.Saves);
    }

    [Fact]
    public void Second_scan_reports_only_new_products()
    {
        using var dir = new TempDir();
        var baseline = new FakeInstallBaseline();
        var monitor = Build(dir, baseline, out var events, out var metrics);

        monitor.Scan([new InstalledProduct("7-Zip", "Igor Pavlov", "24.08")]);
        var reported = monitor.Scan(
        [
            new InstalledProduct("7-Zip", "Igor Pavlov", "24.08"),
            new InstalledProduct("AnyDesk", "AnyDesk Software GmbH", "8.0"),
        ]);

        Assert.Equal(1, reported);
        Assert.Equal(1, events.Count());
        var evt = events.PeekBatch(10).Single();
        Assert.Equal(RemoteSoftwareEventTypes.SoftwareInstalled, evt.Type);
        Assert.Equal(EventSeverity.Warning, evt.Severity);
        Assert.Contains("AnyDesk", evt.DataJson, StringComparison.Ordinal);
        Assert.Contains("software.installed", metrics.Snapshot().Select(r => r.Category));
    }

    [Fact]
    public void A_new_install_notifies_the_member()
    {
        using var dir = new TempDir();
        var baseline = new FakeInstallBaseline();
        var notifier = new FakeMemberNotifier();
        var monitor = Build(dir, baseline, out _, out _, notifier: notifier);

        monitor.Scan([new InstalledProduct("7-Zip", "Igor Pavlov", "24.08")]);   // seeds baseline
        monitor.Scan(
        [
            new InstalledProduct("7-Zip", "Igor Pavlov", "24.08"),
            new InstalledProduct("AnyDesk", "AnyDesk Software GmbH", "8.0"),
        ]);

        Assert.Equal(1, notifier.Count);
        Assert.Contains("AnyDesk", notifier.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_change_counts_as_a_new_install()
    {
        using var dir = new TempDir();
        var baseline = new FakeInstallBaseline();
        var monitor = Build(dir, baseline, out var events, out _);

        monitor.Scan([new InstalledProduct("AnyDesk", "AnyDesk", "8.0")]);
        var reported = monitor.Scan([new InstalledProduct("AnyDesk", "AnyDesk", "8.1")]);

        Assert.Equal(1, reported);
        Assert.Equal(1, events.Count());
    }

    [Fact]
    public void Version_is_included_only_when_details_are_on()
    {
        using var dir = new TempDir();
        var off = Build(dir, new FakeInstallBaseline(), out var offEvents, out _);
        off.Scan([]);
        off.Scan([new InstalledProduct("AnyDesk", "AnyDesk", "8.0")]);
        Assert.DoesNotContain("\"version\"", offEvents.PeekBatch(10).Single().DataJson, StringComparison.Ordinal);

        using var dir2 = new TempDir();
        var on = Build(dir2, new FakeInstallBaseline(), out var onEvents, out _, new RemoteSoftwareOptions { IncludeDetails = true });
        on.Scan([]);
        on.Scan([new InstalledProduct("AnyDesk", "AnyDesk", "8.0")]);
        Assert.Contains("\"version\"", onEvents.PeekBatch(10).Single().DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void File_baseline_round_trips_and_reports_null_before_first_save()
    {
        using var dir = new TempDir();
        var store = new FileInstallBaseline(dir.Path);

        Assert.Null(store.Load());
        store.Save(new HashSet<string> { "a|1", "b|2" });
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(["a|1", "b|2"], loaded!.OrderBy(k => k, StringComparer.Ordinal));
    }
}

public sealed class RemoteToolMonitorTests
{
    private static RemoteToolMonitor Build(
        TempDir dir, IRemoteSoftwareBlocker blocker, out EventQueue events, out MetricsStore metrics, RemoteSoftwareOptions? options = null, IMemberNotifier? notifier = null)
    {
        var opts = options ?? new RemoteSoftwareOptions();
        events = new EventQueue(dir.Path);
        metrics = new MetricsStore(dir.Path);
        var matcher = RemoteToolCatalog.FromOptions(opts);
        return new RemoteToolMonitor(matcher, blocker, metrics, events, notifier ?? new NullMemberNotifier(), opts, NullLogger<RemoteToolMonitor>.Instance);
    }

    [Fact]
    public void A_running_remote_tool_is_detected_once_per_appearance()
    {
        using var dir = new TempDir();
        var monitor = Build(dir, new RecordingBlocker(dryRun: true), out var events, out _);
        var procs = new[] { new RunningProcess("anydesk", 1234), new RunningProcess("notepad", 10) };

        Assert.Equal(1, monitor.Scan(procs, []));
        Assert.Equal(0, monitor.Scan(procs, [])); // still running: not re-reported

        var evt = events.PeekBatch(10).Single();
        Assert.Equal(RemoteSoftwareEventTypes.RemoteToolDetected, evt.Type);
        Assert.Equal(EventSeverity.Warning, evt.Severity);
        Assert.Contains("AnyDesk", evt.DataJson, StringComparison.Ordinal);
        Assert.Contains("process", evt.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_that_leaves_and_returns_is_reported_again()
    {
        using var dir = new TempDir();
        var monitor = Build(dir, new RecordingBlocker(dryRun: true), out var events, out _);
        var procs = new[] { new RunningProcess("teamviewer", 7) };

        Assert.Equal(1, monitor.Scan(procs, []));
        Assert.Equal(0, monitor.Scan([], []));    // gone
        Assert.Equal(1, monitor.Scan(procs, [])); // back

        Assert.Equal(2, events.Count());
    }

    [Fact]
    public void A_listening_port_detects_a_tool_when_no_process_matched()
    {
        using var dir = new TempDir();
        var monitor = Build(dir, new RecordingBlocker(dryRun: true), out var events, out _);

        var appeared = monitor.Scan([], [new ListeningPort(7070, 0, null)]);

        Assert.Equal(1, appeared);
        var evt = events.PeekBatch(10).Single();
        Assert.Contains("AnyDesk", evt.DataJson, StringComparison.Ordinal);
        Assert.Contains("port", evt.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Pid_is_recorded_only_when_details_are_on()
    {
        using var dir = new TempDir();
        var monitor = Build(dir, new RecordingBlocker(dryRun: true), out var events, out _, new RemoteSoftwareOptions { IncludeDetails = true });

        monitor.Scan([new RunningProcess("anydesk", 4242)], []);

        Assert.Contains("4242", events.PeekBatch(10).Single().DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocked_tool_is_passed_to_the_blocker_and_emits_a_blocked_event()
    {
        using var dir = new TempDir();
        var blocker = new RecordingBlocker(dryRun: true);
        var options = new RemoteSoftwareOptions { BlockedTools = ["AnyDesk"] };
        var monitor = Build(dir, blocker, out var events, out _, options);

        monitor.Scan([new RunningProcess("anydesk", 500), new RunningProcess("anydesk", 501)], []);

        Assert.Single(blocker.Calls);
        Assert.Equal(("AnyDesk", 2), blocker.Calls[0]);
        var types = events.PeekBatch(10).Select(e => e.Type).ToList();
        Assert.Contains(RemoteSoftwareEventTypes.RemoteToolDetected, types);
        Assert.Contains(RemoteSoftwareEventTypes.RemoteBlocked, types);
        var blocked = events.PeekBatch(10).Single(e => e.Type == RemoteSoftwareEventTypes.RemoteBlocked);
        Assert.Contains("\"dry_run\":true", blocked.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_block_notifies_the_member_but_a_dry_run_does_not()
    {
        var options = new RemoteSoftwareOptions { BlockedTools = ["AnyDesk"] };

        using (var dir = new TempDir())
        {
            var notifier = new FakeMemberNotifier();
            var monitor = Build(dir, new RecordingBlocker(dryRun: true), out _, out _, options, notifier);
            monitor.Scan([new RunningProcess("anydesk", 1)], []);
            Assert.Equal(0, notifier.Count); // test mode: not really blocked, so no member note
        }

        using (var dir = new TempDir())
        {
            var notifier = new FakeMemberNotifier();
            var monitor = Build(dir, new RecordingBlocker(dryRun: false), out _, out _, options, notifier);
            monitor.Scan([new RunningProcess("anydesk", 1)], []);
            Assert.Equal(1, notifier.Count);
            Assert.Contains("AnyDesk", notifier.LastBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_detected_but_unlisted_tool_is_never_blocked()
    {
        using var dir = new TempDir();
        var blocker = new RecordingBlocker(dryRun: true);
        var monitor = Build(dir, blocker, out var events, out _); // no BlockedTools

        monitor.Scan([new RunningProcess("anydesk", 1)], []);

        Assert.Empty(blocker.Calls);
        Assert.DoesNotContain(events.PeekBatch(10), e => e.Type == RemoteSoftwareEventTypes.RemoteBlocked);
    }

    [Fact]
    public void A_port_only_blocked_tool_emits_no_blocked_event_without_a_pid()
    {
        using var dir = new TempDir();
        var blocker = new RecordingBlocker(dryRun: true);
        var options = new RemoteSoftwareOptions { BlockedTools = ["AnyDesk"] };
        var monitor = Build(dir, blocker, out var events, out _, options);

        monitor.Scan([], [new ListeningPort(7070)]);

        Assert.Empty(blocker.Calls); // nothing to terminate
        Assert.DoesNotContain(events.PeekBatch(10), e => e.Type == RemoteSoftwareEventTypes.RemoteBlocked);
    }
}
