using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Update;

/// <summary>The update can't go ahead; <see cref="Exception.Message"/> is shown to the member as is.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Downloads a release's installer and proves it is the one the Zinwe team published: the size (when given) and
/// the SHA-256 from the heartbeat must match, or the file is deleted and nothing is installed. Downloads to a
/// ".part" file first, so a half-finished download is never mistaken for a good installer.
/// </summary>
public sealed partial class UpdateDownloader(BackendClient backend)
{
    /// <summary>The installer is ready to run when this returns; its full path.</summary>
    public async Task<string> DownloadAsync(AgentUpdate update, string deviceToken, string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl) || string.IsNullOrWhiteSpace(update.Sha256))
        {
            throw new UpdateException("Your company hasn't published an installer for this update yet. Ask your admin.");
        }
        if (!SafeVersion().IsMatch(update.Version))
        {
            throw new UpdateException("This update's version number isn't valid.");
        }

        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"ZinweAgent-{update.Version}.msi");
        var partial = target + ".part";
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await backend.DownloadAsync(update.DownloadUrl, deviceToken, file, ct);
            }
            await VerifyAsync(partial, update, ct);
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            TryDelete(partial);
            throw new UpdateException("Couldn't download the update. Check the internet connection and try again.");
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task VerifyAsync(string path, AgentUpdate update, CancellationToken ct)
    {
        var size = new FileInfo(path).Length;
        if (update.Size is { } expected && expected != size)
        {
            throw new UpdateException("The update didn't download completely. Please try again.");
        }
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (!string.Equals(hash, update.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("The downloaded update didn't pass Zinwe's safety check, so it wasn't installed.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a stale .part is overwritten next time
        }
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$")]
    private static partial Regex SafeVersion();
}
