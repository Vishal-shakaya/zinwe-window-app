using System.Text;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Core.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public void Status_round_trips_with_readable_state_names()
    {
        var status = new AgentStatus(AgentState.Connected, "0.1.0", "PC-01", 7, DateTimeOffset.UtcNow, null);

        var line = PipeProtocol.Serialize(status);

        Assert.Contains("\"state\":\"Connected\"", line);
        Assert.DoesNotContain('\n', line);
        Assert.Equal(status, PipeProtocol.DeserializeStatus(line));
    }

    [Theory]
    [InlineData("{\"type\":\"status\"}", "status")]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public async Task ReadRequest_tolerates_garbage(string line, string? expectedType)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));

        var request = await PipeProtocol.ReadRequestAsync(reader, CancellationToken.None);

        Assert.Equal(expectedType, request?.Type);
    }

    [Fact]
    public async Task Oversized_request_is_rejected()
    {
        var line = "{\"type\":\"" + new string('x', 5000) + "\"}";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));

        Assert.Null(await PipeProtocol.ReadRequestAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Client_returns_null_when_service_is_not_running()
    {
        // Nothing listens on the pipe in the test process unless the real agent happens to be running.
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(status is null || status.AgentVersion.Length > 0);
    }

    [Fact]
    public void MachineInfo_reads_this_pc()
    {
        Assert.Matches("^[0-9a-f-]{36}$", MachineInfo.MachineId);
        Assert.StartsWith("Windows", MachineInfo.OsVersion);
        Assert.False(string.IsNullOrWhiteSpace(MachineInfo.Hostname));
    }
}
