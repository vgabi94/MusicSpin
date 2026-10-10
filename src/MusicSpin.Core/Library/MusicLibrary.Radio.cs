using LiteDB;

namespace MusicSpin.Core.Library;

public sealed partial class MusicLibrary
{
    private static readonly char[] GenreSeparators = [';', '/', ',', '|', '\u0000'];

    /// <summary>
    /// The tracks a radio station with <paramref name="filter"/> may play (see <see cref="RadioFilter"/> for the semantics),
    /// as compact candidates. Unknown --artist/--album values are NotFound, like the listings.
    /// </summary>
    public IReadOnlyList<RadioCandidate> RadioPool(RadioFilter filter)
    {
        var (searches, genres) = Prepare(filter);
        return Guard(() => (IReadOnlyList<RadioCandidate>)RadioMatches(filter, searches, genres).ToList());
    }

    /// <summary>
    /// Whether any track matches (stops at the first one, so it is fast unless nothing matches). Same errors as
    /// <see cref="RadioPool"/>: lets callers fail early without reading the whole pool.
    /// </summary>
    public bool HasRadioTracks(RadioFilter filter)
    {
        var (searches, genres) = Prepare(filter);
        return Guard(() => RadioMatches(filter, searches, genres).Any());
    }

    private static (List<SearchQuery>? Searches, HashSet<string>? Genres) Prepare(RadioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.EnsureValid();
        return (filter.Searches?.Select(s => LibrarySearch.Parse(s)).ToList(),
            filter.Genres?.Select(LibrarySearch.Fold).Where(g => g.Length > 0).ToHashSet(StringComparer.Ordinal));
    }

    private IEnumerable<RadioCandidate> RadioMatches(RadioFilter filter, List<SearchQuery>? searches, HashSet<string>? genres)
    {
        // Artist: track artist OR album artist. Two indexed queries (an OR would scan the whole collection), merged by id.
        var roots = new List<ILiteQueryable<TrackRecord>>();
        if (filter.Artists is { Count: > 0 } artists)
        {
            var keys = new BsonArray(artists.SelectMany(ArtistKeys).Distinct(StringComparer.Ordinal).Select(k => new BsonValue(k)));
            roots.Add(_db.Tracks.Query().Where(BsonExpression.Create("$.ArtistKey IN @0", keys)));
            roots.Add(_db.Tracks.Query().Where(BsonExpression.Create("$.AlbumArtistKey IN @0", keys)));
        }
        else
        {
            roots.Add(_db.Tracks.Query());
        }

        var albumIds = filter.Albums is { Count: > 0 } albums
            ? new BsonArray(albums.SelectMany(AlbumIds).Distinct(StringComparer.Ordinal).Select(i => new BsonValue(i)))
            : null;
        bool Keep(int? year, string? genre)
            => (filter.Years is not { Count: > 0 } years || years.Any(y => y.Contains(year)))
               && (genres is null || (genres.Count > 0 && GenreMatches(genre, genres)));

        var seen = roots.Count > 1 ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (var root in roots)
        {
            var q = root;
            if (albumIds is not null)
            {
                q = q.Where(BsonExpression.Create("$.AlbumId IN @0", albumIds));
            }

            if (filter.Formats is { Count: > 0 } formats)
            {
                q = q.Where(BsonExpression.Create("$.Format IN @0", new BsonArray(formats.Select(f => new BsonValue(f.Trim().ToLowerInvariant())))));
            }

            if (filter.HiRes)
            {
                q = q.Where(t => t.IsHiRes);
            }

            if (searches is { Count: > 0 })
            {
                // Search rules need the folded keys: read whole records.
                foreach (var t in q.ToEnumerable())
                {
                    if (Keep(t.Year, t.Genre) && searches.Any(s => LibrarySearch.Score(s, t) is not null) && (seen?.Add(t.Id) ?? true))
                    {
                        yield return new RadioCandidate(t.Id, t.Path, t.ArtistKey, t.AlbumId, t.Title, t.Artist, t.Album, t.EffectiveAlbumArtist,
                            t.TrackNumber, t.DurationSeconds > 0 ? t.DurationSeconds : null);
                    }
                }

                continue;
            }

            // Only the fields radio needs (cheaper than mapping whole records on big libraries).
            foreach (var t in q.Select(t => new RadioRow
                     {
                         Id = t.Id, Path = t.Path, ArtistKey = t.ArtistKey, AlbumId = t.AlbumId, Title = t.Title, Artist = t.Artist, Album = t.Album,
                         EffectiveAlbumArtist = t.EffectiveAlbumArtist, TrackNumber = t.TrackNumber, DurationSeconds = t.DurationSeconds, Year = t.Year, Genre = t.Genre,
                     }).ToEnumerable())
            {
                if (Keep(t.Year, t.Genre) && (seen?.Add(t.Id) ?? true))
                {
                    yield return new RadioCandidate(t.Id, t.Path, t.ArtistKey, t.AlbumId, t.Title, t.Artist, t.Album, t.EffectiveAlbumArtist,
                        t.TrackNumber, t.DurationSeconds > 0 ? t.DurationSeconds : null);
                }
            }
        }
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

    /// <summary>The projection read for radio pools.</summary>
    private sealed class RadioRow
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string ArtistKey { get; set; } = string.Empty;
        public string AlbumId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? EffectiveAlbumArtist { get; set; }
        public int? TrackNumber { get; set; }
        public double DurationSeconds { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
    }
}
