using System.Globalization;
using System.Text.Json.Serialization;

namespace MusicSpin.Core.Library;

/// <summary>An inclusive year range (a single year has From == To).</summary>
public sealed record YearRange(int From, int To)
{
    public const int MinYear = 0;
    public const int MaxYear = 9999;

    /// <summary>Parses "1959" or "1990-1999" (inclusive). Null with an error message when invalid.</summary>
    public static YearRange? TryParse(string? text, out string? error)
    {
        error = null;
        var value = text?.Trim() ?? string.Empty;
        var dash = value.IndexOf('-', 1 < value.Length ? 1 : 0);
        var (a, b) = dash > 0 ? (value[..dash], value[(dash + 1)..]) : (value, value);
        if (!int.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var from)
            || !int.TryParse(b.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var to))
        {
            error = $"Invalid year range '{text}'. Use a year (1959) or an inclusive range (1990-1999).";
            return null;
        }

        var range = new YearRange(from, to);
        error = range.Validate();
        return error is null ? range : null;
    }

    public string? Validate()
        => From is < MinYear or > MaxYear || To is < MinYear or > MaxYear ? $"Years must be between {MinYear} and {MaxYear} (got {this})."
            : From > To ? $"Year range {this} is backwards: the first year must not be after the second."
            : null;

    public bool Contains(int? year) => year is { } y && y >= From && y <= To;

    public override string ToString() => From == To ? From.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{From}-{To}");
}

/// <summary>
/// Which library tracks a radio station plays. Every list is an OR of its values; different filters are ANDed. Empty
/// filter: the whole library. Matching follows the listing commands: <see cref="Artists"/> = exact artist (track or album
/// artist; unknown name: NotFound), <see cref="Albums"/> = exact album title or id (unknown: NotFound), <see cref="Genres"/> =
/// exact genre name ignoring case/diacritics (multi-genre tags split on ; / , |), <see cref="Searches"/> = search syntax
/// (as <c>music-spin search</c>), <see cref="Formats"/> = format ids, <see cref="Years"/> = inclusive ranges,
/// <see cref="HiRes"/> = hi-res tracks only, <see cref="Favorites"/> = favorites only.
/// </summary>
public sealed record RadioFilter
{
    public const int MaxValuesPerFilter = 50;

    public IReadOnlyList<string>? Artists { get; init; }
    public IReadOnlyList<string>? Albums { get; init; }
    public IReadOnlyList<string>? Genres { get; init; }
    public IReadOnlyList<YearRange>? Years { get; init; }
    public IReadOnlyList<string>? Formats { get; init; }
    public IReadOnlyList<string>? Searches { get; init; }
    public bool HiRes { get; init; }

    /// <summary>Only favorite tracks (narrows the other filters, like <see cref="HiRes"/>).</summary>
    public bool Favorites { get; init; }

    /// <summary>
    /// Not a filter but a weight (#49): favorites are picked <see cref="Playback.RadioPicker.FavoriteWeight"/>× as often as
    /// other tracks, without limiting the station to them. Part of the station, so it is saved with it and shown in its
    /// description. No effect together with <see cref="Favorites"/> (every track is a favorite then).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreferFavorites { get; init; }

    // Excludes: a track matching ANY exclude value is left out, whatever the include filters say (exclude wins). Same
    // matching rules as the includes, except that unknown artist/album names are ignored (excluding something absent is harmless).
    public IReadOnlyList<string>? ExcludeArtists { get; init; }
    public IReadOnlyList<string>? ExcludeAlbums { get; init; }
    public IReadOnlyList<string>? ExcludeGenres { get; init; }
    public IReadOnlyList<YearRange>? ExcludeYears { get; init; }
    public IReadOnlyList<string>? ExcludeFormats { get; init; }
    public IReadOnlyList<string>? ExcludeSearches { get; init; }

    /// <summary>No include filter (the excludes may still narrow the whole library).</summary>
    [JsonIgnore]
    public bool IsEmpty => Count(Artists) + Count(Albums) + Count(Genres) + Count(Years) + Count(Formats) + Count(Searches) == 0 && !HiRes && !Favorites;

    [JsonIgnore]
    public bool HasExcludes => Count(ExcludeArtists) + Count(ExcludeAlbums) + Count(ExcludeGenres) + Count(ExcludeYears) + Count(ExcludeFormats) + Count(ExcludeSearches) > 0;

