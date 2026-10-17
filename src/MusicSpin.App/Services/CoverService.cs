using System.Collections.Concurrent;
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
}

/// <summary>
/// Reads covers through Core's cover cache (<see cref="CoverStore"/>, files in the data folder) and keeps the decoded
/// bitmaps, so scrolling back to an album reuses them. A failed read is retried the next time it is asked for. While
/// another process (a scan, say) holds the library, a request doesn't fail: it is tried again at each of the library's
/// checks (<see cref="LibraryService.PollInterval"/>) and completes once the holder lets go, so a cover asked for once
/// (a card's, the player bar's) still shows.
/// </summary>
/// <param name="time">The clock of the library's checks (default: the system's).</param>
/// <param name="lockTimeout">How long each try waits for the library (default <see cref="MusicLibrary.DefaultLockTimeout"/>).</param>
public sealed class CoverService(DataDirectory directory, ICoverResizer resizer, TimeProvider? time = null, TimeSpan? lockTimeout = null) : ICoverService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string AlbumId, CoverSize Size), Task<Bitmap?>> _covers = new();
    private readonly ConcurrentDictionary<string, Task<Rgb?>> _accents = new(StringComparer.Ordinal);
    private int _bitmapsLoaded;
    private int _accentsComputed;

    /// <summary>Bitmaps decoded so far (diagnostics and tests: a cached cover is not decoded again).</summary>
    public int BitmapsLoaded => Volatile.Read(ref _bitmapsLoaded);

    /// <summary>Accent colours computed so far (once per album).</summary>
    public int AccentsComputed => Volatile.Read(ref _accentsComputed);

    /// <summary>For tests: runs each time the busy library refuses a try, once its wait for the next check is set.</summary>
    internal Action? Refused { get; set; }

    public Task<Bitmap?> CoverAsync(string albumId, CoverSize size)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumId);
        return Cached(_covers, (albumId, size), key => WhileBusy(() =>
        {
            if (CoverFile(key.Item1, key.Item2) is not { } path)
            {
                return null;
            }

            Interlocked.Increment(ref _bitmapsLoaded);
            return new Bitmap(path);
        }));
    }

    public Task<Rgb?> AccentAsync(string albumId)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumId);
        return Cached(_accents, albumId, id => WhileBusy(() =>
        {
            if (CoverFile(id, CoverSize.Small) is not { } path)
            {
                return null;
            }

            Interlocked.Increment(ref _accentsComputed);
            using var bitmap = SKBitmap.Decode(path);
            return bitmap is null ? null : AccentColors.Base(Pixels(bitmap));
        }));
    }

    private string? CoverFile(string albumId, CoverSize size)
    {
        CoverSource? source;
        using (var library = MusicLibrary.OpenForRead(directory, lockTimeout))
        {
            source = library.CoverSources([albumId]).GetValueOrDefault(albumId);
        }

        // The image work happens with the library closed: it holds a cross-process lock.
        return source is null ? null : new CoverStore(directory, resizer).GetPath(source, size);
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

    /// <summary>The cached task, or a new one; a faulted task is dropped so the next request retries.</summary>
    private static Task<T> Cached<TKey, T>(ConcurrentDictionary<TKey, Task<T>> cache, TKey key, Func<TKey, Task<T>> load)
        where TKey : notnull
    {
        var task = cache.GetOrAdd(key, load);
        if (task.IsFaulted && cache.TryRemove(new KeyValuePair<TKey, Task<T>>(key, task)))
        {
            task = cache.GetOrAdd(key, load);
        }

        return task;
    }
}
