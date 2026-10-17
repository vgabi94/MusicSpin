using System.Globalization;
using System.Text.RegularExpressions;
using MusicSpin.Core.Settings;
using TagLib;

namespace MusicSpin.Core.Library;

/// <summary>Cover sizes the UI asks for, each cached at 2× its on-screen size (longest side, in pixels).</summary>
public enum CoverSize
{
    /// <summary>Track and queue rows, the player bar (about 64 px on screen): 128 px.</summary>
    Small,

    /// <summary>Cards, shelf sleeves, crate records (about 360 px on screen): 720 px.</summary>
    Medium,

    /// <summary>Now Playing and its blurred background (about 1024 px on screen): 2048 px.</summary>
    Large,
}

/// <summary>An encoded image and the file extension for its format (".jpg", ".png", ...).</summary>
public sealed record CoverImage(byte[] Data, string Extension);

/// <summary>
/// Shrinks cover images for the cache. Core has no image library: the app plugs one in (Skia). Without a resizer the
/// cache keeps the original image, whatever the requested size.
/// </summary>
public interface ICoverResizer
{
    /// <summary>
    /// <paramref name="image"/> re-encoded so its longest side is at most <paramref name="maxSize"/> pixels (never
    /// enlarged). Null when the image can't be decoded.
    /// </summary>
    CoverImage? Resize(ReadOnlySpan<byte> image, int maxSize);
}

public static class CoverSizes
{
    /// <summary>The longest side, in pixels, cached for <paramref name="size"/>.</summary>
    public static int Pixels(this CoverSize size) => size switch
    {
        CoverSize.Small => 128,
        CoverSize.Medium => 720,
        CoverSize.Large => 2048,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unknown cover size."),
    };
}

/// <summary>
/// Where an album's cover can come from, best first (from <see cref="MusicLibrary.CoverSources"/>):
/// <list type="number">
/// <item>the pictures embedded in the album's tracks, in album order: the front cover, else the first picture;</item>
/// <item>images in the album's folders: <c>cover</c>, <c>folder</c>, <c>front</c> or <c>album</c> with the extension jpg,
/// jpeg, png or webp (any case). For albums split into disc folders ("CD1", "Disc 2") the parent folder counts too.</item>
/// </list>
/// </summary>
public sealed record CoverSource(string AlbumId, IReadOnlyList<CoverSource.EmbeddedPicture> Embedded, IReadOnlyList<string> Folders)
{
    /// <summary>A track with an embedded picture. Its size and time come from the library, so no file is read to list it.</summary>
    public sealed record EmbeddedPicture(string Path, long SizeBytes, long ModifiedUtcTicks);
}

/// <summary>
/// Album covers cached in <see cref="Hosting.DataDirectory.CoversDirectory"/>, produced on first request. A cached cover is
/// replaced when its source file changes (a scan records the new modification time of a track; folder images are checked
/// on each request). When the data folder can't be written (read-only), covers go to a folder of this process's own in
/// the system's temp folder instead, removed when the process exits. Safe to use from several threads and processes at
/// once. Reads and writes files: keep it off the UI thread.
/// </summary>
/// <param name="resizer">Shrinks covers to the requested size. Without one, the original image is cached for every size.</param>
public sealed class CoverStore(Hosting.DataDirectory dir, ICoverResizer? resizer = null)
{
    /// <summary>For tests: where covers go when the data folder can't be written (default: this process's temp folder).</summary>
    internal Lazy<string?> Fallback { get; init; } = CoverCache.ProcessFallback;

    /// <summary>The path of the album's cover at <paramref name="size"/>; null when none of its sources has a usable image.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not a <see cref="CoverSize"/>.</exception>
    public string? GetPath(CoverSource source, CoverSize size)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CoverCache.Get(dir.CoversDirectory, source, size.Pixels(), resizer, Fallback);
    }
}