    /// <summary>Null when valid, else the problem (InvalidArgument / usage).</summary>
    public string? Validate()
    {
        foreach (var (name, list) in new (string, IReadOnlyList<string>?)[] { ("artist", Artists), ("album", Albums), ("genre", Genres), ("format", Formats), ("search", Searches),
                     ("exclude-artist", ExcludeArtists), ("exclude-album", ExcludeAlbums), ("exclude-genre", ExcludeGenres), ("exclude-format", ExcludeFormats), ("exclude-search", ExcludeSearches) })
        {
            if (list is null)
            {
                continue;
            }

            if (list.Count > MaxValuesPerFilter)
            {
                return $"At most {MaxValuesPerFilter} --{name} values.";
            }

            if (list.Any(string.IsNullOrWhiteSpace))
            {
                return $"--{name} values must not be empty.";
            }
        }

        foreach (var years in new[] { Years, ExcludeYears })
        {
            if (years is null)
            {
                continue;
            }

            if (years.Count > MaxValuesPerFilter)
            {
                return $"At most {MaxValuesPerFilter} year ranges.";
            }

            foreach (var range in years)
            {
                if (range is null)
                {
                    return "Year ranges must not be null.";
                }

                if (range.Validate() is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }

    /// <summary>A favorites station with nothing to play (see <see cref="MusicLibrary.RadioEmptyPoolMessage"/> for the precise reason).</summary>
    public const string NoFavoritesMessage = "There are no favorites in the library to play. Mark tracks with 'music-spin favorite add' (or the heart in the app).";

    /// <summary>Why a station has nothing to play (empty library, no favorites, nothing matches, or the excludes removed everything).</summary>
    public string EmptyPoolMessage()
        => IsEmpty && !HasExcludes ? "The library is empty. Add music with 'music-spin library add <folder>' and 'music-spin library scan'."
            : Favorites && !HasExcludes && this with { Favorites = false } is { IsEmpty: true } ? NoFavoritesMessage
            : HasExcludes ? $"No library tracks are left after the excludes ({Describe()})."
            : $"No library tracks match the radio filters ({Describe()}).";

    public void EnsureValid()
    {
        if (Validate() is { } error)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error);
        }
    }

    /// <summary>
    /// Human description, e.g. "genre Jazz · year 1955-1965 · excluding artist Kenny G"; "whole library" when there are no
    /// include filters ("whole library · excluding genre Christmas" with excludes only).
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();
        void Add(string name, IEnumerable<string>? values, List<string>? into = null)
        {
            if (values?.ToList() is { Count: > 0 } list)
            {
                (into ?? parts).Add($"{name} {string.Join(" or ", list)}");
            }
        }

        Add("artist", Artists);
        Add("album", Albums);
        Add("genre", Genres);
        Add("year", Years?.Select(y => y.ToString()));
        Add("format", Formats);
        Add("search", Searches?.Select(s => $"\"{s}\""));
        if (HiRes)
        {
            parts.Add("hi-res");
        }

        if (Favorites)
        {
            parts.Add("favorites");
        }

        var excluded = new List<string>();
        Add("artist", ExcludeArtists, excluded);
        Add("album", ExcludeAlbums, excluded);
        Add("genre", ExcludeGenres, excluded);
        Add("year", ExcludeYears?.Select(y => y.ToString()), excluded);
        Add("format", ExcludeFormats, excluded);
        Add("search", ExcludeSearches?.Select(s => $"\"{s}\""), excluded);
        var text = parts.Count == 0 ? "whole library" : string.Join(" · ", parts);
        text = excluded.Count == 0 ? text : $"{text} · excluding {string.Join(", ", excluded)}";
        return PreferFavorites && !Favorites ? $"{text} · favorites first" : text;
    }

    private static int Count<T>(IReadOnlyList<T>? list) => list?.Count ?? 0;
}

/// <summary>
/// One value a station can be built from (<see cref="MusicLibrary.RadioTags"/>): <paramref name="Value"/> goes in the
/// filter (a genre, a year range "1960-1969", a format id, an artist name, an album id), <paramref name="Name"/> is shown
/// ("1960s", an album's title), and <paramref name="Tracks"/> is how many tracks a station of that value alone plays.
/// <paramref name="Detail"/> tells apart values that share a name (an album's artist).
/// </summary>
public sealed record RadioTagCount(string Value, string Name, int Tracks, string? Detail = null);

/// <summary>
/// The library's radio values with their track counts: genres, decades, formats and albums by name, artists by track
/// count (most first), and how many tracks are hi-res.
/// </summary>
public sealed record RadioTagCounts(
    IReadOnlyList<RadioTagCount> Genres,
    IReadOnlyList<RadioTagCount> Decades,
    IReadOnlyList<RadioTagCount> Formats,
    int HiRes,
    IReadOnlyList<RadioTagCount> Artists,
    IReadOnlyList<RadioTagCount> Albums);

/// <summary>One track a station can play: what radio needs to pick (ids, artist/album for spacing) and to queue it.</summary>
public sealed record RadioCandidate(
    string TrackId,
    string Path,
    string ArtistKey,
    string AlbumId,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumArtist = null,
    int? TrackNumber = null,
    double? DurationSeconds = null,
    bool IsFavorite = false);
