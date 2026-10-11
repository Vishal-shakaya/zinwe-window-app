using System.Windows.Threading;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// Polls the per-user notifications file on a timer and raises <see cref="NewItems"/> for ids not seen
/// before. One instance lives in the tray (always running, so toasts fire even when the widget is hidden);
/// the widget subscribes to the same instance to refresh its list. Polling (not FileSystemWatcher) keeps
/// this robust against roaming/locked files and partial writes.
/// </summary>
public sealed class NotificationWatcher : IDisposable
{
    private readonly NotificationStore _store;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _primed;

    /// <summary>Raised on the UI thread with the newly-arrived items (newest first). Never empty.</summary>
    public event Action<IReadOnlyList<NotificationItem>>? NewItems;

    /// <summary>Raised on every poll with the full current file, so subscribers can re-render.</summary>
    public event Action<NotificationFile>? Refreshed;

    public NotificationWatcher(NotificationStore store, TimeSpan interval)
    {
        _store = store;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    /// <summary>Reads the file now and fires events. Safe to call from the UI thread at any time.</summary>
    public NotificationFile Poll()
    {
        var file = _store.Load();

        // First poll only establishes the baseline so we don't toast the entire backlog at startup.
        if (!_primed)
        {
            foreach (var item in file.Items)
            {
                _seen.Add(item.Id);
            }
            _primed = true;
            Refreshed?.Invoke(file);
            return file;
        }

        var fresh = new List<NotificationItem>();
        foreach (var item in file.Items)
        {
            if (_seen.Add(item.Id))
            {
                fresh.Add(item);
            }
        }

        Refreshed?.Invoke(file);
        if (fresh.Count > 0)
        {
            // Newest first.
            fresh.Sort((a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
            NewItems?.Invoke(fresh);
        }
        return file;
    }

    public void Dispose() => _timer.Stop();
}
