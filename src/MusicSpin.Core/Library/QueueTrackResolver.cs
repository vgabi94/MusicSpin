using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;

namespace MusicSpin.Core.Library;

/// <summary>
/// Points queue entries (queue.json, the player's queue) at the current location of their library tracks. Entries are
/// matched by track id (GUID), so tracks that were renamed, moved or relocated are found at their new path. Entries
/// without a library track id (plain files) are left as they are.
/// </summary>
public static class QueueTrackResolver
{
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Returns the items with current paths/ids. When the library cannot be opened (locked by a scan, corrupt, missing),
    /// the items come back unchanged and <paramref name="problem"/> says why.
    /// </summary>
    public static IReadOnlyList<QueueItem> Resolve(DataDirectory dir, IReadOnlyList<QueueItem> items, TimeSpan? lockTimeout, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(items);
        problem = null;
        if (!items.Any(i => LibraryKeys.IsId(i.TrackId)) || !File.Exists(dir.LibraryDatabaseFile))
        {
            return items;
        }

        try
        {
            using var library = MusicLibrary.OpenForRead(dir, lockTimeout ?? DefaultLockTimeout);
            return library.ResolveQueueItems(items);
        }
        catch (MusicSpinException ex)
        {
            problem = ex.Message;
            return items;
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
            var track = LibraryKeys.IsId(item.TrackId) ? _db.Tracks.FindById(item.TrackId!.ToLowerInvariant()) : null;
            result.Add(track is null ? item : item with { Path = track.Path, TrackId = track.Id });
        }

        return (IReadOnlyList<QueueItem>)result;
    });
}