/// <summary>
/// The cover files in <c>&lt;data dir&gt;/covers/&lt;album id&gt;/</c>: <c>&lt;source&gt;-&lt;pixels&gt;.&lt;ext&gt;</c>, or
/// <c>&lt;source&gt;-orig.&lt;ext&gt;</c> without a resizer, where &lt;source&gt; is a hash of the source file's path, size
/// and modification time. A changed source gets a new hash, so its old files are replaced on the next request.
/// <c>&lt;source&gt;.none</c> marks a picture that couldn't be used, so the file isn't read again until it changes.
/// Several processes may fill the cache at once: files are written under a temporary name and published atomically.
/// When the cache can't be written, the same files go to a fallback folder (<see cref="ProcessFallback"/>).
/// </summary>
internal static partial class CoverCache
{
    /// <summary>
    /// A folder only this process uses (created on first need with owner-only permissions on Unix, so nothing else can
    /// put a file where a cover is looked for), deleted when it exits; null when even that can't be created.
    /// </summary>
    public static Lazy<string?> ProcessFallback { get; } = new(CreateProcessFallback, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Folder images, best first; any of these extensions.</summary>
    private static readonly string[] FolderNames = ["cover", "folder", "front", "album"];
    private static readonly string[] FolderExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>The path of the cached cover, creating it if needed. Null when the album has no usable cover.</summary>
    public static string? Get(string coversRoot, CoverSource source, int pixels, ICoverResizer? resizer, Lazy<string?> fallback)
    {
        if (!LibraryKeys.IsId(source.AlbumId)) // it becomes a folder name
        {
            throw new ArgumentException($"Not an album id: '{source.AlbumId}'.", nameof(source));
        }

        var dir = Path.Combine(coversRoot, source.AlbumId);
        var sizeName = resizer is null ? "orig" : pixels.ToString(CultureInfo.InvariantCulture);
        foreach (var track in source.Embedded)
        {
            var stamp = Stamp("embedded", track.Path, track.SizeBytes, track.ModifiedUtcTicks);
            if ((Existing(dir, stamp, sizeName) ?? ExistingOutside(fallback, source.AlbumId, stamp, sizeName)) is { } cached)
            {
                return cached;
            }

            if (System.IO.File.Exists(Path.Combine(dir, stamp + ".none")))
            {
                continue;
            }

            if (Encode(ReadEmbedded(track.Path), pixels, resizer) is not { } image)
            {
                MarkUnusable(dir, stamp);
                continue;
            }

            // The cache can't be written (a read-only data folder): the copy outside it shows instead.
            if ((Write(dir, stamp, sizeName, image) ?? WriteOutside(fallback, source.AlbumId, stamp, sizeName, image)) is { } stored)
            {
                return stored;
            }
        }

        foreach (var image in FolderImages(source.Folders))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(image);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var stamp = Stamp("folder", image, info.Length, info.LastWriteTimeUtc.Ticks);
            if ((Existing(dir, stamp, sizeName) ?? ExistingOutside(fallback, source.AlbumId, stamp, sizeName)) is { } cached)
            {
                return cached;
            }

            if (System.IO.File.Exists(Path.Combine(dir, stamp + ".none")))
            {
                continue;
            }

            byte[]? bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(image);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (Encode(bytes, pixels, resizer) is not { } encoded)
            {
                MarkUnusable(dir, stamp);
                continue;
            }

            // The cache can't be written (a read-only data folder): the copy outside it, else the original at full size.
            return Write(dir, stamp, sizeName, encoded) ?? WriteOutside(fallback, source.AlbumId, stamp, sizeName, encoded) ?? image;
        }

        return null;
    }

