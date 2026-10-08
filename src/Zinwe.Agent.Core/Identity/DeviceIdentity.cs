using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Identity;

/// <summary>What the backend gave this PC at enrollment. The token is a bearer secret.</summary>
public sealed record DeviceIdentity(long DeviceId, string DeviceToken, string BackendBaseUrl, DateTimeOffset EnrolledAt);

/// <summary>
/// Persists <see cref="DeviceIdentity"/> as a DPAPI-encrypted file (device.dat) in the agent data directory.
/// Under the service this is LocalSystem's DPAPI scope, so other accounts on the PC cannot decrypt it.
/// </summary>
public sealed class IdentityStore(string dataDirectory)
{
    // Not a secret -- DPAPI entropy just binds the blob to this purpose.
    private static readonly byte[] Entropy = "Zinwe.Agent.DeviceIdentity.v1"u8.ToArray();

    public string FilePath { get; } = Path.Combine(dataDirectory, "device.dat");

    /// <summary>Returns null when not enrolled, or when the file is unreadable (treated as "re-enroll").</summary>
    public DeviceIdentity? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(Encoding.UTF8.GetString(plain), IdentityJsonContext.Default.DeviceIdentity);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            return null;
        }
    }

    public void Save(DeviceIdentity identity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity, IdentityJsonContext.Default.DeviceIdentity));
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, FilePath, overwrite: true); // atomic replace: never a half-written identity
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

[JsonSerializable(typeof(DeviceIdentity))]
internal sealed partial class IdentityJsonContext : JsonSerializerContext;
