using System.Text;
using Zinwe.Agent.Core.Identity;

namespace Zinwe.Agent.Core.Tests;

public sealed class IdentityStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_returns_null_when_not_enrolled()
    {
        Assert.Null(new IdentityStore(_dir).Load());
    }

    [Fact]
    public void Save_then_load_round_trips_and_the_file_is_encrypted()
    {
        var store = new IdentityStore(_dir);
        var identity = new DeviceIdentity(42, "super-secret-token", "https://api.zinwe.io", DateTimeOffset.UtcNow);

        store.Save(identity);

        Assert.Equal(identity, store.Load());
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath));
        Assert.DoesNotContain("super-secret-token", raw);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_is_treated_as_not_enrolled()
    {
        var store = new IdentityStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "not a dpapi blob");

        Assert.Null(store.Load());
    }

    [Fact]
    public void Delete_forgets_the_identity()
    {
        var store = new IdentityStore(_dir);
        store.Save(new DeviceIdentity(1, "t", "u", DateTimeOffset.UtcNow));

        store.Delete();
        store.Delete(); // idempotent

        Assert.Null(store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
