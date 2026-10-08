using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Tests;

public sealed class PolicyStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Load_returns_null_when_nothing_is_cached()
    {
        Assert.Null(new PolicyStore(_dir.Path).Load());
    }

    [Theory]
    [InlineData(UsbMode.Allow)]
    [InlineData(UsbMode.ReadOnly)]
    [InlineData(UsbMode.Block)]
    public void Save_then_load_round_trips_as_readable_snake_case_json(UsbMode mode)
    {
        var store = new PolicyStore(_dir.Path);
        var policy = new AgentPolicy(5, new UsbPolicy(mode));

        store.Save(policy);

        Assert.Equal(policy, store.Load());
        Assert.Equal(Path.Combine(_dir.Path, "policy.json"), store.FilePath);
        Assert.Contains($"\"mode\": \"{mode.ToWire()}\"", File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Save_overwrites_the_previous_policy()
    {
        var store = new PolicyStore(_dir.Path);
        store.Save(new AgentPolicy(1, new UsbPolicy(UsbMode.Allow)));

        store.Save(new AgentPolicy(2, new UsbPolicy(UsbMode.Block)));

        Assert.Equal(new AgentPolicy(2, new UsbPolicy(UsbMode.Block)), store.Load());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("""{"version": 2, "usb": {"mode": "explode"}}""")]
    [InlineData("""{"version": 2, "usb": {"mode": 9}}""")]
    [InlineData("""{"version": 2}""")]
    [InlineData("null")]
    public void Corrupt_or_malformed_file_loads_as_null(string content)
    {
        var store = new PolicyStore(_dir.Path);
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(store.FilePath, content);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Malformed_policy_is_never_saved()
    {
        var store = new PolicyStore(_dir.Path);

        Assert.Throws<ArgumentException>(() => store.Save(new AgentPolicy(1, new UsbPolicy((UsbMode)42))));
        Assert.False(File.Exists(store.FilePath));
    }

    public void Dispose() => _dir.Dispose();
}