    /// <summary>Deletes the cached covers of albums that are no longer in the library. Best effort.</summary>
    public static void Prune(string coversRoot, IReadOnlySet<string> albumIds)
    {
        try
        {
            if (!Directory.Exists(coversRoot))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(coversRoot))
            {
                if (!albumIds.Contains(Path.GetFileName(dir)))
                {
                    TryDelete(() => Directory.Delete(dir, recursive: true));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: stale covers only cost disk space
        }
    }

    /// <summary>The image format of <paramref name="data"/> from its first bytes: ".jpg", ".png", ".webp", ".gif", ".bmp", or null.</summary>
    internal static string? ImageExtension(ReadOnlySpan<byte> data)
        => data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg"
            : data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) ? ".png"
            : data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8) ? ".webp"
            : data.StartsWith("GIF8"u8) ? ".gif"
            : data.StartsWith("BM"u8) ? ".bmp"
            : null;

    /// <summary>The front cover embedded in <paramref name="path"/>, else its first picture; null when there is none or the file can't be read.</summary>
    internal static byte[]? ReadEmbedded(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, ReadStyle.PictureLazy);
            var pictures = file.Tag.Pictures ?? [];
            var picture = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? pictures.FirstOrDefault();
            return picture?.Data?.Data is { Length: > 0 } data ? data : null;
        }
        catch (Exception)
        {
            // TagLib# throws arbitrary exceptions on damaged files; a cover is never worth failing for.
            return null;
        }
    }

    /// <summary>The folder images in <paramref name="folders"/>, best first (each folder in turn, then by name and extension).</summary>
    internal static IEnumerable<string> FolderImages(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            Dictionary<string, string> files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .GroupBy(f => Path.GetFileName(f).ToLowerInvariant(), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var name in FolderNames)
            {
                foreach (var extension in FolderExtensions)
                {
                    if (files.TryGetValue(name + extension, out var file))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    /// <summary>The folders to look in for an album whose tracks are in <paramref name="trackDirectories"/>: each of them, then the parent of disc folders ("CD1", "Disc 2").</summary>
    internal static IReadOnlyList<string> AlbumFolders(IEnumerable<string> trackDirectories)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(LibraryKeys.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var parents = new List<string>();
        foreach (var dir in trackDirectories)
        {
            if (seen.Add(dir))
            {
                result.Add(dir);
            }

            if (DiscFolder().IsMatch(Path.GetFileName(dir)) && Path.GetDirectoryName(dir) is { Length: > 0 } parent)
            {
                parents.Add(parent);
            }
        }

        result.AddRange(parents.Where(seen.Add));
        return result;
    }

    private static string Stamp(string kind, string path, long size, long ticks)
        => LibraryKeys.Hash(string.Join('\u001f', kind, path, size.ToString(CultureInfo.InvariantCulture), ticks.ToString(CultureInfo.InvariantCulture)))[..16];

    private static string? Existing(string dir, string stamp, string sizeName)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, $"{stamp}-{sizeName}.*").FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.Ordinal)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The image as cached at <paramref name="pixels"/>; null when it is unusable (no picture, an unknown format, or the resizer can't decode it).</summary>
    private static CoverImage? Encode(byte[]? source, int pixels, ICoverResizer? resizer)
    {
        if (source is null || ImageExtension(source) is not { } extension)
        {
            return null;
        }

        try
        {
            return resizer is null ? new CoverImage(source, extension) : resizer.Resize(source, pixels);
        }
        catch (Exception)
        {
            return null; // a decoder that throws on a damaged image counts as "can't decode"
        }
    }

    /// <summary>The cover in the fallback folder, if that folder has been made (it isn't made just to look).</summary>
    private static string? ExistingOutside(Lazy<string?> fallback, string albumId, string stamp, string sizeName)
        => fallback.IsValueCreated && fallback.Value is { } root ? Existing(Path.Combine(root, albumId), stamp, sizeName) : null;

    /// <summary>Writes the cover to the fallback folder; null when there is none or it can't be written either.</summary>
    private static string? WriteOutside(Lazy<string?> fallback, string albumId, string stamp, string sizeName, CoverImage image)
        => fallback.Value is { } root ? Write(Path.Combine(root, albumId), stamp, sizeName, image) : null;

    private static string? CreateProcessFallback()
    {
        try
        {
            var root = Directory.CreateTempSubdirectory("musicspin-covers-").FullName;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(() => Directory.Delete(root, recursive: true));
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the cover for one source and size into <paramref name="dir"/>; null when it can't be written.</summary>
    private static string? Write(string dir, string stamp, string sizeName, CoverImage image)
    {
        var destination = Path.Combine(dir, $"{stamp}-{sizeName}{image.Extension}");
        var temp = Path.Combine(dir, $"{stamp}-{sizeName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(temp, image.Data);
            if (!AtomicFile.TryPublish(temp, destination))
            {
                TryDelete(() => System.IO.File.Delete(temp)); // another process wrote the same file first
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(() => System.IO.File.Delete(temp));
            return null;
        }

        RemoveOtherSources(dir, stamp);
        return destination;
    }

    private static void MarkUnusable(string dir, string stamp)
    {
        try
        {
            Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(Path.Combine(dir, stamp + ".none"), []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the picture is just read again next time
        }
    }

    /// <summary>Removes the files of earlier sources (a changed cover, or one that is no longer the album's best).</summary>
    private static void RemoveOtherSources(string dir, string stamp)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith(stamp, StringComparison.Ordinal) && !name.EndsWith(".none", StringComparison.Ordinal) && !name.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    TryDelete(() => System.IO.File.Delete(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use by another process, or already gone
        }
    }

    [GeneratedRegex(@"^(cd|disc|disk)[\s._-]*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscFolder();
}
