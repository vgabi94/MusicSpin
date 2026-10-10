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
/// <see cref="HiRes"/> = hi-res tracks only.
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

    [JsonIgnore]
    public bool IsEmpty => Count(Artists) + Count(Albums) + Count(Genres) + Count(Years) + Count(Formats) + Count(Searches) == 0 && !HiRes;

    /// <summary>Null when valid, else the problem (InvalidArgument / usage).</summary>
    public string? Validate()
    {
        foreach (var (name, list) in new (string, IReadOnlyList<string>?)[] { ("artist", Artists), ("album", Albums), ("genre", Genres), ("format", Formats), ("search", Searches) })
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

        if (Years is { } years)
        {
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

    public void EnsureValid()
    {
        if (Validate() is { } error)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, error);
        }
    }

    /// <summary>Human description, e.g. "artist Miles Davis or Coltrane · years 1955-1965 · hi-res"; "whole library" when empty.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        void Add(string name, IEnumerable<string>? values)
        {
            if (values?.ToList() is { Count: > 0 } list)
            {
                parts.Add($"{name} {string.Join(" or ", list)}");
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

        return parts.Count == 0 ? "whole library" : string.Join(" · ", parts);
    }

    private static int Count<T>(IReadOnlyList<T>? list) => list?.Count ?? 0;
}

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
    double? DurationSeconds = null);
