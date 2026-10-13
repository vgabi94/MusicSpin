using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
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
/// bitmaps, so scrolling back to an album reuses them. A failed read is retried the next time it is asked for.
/// </summary>
public sealed class CoverService(DataDirectory directory, ICoverResizer resizer) : ICoverService
{
    private readonly ConcurrentDictionary<(string AlbumId, CoverSize Size), Task<Bitmap?>> _covers = new();
    private readonly ConcurrentDictionary<string, Task<Rgb?>> _accents = new(StringComparer.Ordinal);
    private int _bitmapsLoaded;
    private int _accentsComputed;

    /// <summary>Bitmaps decoded so far (diagnostics and tests: a cached cover is not decoded again).</summary>
    public int BitmapsLoaded => Volatile.Read(ref _bitmapsLoaded);

    /// <summary>Accent colours computed so far (once per album).</summary>
    public int AccentsComputed => Volatile.Read(ref _accentsComputed);

    public Task<Bitmap?> CoverAsync(string albumId, CoverSize size)
    {
        ArgumentException.ThrowIfNullOrEmpty(albumId);
        return Cached(_covers, (albumId, size), key => Task.Run(() =>
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
        return Cached(_accents, albumId, id => Task.Run(() =>
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
        using (var library = MusicLibrary.OpenForRead(directory))
        {
            source = library.CoverSources([albumId]).GetValueOrDefault(albumId);
        }

        // The image work happens with the library closed: it holds a cross-process lock.
        return source is null ? null : new CoverStore(directory, resizer).GetPath(source, size);
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
