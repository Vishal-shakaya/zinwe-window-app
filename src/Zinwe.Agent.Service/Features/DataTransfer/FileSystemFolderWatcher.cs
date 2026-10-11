using Zinwe.Agent.Core.Features.DataTransfer;

namespace Zinwe.Agent.Service.Features.DataTransfer;

/// <summary>Builds a real <see cref="FileSystemWatcher"/>-backed watcher for a target root.</summary>
internal sealed class FileSystemFolderWatcherFactory(ILoggerFactory loggers) : IFolderWatcherFactory
{
    public IFolderWatcher Create(TransferTarget target) =>
        new FileSystemFolderWatcher(target, loggers.CreateLogger<FileSystemFolderWatcher>());
}

/// <summary>
/// Watches a drive root recursively and reports each created/changed FILE as a <see cref="FileWriteNotice"/>,
/// reading its current size best-effort. It does not dedupe the many notifications Windows raises per file —
/// <see cref="DataTransferLogger"/> does that by path. Size/count only; the path is used by the logger just to
/// dedupe and (when details are on) for the leaf name.
/// </summary>
internal sealed partial class FileSystemFolderWatcher(TransferTarget target, ILogger<FileSystemFolderWatcher> logger)
    : IFolderWatcher
{
    private FileSystemWatcher? _fsw;

    public TransferTarget Target => target;

    public event EventHandler<FileWriteNotice>? FileWritten;

    public void Start()
    {
        var fsw = new FileSystemWatcher(target.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        fsw.Created += OnChanged;
        fsw.Changed += OnChanged;
        fsw.Renamed += OnRenamed;
        fsw.Error += OnError;
        fsw.EnableRaisingEvents = true;
        _fsw = fsw;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Report(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => Report(e.FullPath);

    private void Report(string fullPath)
    {
        try
        {
            // Directories and vanished temp files are not copies we count.
            var info = new FileInfo(fullPath);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            {
                return;
            }
            FileWritten?.Invoke(this, new FileWriteNotice(fullPath, info.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Locked, mid-write or malformed path: skip this notice (the next one for the file usually succeeds).
        }
    }

    private void OnError(object sender, ErrorEventArgs e) => LogWatcherError(target.Key, e.GetException().Message);

    public void Dispose()
    {
        if (_fsw is null)
        {
            return;
        }
        try
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Created -= OnChanged;
            _fsw.Changed -= OnChanged;
            _fsw.Renamed -= OnRenamed;
            _fsw.Error -= OnError;
            _fsw.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // already torn down
        }
        _fsw = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "File watcher for {Key} overflowed/errored: {Error}")]
    private partial void LogWatcherError(string key, string error);
}
