using System.Text.Json;

namespace Zinwe.Agent.Core.Features.RemoteSoftware;

/// <summary>
/// Remembers which products were already installed, so only genuinely NEW installs are reported. On first ever
/// run the current list is saved silently as the baseline (nothing is reported for software that predates the
/// agent); later runs diff against it. The store holds only opaque identity keys — never names, publishers or
/// paths in a form the owner would read — and lives next to the other agent databases.
/// </summary>
public interface IInstallBaseline
{
    /// <summary>The stored keys, or null when no baseline has been written yet (first run).</summary>
    IReadOnlySet<string>? Load();

    /// <summary>Replace the stored baseline with <paramref name="keys"/>.</summary>
    void Save(IReadOnlySet<string> keys);
}

/// <summary>File-backed baseline (DataDirectory\remote-software-baseline.json). Never throws: a read/write failure
/// degrades to "no baseline" / "not saved" and is left to the caller's logger.</summary>
public sealed class FileInstallBaseline : IInstallBaseline
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public FileInstallBaseline(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "remote-software-baseline.json");
    }

    public string FilePath => _path;

    public IReadOnlySet<string>? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            try
            {
                var json = File.ReadAllText(_path);
                var keys = JsonSerializer.Deserialize<string[]>(json);
                return keys is null ? new HashSet<string>() : new HashSet<string>(keys, StringComparer.Ordinal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Corrupt or unreadable: treat as "no baseline" so we re-seed rather than spam the feed.
                return null;
            }
        }
    }

    public void Save(IReadOnlySet<string> keys)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
                File.WriteAllText(_path, json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort; a missed save just means a re-seed or a repeat next run, never a crash.
            }
        }
    }
}
