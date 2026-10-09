using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;

namespace MusicSpin.Core.Library;

/// <summary>
/// Points queue entries (queue.json, the player's queue) at the current location of their library tracks. Entries are
/// matched by track id (GUID), so tracks that were renamed, moved or relocated are found at their new path; entries
/// without a valid id (plain files, queue.json from before schema 3) are linked to a track by path when there is one.
/// </summary>
public static class QueueTrackResolver
{
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Returns the items with current paths/ids. When the library cannot be opened (locked by a scan, corrupt, missing),
    /// the items come back unchanged except that invalid track ids are dropped, and <paramref name="problem"/> says why.
    /// </summary>
    public static IReadOnlyList<QueueItem> Resolve(DataDirectory dir, IReadOnlyList<QueueItem> items, TimeSpan? lockTimeout, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(items);
        problem = null;
        var cleaned = items.Select(i => i.TrackId is null || LibraryKeys.IsId(i.TrackId) ? i : i with { TrackId = null }).ToList();
        if (cleaned.Count == 0 || !File.Exists(dir.LibraryDatabaseFile))
        {
            return cleaned;
        }

        try
        {
            using var library = MusicLibrary.OpenForRead(dir, lockTimeout ?? DefaultLockTimeout);
            return library.ResolveQueueItems(cleaned);
        }
        catch (MusicSpinException ex)
        {
            problem = ex.Message;
            return cleaned;
        }
    }
}

public sealed partial class MusicLibrary
{
    /// <summary>See <see cref="QueueTrackResolver"/>.</summary>
    public IReadOnlyList<QueueItem> ResolveQueueItems(IReadOnlyList<QueueItem> items) => Guard(() =>
    {
        var result = new List<QueueItem>(items.Count);
        foreach (var item in items)
        {
            TrackRecord? track = null;
            if (item.TrackId is { } id && LibraryKeys.IsId(id))
            {
                track = _db.Tracks.FindById(id.ToLowerInvariant());
            }

            if (track is null && Path.IsPathFullyQualified(item.Path))
            {
                var key = LibraryKeys.PathKey(LibraryKeys.NormalizePath(item.Path));
                track = _db.Tracks.FindOne(t => t.PathKey == key);
            }

            result.Add(track is null
                ? item with { TrackId = item.TrackId is { } t && LibraryKeys.IsId(t) ? t : null }
                : item with { Path = track.Path, TrackId = track.Id });
        }

        return (IReadOnlyList<QueueItem>)result;
    });
}
