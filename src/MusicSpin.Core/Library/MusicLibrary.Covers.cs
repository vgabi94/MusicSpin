using LiteDB;

namespace MusicSpin.Core.Library;

public sealed partial class MusicLibrary
{
    /// <summary>
    /// Where the covers of <paramref name="albumIds"/> come from, for <see cref="CoverStore.GetPath"/>. Only reads the
    /// library (no image files), so it is quick; unknown ids are left out. Read the
    /// sources for a whole page at once and close the library before getting the covers: they can take a while, and the
    /// library is locked while it's open.
    /// </summary>
    public IReadOnlyDictionary<string, CoverSource> CoverSources(IEnumerable<string> albumIds)
    {
        ArgumentNullException.ThrowIfNull(albumIds);
        var ids = new BsonArray(albumIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Select(id => new BsonValue(id)));
        if (ids.Count == 0)
        {
            return new Dictionary<string, CoverSource>(StringComparer.Ordinal);
        }

        return Guard(() => _db.Tracks.Query()
            .Where(BsonExpression.Create("$.AlbumId IN @0", ids))
            .Select(t => new CoverRow
            {
                AlbumId = t.AlbumId, Path = t.Path, Directory = t.Directory, SizeBytes = t.SizeBytes, ModifiedUtcTicks = t.ModifiedUtcTicks,
                HasEmbeddedCover = t.HasEmbeddedCover, SortKey = t.SortKey,
            })
            .ToList()
            .GroupBy(t => t.AlbumId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var tracks = g.OrderBy(t => t.SortKey, StringComparer.Ordinal).ToList();
                return new CoverSource(
                    g.Key,
                    tracks.Where(t => t.HasEmbeddedCover).Select(t => new CoverSource.EmbeddedPicture(t.Path, t.SizeBytes, t.ModifiedUtcTicks)).ToList(),
                    CoverCache.AlbumFolders(tracks.Select(t => t.Directory)));
            }, StringComparer.Ordinal));
    }

    /// <summary>
    /// One album's cover at <paramref name="size"/>: <see cref="CoverSources"/> and <see cref="CoverStore.GetPath"/> in one
    /// call. It reads image files while the library is open, so prefer the two steps for more than a cover or two.
    /// </summary>
    public string? CoverPath(string albumId, CoverSize size, ICoverResizer? resizer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumId);
        return CoverSources([albumId]).TryGetValue(albumId, out var source)
            ? new CoverStore(Directory, resizer).GetPath(source, size)
            : null;
    }

    /// <summary>Drops the cached covers of albums that are gone (after a scan or a folder removal).</summary>
    private void PruneCovers()
        => CoverCache.Prune(Directory.CoversDirectory, _db.Albums.Query().Select(a => a.Id).ToList().ToHashSet(StringComparer.Ordinal));

    /// <summary>The track fields a cover lookup needs.</summary>
    internal sealed class CoverRow
    {
        public string AlbumId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Directory { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public bool HasEmbeddedCover { get; set; }
        public string SortKey { get; set; } = string.Empty;
    }
}
