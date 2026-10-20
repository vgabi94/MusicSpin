using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>Album items for a fresh read of the albums (the Albums page, and the Tracks page's album headers).</summary>
internal static class AlbumItems
{
    /// <summary>
    /// An album that is unchanged keeps its item, with its cover, hover and playing mark; a changed or new one gets a new
    /// item from <paramref name="create"/>. In the order read. One pass over each list (a reload runs every couple of
    /// seconds while a scan does, so matching each album against every item was quadratic in the library's size).
    /// </summary>
    /// <param name="ids">Compares album ids (a seam for tests that count the work; ordinal by default).</param>
    public static List<AlbumItemViewModel> Reuse(IReadOnlyList<AlbumSummary> read, IReadOnlyList<AlbumItemViewModel> previous,
        Func<AlbumSummary, AlbumItemViewModel> create, IEqualityComparer<string>? ids = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(create);
        var byId = new Dictionary<string, AlbumItemViewModel>(previous.Count, ids ?? StringComparer.Ordinal);
        foreach (var item in previous)
        {
            byId.TryAdd(item.Album.Id, item);
        }

        var items = new List<AlbumItemViewModel>(read.Count);
        foreach (var album in read)
        {
            items.Add(byId.TryGetValue(album.Id, out var item) && item.Album == album ? item : create(album));
        }

        return items;
    }
}
