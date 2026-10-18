using Avalonia.Media.Imaging;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using SkiaSharp;

namespace MusicSpin.App.Services;

/// <summary>Album covers as bitmaps, and each album's accent colour; both cached for the session.</summary>
public interface ICoverService
{
    /// <summary>The album's cover at <paramref name="size"/>, or null when it has none.</summary>
    Task<Bitmap?> CoverAsync(string albumId, CoverSize size);

    /// <summary>The album's accent base colour (see <see cref="AccentColors.Base"/>), or null when it has no cover.</summary>
    Task<Rgb?> AccentAsync(string albumId);

    /// <summary>
    /// Tells <paramref name="observer"/> (held weakly, so it can be collected) when the album's cover is found to have
    /// changed since it was read (a rescan picked up new art, or art where there was none). It is told on the UI thread;
    /// asking again then gives the new cover.
    /// </summary>
    void Observe(string albumId, ICoverObserver observer);
}

/// <summary>Something showing an album's cover or colour, to be told when it changes (<see cref="ICoverService.Observe"/>).</summary>
public interface ICoverObserver
{
    void CoverChanged();
}

/// <summary>
/// Reads covers through Core's cover cache (<see cref="CoverStore"/>, files in the data folder) and keeps the decoded
/// bitmaps, so scrolling back to an album reuses them. The bitmaps kept are bounded (<see cref="DefaultCacheBytes"/>): past
/// that the least recently asked for are let go (not disposed: a view may still show one; the GC frees it once none does)
/// and decoded again if asked for. A failed read is retried the next time it is asked for. While another process (a
/// scan, say) holds the library, a request doesn't fail: it is tried again at each of the library's checks
/// (<see cref="LibraryService.PollInterval"/>) and completes once the holder lets go, so a cover asked for once (a
/// card's, the player bar's) still shows. When the library changes (<see cref="ILibraryService.Changed"/>) the kept
/// covers and colours are looked up again: one whose image is not the one read is dropped and its observers are told.
/// </summary>
public sealed class CoverService : ICoverService
{
    /// <summary>About 90 Medium covers (2 MB each) or 12 Large ones (16 MB each).</summary>
    public const long DefaultCacheBytes = 192L * 1024 * 1024;

    private readonly DataDirectory _directory;
    private readonly ICoverResizer _resizer;
    private readonly TimeProvider _time;
    private readonly TimeSpan? _lockTimeout;
    private readonly IUiDispatcher? _ui;
    private readonly long _cacheBytes;

    // Guarded by _gate: the entries, the recency list of the decoded covers, their bytes, and the observers.
    private readonly Lock _gate = new();
    private readonly Dictionary<(string AlbumId, CoverSize Size), Entry<Bitmap?>> _covers = [];
    private readonly Dictionary<string, Entry<Rgb?>> _accents = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry<Bitmap?>> _recent = []; // most recently asked for first; loaded covers only
    private readonly Dictionary<string, List<WeakReference<ICoverObserver>>> _observers = new(StringComparer.Ordinal);
    private long _bytes;

    private int _bitmapsLoaded;
    private int _accentsComputed;
    private int _checking; // 1 while a check of the kept covers runs
    private int _checkAgain; // 1 when the library changed again since that check started
    private int _changes; // library changes seen so far

