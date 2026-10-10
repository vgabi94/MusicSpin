using LiteDB;

namespace MusicSpin.Core.Library;

public sealed partial class MusicLibrary
{
    private static readonly char[] GenreSeparators = [';', '/', ',', '|', '\u0000'];

    /// <summary>
    /// The tracks a radio station with <paramref name="filter"/> may play (see <see cref="RadioFilter"/> for the semantics),
    /// as compact candidates in library order. Unknown --artist/--album values are NotFound, like the listings.
    /// </summary>
    public IReadOnlyList<RadioCandidate> RadioPool(RadioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.EnsureValid();
        var searches = filter.Searches?.Select(s => LibrarySearch.Parse(s)).ToList();
        var genres = filter.Genres?.Select(LibrarySearch.Fold).Where(g => g.Length > 0).ToHashSet(StringComparer.Ordinal);
        return Guard(() =>
        {
            var q = _db.Tracks.Query();
            if (filter.Artists is { Count: > 0 } artists)
            {
                var keys = new BsonArray(artists.SelectMany(ArtistKeys).Distinct(StringComparer.Ordinal).Select(k => new BsonValue(k)));
                q = q.Where(BsonExpression.Create("($.ArtistKey IN @0 OR $.AlbumArtistKey IN @0)", keys));
            }

            if (filter.Albums is { Count: > 0 } albums)
            {
                q = q.Where(BsonExpression.Create("$.AlbumId IN @0", new BsonArray(albums.SelectMany(AlbumIds).Distinct(StringComparer.Ordinal).Select(i => new BsonValue(i)))));
            }

            if (filter.Formats is { Count: > 0 } formats)
            {
                q = q.Where(BsonExpression.Create("$.Format IN @0", new BsonArray(formats.Select(f => new BsonValue(f.Trim().ToLowerInvariant())))));
            }

            if (filter.HiRes)
            {
                q = q.Where(t => t.IsHiRes);
            }

            var pool = new List<RadioCandidate>();
            foreach (var t in q.OrderBy(t => t.SortKey).ToEnumerable())
            {
                if (filter.Years is { Count: > 0 } years && !years.Any(y => y.Contains(t.Year)))
                {
                    continue;
                }

                if (genres is not null && (genres.Count == 0 || !GenreMatches(t.Genre, genres)))
                {
                    continue;
                }

                if (searches is { Count: > 0 } && !searches.Any(s => LibrarySearch.Score(s, t) is not null))
                {
                    continue;
                }

                pool.Add(new RadioCandidate(t.Id, t.Path, t.ArtistKey, t.AlbumId, t.Title, t.Artist, t.Album, t.EffectiveAlbumArtist,
                    t.TrackNumber, t.DurationSeconds > 0 ? t.DurationSeconds : null));
            }

            return (IReadOnlyList<RadioCandidate>)pool;
        });
    }

    private static bool GenreMatches(string? genre, HashSet<string> wanted)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return false;
        }

        return wanted.Contains(LibrarySearch.Fold(genre))
            || genre.Split(GenreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(g => wanted.Contains(LibrarySearch.Fold(g)));
    }
}
