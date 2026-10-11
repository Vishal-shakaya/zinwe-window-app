using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Features.RemoteSoftware;

namespace Zinwe.Agent.Core.Tests;

public sealed class RemoteSoftwareBlockingTests
{
    [Theory]
    [InlineData(false, false)] // dotnet run as a normal user, dry run off → still dry run
    [InlineData(false, true)]
    [InlineData(true, true)]   // the installed service with DryRun=true → dry run
    public void Real_blocker_is_never_built_unless_system_and_dry_run_off(bool isSystem, bool dryRun)
    {
        var loggers = new ListLoggerFactory();
        var built = false;

        var blocker = RemoteSoftwareBlockerSelector.Select(isSystem, dryRun, () =>
        {
            built = true;
            return new ProcessKillRemoteSoftwareBlocker(new FakeProcessTerminator(), NullLogger<ProcessKillRemoteSoftwareBlocker>.Instance);
        }, loggers);

        Assert.IsType<DryRunRemoteSoftwareBlocker>(blocker);
        Assert.True(blocker.IsDryRun);
        Assert.False(built);
        Assert.Contains(loggers.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Real_blocker_is_used_only_as_system_with_dry_run_off()
    {
        var real = new ProcessKillRemoteSoftwareBlocker(new FakeProcessTerminator(), NullLogger<ProcessKillRemoteSoftwareBlocker>.Instance);

        var blocker = RemoteSoftwareBlockerSelector.Select(
            runningAsSystem: true, dryRunConfigured: false, () => real, new ListLoggerFactory());

        Assert.Same(real, blocker);
    }

    [Fact]
    public void Dry_run_blocker_terminates_nothing_and_logs()
    {
        var loggers = new ListLoggerFactory();
        var blocker = new DryRunRemoteSoftwareBlocker(loggers.CreateLogger<DryRunRemoteSoftwareBlocker>());

        var result = blocker.Block("AnyDesk", [new RunningProcess("anydesk", 1), new RunningProcess("anydesk", 2)]);

        Assert.True(blocker.IsDryRun);
        Assert.Equal(2, result.Attempted);
        Assert.Equal(0, result.Terminated);
        Assert.Contains(loggers.Entries, e => e.Message.Contains("dry run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Real_blocker_terminates_each_target_through_the_terminator()
    {
        var terminator = new FakeProcessTerminator();
        var blocker = new ProcessKillRemoteSoftwareBlocker(terminator, NullLogger<ProcessKillRemoteSoftwareBlocker>.Instance);

        var result = blocker.Block("AnyDesk", [new RunningProcess("anydesk", 10), new RunningProcess("anydesk", 20)]);

        Assert.False(blocker.IsDryRun);
        Assert.Equal(2, result.Attempted);
        Assert.Equal(2, result.Terminated);
        Assert.Equal([10, 20], terminator.Terminated);
    }

    [Fact]
    public void Real_blocker_counts_a_failed_termination_as_attempted_only()
    {
        var terminator = new FakeProcessTerminator { FailWith = new InvalidOperationException("already exited") };
        var blocker = new ProcessKillRemoteSoftwareBlocker(terminator, NullLogger<ProcessKillRemoteSoftwareBlocker>.Instance);

        var result = blocker.Block("AnyDesk", [new RunningProcess("anydesk", 10)]);

        Assert.Equal(1, result.Attempted);
        Assert.Equal(0, result.Terminated);
        Assert.Empty(terminator.Terminated);
    }
}