    /// <param name="directory">The data folder.</param>
    /// <param name="resizer">Shrinks covers for Core's cover cache.</param>
    /// <param name="time">The clock of the library's checks (default: the system's).</param>
    /// <param name="lockTimeout">How long each try waits for the library (default <see cref="MusicLibrary.DefaultLockTimeout"/>).</param>
    /// <param name="library">Whose <see cref="ILibraryService.Changed"/> makes the kept covers be looked up again (none: they are kept as read).</param>
    /// <param name="ui">Where observers are told (none: on the thread that found the change).</param>
    /// <param name="cacheBytes">How many bytes of decoded bitmaps to keep (default <see cref="DefaultCacheBytes"/>).</param>
    public CoverService(DataDirectory directory, ICoverResizer resizer, TimeProvider? time = null, TimeSpan? lockTimeout = null,
        ILibraryService? library = null, IUiDispatcher? ui = null, long? cacheBytes = null)
    {
        ArgumentNullException.ThrowIfNull(resizer);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheBytes ?? 0, nameof(cacheBytes));
        _directory = directory;
        _resizer = resizer;
        _time = time ?? TimeProvider.System;
        _lockTimeout = lockTimeout;
        _ui = ui;
        _cacheBytes = cacheBytes ?? DefaultCacheBytes;
        if (library is not null)
        {
            library.Changed += (_, _) => _ = CheckAsync(counted: true);
        }
    }

    /// <summary>Bitmaps decoded so far (diagnostics and tests: a cached cover is not decoded again).</summary>
    public int BitmapsLoaded => Volatile.Read(ref _bitmapsLoaded);

    /// <summary>Accent colours computed so far (once per album, until its cover changes).</summary>
    public int AccentsComputed => Volatile.Read(ref _accentsComputed);

    /// <summary>The bytes of the decoded bitmaps kept now (at most the cache size, unless one cover alone is bigger).</summary>
    public long CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>For tests: runs each time the busy library refuses a try, once its wait for the next check is set.</summary>
    internal Action? Refused { get; set; }

    /// <summary>
    /// For tests: the check that follows a library change (<see cref="ILibraryService.Changed"/>), or the one under way.
    /// Completes once it and any check owed by a change during it are done.
    /// </summary>
    internal Task Checked { get; private set; } = Task.CompletedTask;

    public Task<Bitmap?> CoverAsync(string albumId, CoverSize size)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumId);
        var key = (albumId, size);
        lock (_gate)
        {
            if (_covers.TryGetValue(key, out var kept) && !kept.Task.IsFaulted)
            {
                if (kept.Node is { } node)
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                }

                return kept.Task;
            }

            var entry = new Entry<Bitmap?> { Changes = Volatile.Read(ref _changes) };
            _covers[key] = entry;
            entry.Task = LoadCoverAsync(key, entry);
            return entry.Task;
        }
    }

    public Task<Rgb?> AccentAsync(string albumId)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumId);
        lock (_gate)
        {
            if (_accents.TryGetValue(albumId, out var kept) && !kept.Task.IsFaulted)
            {
                return kept.Task;
            }

            var entry = new Entry<Rgb?> { Changes = Volatile.Read(ref _changes) };
            _accents[albumId] = entry;
            entry.Task = LoadAccentAsync(albumId, entry);
            return entry.Task;
        }
    }

    public void Observe(string albumId, ICoverObserver observer)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumId);
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            if (!_observers.TryGetValue(albumId, out var list))
            {
                _observers[albumId] = list = [];
            }

            list.RemoveAll(w => !w.TryGetTarget(out _));
            list.Add(new WeakReference<ICoverObserver>(observer));
        }
    }

    private async Task<Bitmap?> LoadCoverAsync((string AlbumId, CoverSize Size) key, Entry<Bitmap?> entry)
    {
        try
        {
            var (bitmap, path) = await WhileBusy(() =>
            {
                var path = CoverFile(key.AlbumId, key.Size);
                if (path is null)
                {
                    return ((Bitmap?)null, (string?)null);
                }

                Interlocked.Increment(ref _bitmapsLoaded);
                return (new Bitmap(path), path);
            }).ConfigureAwait(false);
            Admit(key, entry, bitmap, path);
            CheckIfChangedSince(entry);
            return bitmap;
        }
        catch
        {
            Forget(_covers, key, entry);
            throw;
        }
    }

    private async Task<Rgb?> LoadAccentAsync(string albumId, Entry<Rgb?> entry)
    {
        try
        {
            var (accent, path) = await WhileBusy(() =>
            {
                if (CoverFile(albumId, CoverSize.Small) is not { } path)
                {
                    return ((Rgb?)null, (string?)null);
                }

                Interlocked.Increment(ref _accentsComputed);
                using var bitmap = SKBitmap.Decode(path);
                return (bitmap is null ? null : AccentColors.Base(Pixels(bitmap)), path);
            }).ConfigureAwait(false);
            lock (_gate)
            {
                entry.Path = path;
                entry.Loaded = true;
            }

            CheckIfChangedSince(entry);
            return accent;
        }
        catch
        {
            Forget(_accents, albumId, entry);
            throw;
        }
    }

    /// <summary>
    /// A read that began before a library change may have seen the library from before it, and the check that change
    /// started skipped it (it wasn't loaded yet): check once more now that it is.
    /// </summary>
    private void CheckIfChangedSince<T>(Entry<T> entry)
    {
        if (entry.Changes != Volatile.Read(ref _changes))
        {
            _ = CheckAsync(counted: false);
        }
    }

    /// <summary>Records a loaded cover as the most recent and lets go of the least recent ones past the cache size.</summary>
    private void Admit((string AlbumId, CoverSize Size) key, Entry<Bitmap?> entry, Bitmap? bitmap, string? path)
    {
        lock (_gate)
        {
            entry.Path = path;
            entry.Loaded = true;
            if (!_covers.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                return; // dropped while it loaded (its cover changed): the caller still gets it, the cache doesn't keep it
            }

            entry.Bytes = bitmap is null ? 0 : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
            entry.Key = key;
            entry.Node = _recent.AddFirst(entry);
            _bytes += entry.Bytes;
            while (_bytes > _cacheBytes && _recent.Last is { } oldest && !ReferenceEquals(oldest.Value, entry))
            {
                Drop(oldest.Value);
            }
        }
    }

    /// <summary>Removes a loaded cover from the cache (under <see cref="_gate"/>).</summary>
    private void Drop(Entry<Bitmap?> entry)
    {
        if (entry.Node is { } node)
        {
            _recent.Remove(node);
            entry.Node = null;
            _bytes -= entry.Bytes;
        }

        if (entry.Key is { } key && _covers.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            _covers.Remove(key);
        }
    }

    private void Forget<TKey, T>(Dictionary<TKey, Entry<T>> cache, TKey key, Entry<T> entry)
        where TKey : notnull
    {
        lock (_gate)
        {
            if (cache.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                cache.Remove(key);
            }
        }
    }

    /// <summary>
    /// Looks up again the image of every kept cover and colour, and drops those whose image changed. Coalesced: a change
    /// during a check makes it run once more after.
    /// </summary>
    private Task CheckAsync(bool counted)
    {
        if (counted)
        {
            Interlocked.Increment(ref _changes);
        }

        Volatile.Write(ref _checkAgain, 1);
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return Checked; // the check under way looks again when done
        }

        return Checked = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    while (Interlocked.Exchange(ref _checkAgain, 0) == 1)
                    {
                        await CheckOnceAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
                {
                    // Not checked (a library that can't be opened): the covers stay as read until the next change.
                }
                finally
                {
                    Volatile.Write(ref _checking, 0);
                }

                // A change that came between the last check and the release above is ours to serve.
                if (Volatile.Read(ref _checkAgain) == 0 || Interlocked.Exchange(ref _checking, 1) == 1)
                {
                    return;
                }
            }
        });
    }

    private async Task CheckOnceAsync()
    {
        List<((string AlbumId, CoverSize Size) Key, Entry<Bitmap?> Entry)> covers;
        List<(string AlbumId, Entry<Rgb?> Entry)> accents;
        lock (_gate)
        {
            covers = [.. _covers.Where(c => c.Value.Loaded).Select(c => (c.Key, c.Value))];
            accents = [.. _accents.Where(a => a.Value.Loaded).Select(a => (a.Key, a.Value))];
        }

        if (covers.Count == 0 && accents.Count == 0)
        {
            return;
        }

        var albumIds = covers.Select(c => c.Key.AlbumId).Concat(accents.Select(a => a.AlbumId)).Distinct(StringComparer.Ordinal).ToList();
        var changed = await WhileBusy(() =>
        {
            IReadOnlyDictionary<string, CoverSource> sources;
            using (var library = MusicLibrary.OpenForRead(_directory, _lockTimeout))
            {
                sources = library.CoverSources(albumIds);
            }

            var store = new CoverStore(_directory, _resizer);
            string? Now(string albumId, CoverSize size) => sources.GetValueOrDefault(albumId) is { } source ? store.GetPath(source, size) : null;
            return (
                Covers: covers.Where(c => !PathsEqual(c.Entry.Path, Now(c.Key.AlbumId, c.Key.Size))).ToList(),
                Accents: accents.Where(a => !PathsEqual(a.Entry.Path, Now(a.AlbumId, CoverSize.Small))).ToList());
        }).ConfigureAwait(false);

        var albums = new HashSet<string>(StringComparer.Ordinal);
        var told = new List<ICoverObserver>();
        lock (_gate)
        {
            foreach (var (key, entry) in changed.Covers)
            {
                Drop(entry);
                albums.Add(key.AlbumId);
            }

            foreach (var (albumId, entry) in changed.Accents)
            {
                if (_accents.TryGetValue(albumId, out var current) && ReferenceEquals(current, entry))
                {
                    _accents.Remove(albumId);
                }

                albums.Add(albumId);
            }

            foreach (var albumId in albums)
            {
                if (_observers.TryGetValue(albumId, out var list))
                {
                    list.RemoveAll(w => !w.TryGetTarget(out _));
                    told.AddRange(list.Select(w => w.TryGetTarget(out var o) ? o : null).OfType<ICoverObserver>());
                    if (list.Count == 0)
                    {
                        _observers.Remove(albumId);
                    }
                }
            }
        }

        if (told.Count > 0)
        {
            void Tell() => told.ForEach(o => o.CoverChanged());
            if (_ui is null)
            {
                Tell();
            }
            else
            {
                _ui.Post(Tell);
            }
        }
    }

    private static bool PathsEqual(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    private string? CoverFile(string albumId, CoverSize size)
    {
        CoverSource? source;
        using (var library = MusicLibrary.OpenForRead(_directory, _lockTimeout))
        {
            source = library.CoverSources([albumId]).GetValueOrDefault(albumId);
        }

        // The image work happens with the library closed: it holds a cross-process lock.
        return source is null ? null : new CoverStore(_directory, _resizer).GetPath(source, size);
    }

    /// <summary>Runs <paramref name="read"/> on a worker thread, again at each library check while the library is busy.</summary>
    private async Task<T> WhileBusy<T>(Func<T> read)
    {
        while (true)
        {
            try
            {
                return await Task.Run(read).ConfigureAwait(false);
            }
            catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.LibraryLocked)
            {
                // The holder lets go without telling anyone: look again when the library is next checked.
                var nextCheck = Task.Delay(LibraryService.PollInterval, _time);
                Refused?.Invoke();
                await nextCheck.ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<(Rgb, byte)> Pixels(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                yield return (new Rgb(c.Red, c.Green, c.Blue), c.Alpha);
            }
        }
    }

    /// <summary>A cached read: its task and, once loaded, the image file it came from (null: no cover).</summary>
    private sealed class Entry<T>
    {
        public Task<T> Task { get; set; } = null!;

        public bool Loaded { get; set; }

        /// <summary>The library changes seen when the read began.</summary>
        public int Changes { get; init; }

        public string? Path { get; set; }

        public (string AlbumId, CoverSize Size)? Key { get; set; }

        public long Bytes { get; set; }

        public LinkedListNode<Entry<T>>? Node { get; set; }
    }
}
