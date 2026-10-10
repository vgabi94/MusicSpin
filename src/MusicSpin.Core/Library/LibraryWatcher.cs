namespace MusicSpin.Core.Library;

/// <summary>
/// Basic file-system sync: watches library folders and raises <see cref="Changed"/> once activity has been quiet
/// for <see cref="Debounce"/>. The handler is expected to run an incremental scan (cheap: unchanged files are skipped).
/// Watcher buffer overflows also trigger a rescan, so missed events are eventually reconciled.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    public LibraryWatcher(IEnumerable<string> folders, TimeSpan debounce, TimeProvider? time = null)
        : this(folders, debounce, time, folder => new FileSystemWatcher(folder))
    {
    }

    /// <param name="createWatcher">Seam for tests: a watcher subclass can raise bursts, renames and buffer overflows on demand.</param>
    internal LibraryWatcher(IEnumerable<string> folders, TimeSpan debounce, TimeProvider? time, Func<string, FileSystemWatcher> createWatcher)
    {
        ArgumentNullException.ThrowIfNull(folders);
        Debounce = debounce;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var watcher = createWatcher(folder);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.InternalBufferSize = 64 * 1024;
            watcher.Created += OnEvent;
            watcher.Changed += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += (s, e) => { OnEvent(s, e); Touch(e.OldFullPath); };
            watcher.Error += (_, _) => Touch("*overflow*");
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public TimeSpan Debounce { get; }

    public int WatchedFolders => _watchers.Count;

    /// <summary>Raised (on a thread-pool thread) with the changed paths after a quiet period.</summary>
    public event EventHandler<IReadOnlyCollection<string>>? Changed;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _timer.Dispose();
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        // Directory events matter (folder moved/deleted); for files only audio extensions do.
        if (AudioFileTypes.IsAudioFile(e.FullPath) || !Path.HasExtension(e.FullPath) || Directory.Exists(e.FullPath))
        {
            Touch(e.FullPath);
        }
    }

    private void Touch(string path)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(path);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] paths;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            paths = [.. _pending];
            _pending.Clear();
        }

        Changed?.Invoke(this, paths);
    }
}
