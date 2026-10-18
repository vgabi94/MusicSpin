using System.Globalization;
using MusicSpin.Core.Library;

namespace MusicSpin.App.Services;

/// <summary>
/// What a radio tag selects: one per CLI radio filter (<c>music-spin radio</c>), in the order a station's description
/// reads them (#23). <see cref="Favorites"/> has no row on the Radio page; it comes from the Favorites view's Radio.
/// </summary>
public enum RadioTagKind
{
    /// <summary><c>--genre</c>.</summary>
    Genre,

    /// <summary><c>--years A-B</c>: on the Radio page, one decade per tag.</summary>
    Years,

    /// <summary><c>--format</c>: a format id ("flac").</summary>
    Format,

    /// <summary><c>--hires</c>: only included (the CLI can't leave hi-res out).</summary>
    HiRes,

    /// <summary><c>--artist</c>: the track or album artist's name.</summary>
    Artist,

    /// <summary><c>--album</c>: an album id (a title from the CLI).</summary>
    Album,

    /// <summary><c>--search</c>: words in the search syntax.</summary>
    Words,

    /// <summary><c>--favorites</c>: only included.</summary>
    Favorites,
}

/// <summary>
/// One tag of a station: a value of one kind, included, or left out (<see cref="Excluded"/>, the CLI's
/// <c>--exclude-*</c>). <see cref="RadioTagKind.HiRes"/> and <see cref="RadioTagKind.Favorites"/> have no value.
/// </summary>
public sealed record RadioTag(RadioTagKind Kind, string Value = "", bool Excluded = false)
{
    /// <summary>The tag of the decade <paramref name="decade"/> (1960: the 1960s).</summary>
    public static RadioTag Decade(int decade) => new(RadioTagKind.Years, new YearRange(decade, Math.Min(decade + 9, YearRange.MaxYear)).ToString());

    /// <summary>The same tag left out, or included again.</summary>
    public RadioTag WithExcluded(bool excluded) => this with { Excluded = excluded };
}

/// <summary>A piece of a station's description: a tag's name (shown strong on the Radio page) or the words between.</summary>
public sealed record DescriptionPart(string Text, bool IsValue);

/// <summary>
/// Radio stations as the app shows them: a station is a set of tags, values of one kind are ORed, kinds are ANDed, and a
/// left-out value always wins (as in the CLI). This converts tags to and from the <see cref="RadioFilter"/> the player
/// takes, tells two stations apart, and writes the description ("Jazz or Ambient · 1960s · not Kodo Arai").
/// </summary>
public static class RadioStation
{
    /// <summary>"Whole library": a station with nothing included.</summary>
    public const string WholeLibrary = "Whole library";

    /// <summary>The tags of <paramref name="filter"/>: kind by kind, included before left out (none for null).</summary>
    public static IReadOnlyList<RadioTag> TagsOf(RadioFilter? filter)
    {
        if (filter is null)
        {
            return [];
        }

        var tags = new List<RadioTag>();
        void Add(RadioTagKind kind, IEnumerable<string>? values, bool excluded = false)
            => tags.AddRange((values ?? []).Select(v => new RadioTag(kind, v.Trim(), excluded)));

        Add(RadioTagKind.Genre, filter.Genres);
        Add(RadioTagKind.Genre, filter.ExcludeGenres, excluded: true);
        Add(RadioTagKind.Years, filter.Years?.Select(y => y.ToString()));
        Add(RadioTagKind.Years, filter.ExcludeYears?.Select(y => y.ToString()), excluded: true);
        Add(RadioTagKind.Format, filter.Formats?.Select(f => f.ToLowerInvariant()));
        Add(RadioTagKind.Format, filter.ExcludeFormats?.Select(f => f.ToLowerInvariant()), excluded: true);
        if (filter.HiRes)
        {
            tags.Add(new RadioTag(RadioTagKind.HiRes));
        }

        Add(RadioTagKind.Artist, filter.Artists);
        Add(RadioTagKind.Artist, filter.ExcludeArtists, excluded: true);
        Add(RadioTagKind.Album, filter.Albums);
        Add(RadioTagKind.Album, filter.ExcludeAlbums, excluded: true);
        Add(RadioTagKind.Words, filter.Searches);
        Add(RadioTagKind.Words, filter.ExcludeSearches, excluded: true);
        if (filter.Favorites)
        {
            tags.Add(new RadioTag(RadioTagKind.Favorites));
        }

        return Distinct(tags);
    }

    /// <summary>
    /// The filter for <paramref name="tags"/>, as <c>music-spin radio</c> builds it from the same options: a list per
    /// filter, null when it has no value. Hi-Res and Favorites left out mean nothing (the CLI has no such option).
    /// </summary>
    public static RadioFilter FilterOf(IEnumerable<RadioTag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var all = Distinct(tags);
        IReadOnlyList<string>? Values(RadioTagKind kind, bool excluded)
            => all.Where(t => t.Kind == kind && t.Excluded == excluded).Select(t => t.Value).ToList() is { Count: > 0 } list ? list : null;
        IReadOnlyList<YearRange>? Years(bool excluded)
            => Values(RadioTagKind.Years, excluded)?.Select(v => YearRange.TryParse(v, out _)).OfType<YearRange>().ToList() is { Count: > 0 } list ? list : null;

        return new RadioFilter
        {
            Artists = Values(RadioTagKind.Artist, false),
            Albums = Values(RadioTagKind.Album, false),
            Genres = Values(RadioTagKind.Genre, false),
            Years = Years(false),
            Formats = Values(RadioTagKind.Format, false),
            Searches = Values(RadioTagKind.Words, false),
            HiRes = all.Any(t => t is { Kind: RadioTagKind.HiRes, Excluded: false }),
            Favorites = all.Any(t => t is { Kind: RadioTagKind.Favorites, Excluded: false }),
            ExcludeArtists = Values(RadioTagKind.Artist, true),
            ExcludeAlbums = Values(RadioTagKind.Album, true),
            ExcludeGenres = Values(RadioTagKind.Genre, true),
            ExcludeYears = Years(true),
            ExcludeFormats = Values(RadioTagKind.Format, true),
            ExcludeSearches = Values(RadioTagKind.Words, true),
        };
    }

    /// <summary>
    /// The same station: the same tags, in any order, matched as the player matches them (names ignoring case and
    /// diacritics, so <c>--genre jazz</c> from the CLI is the app's Jazz).
    /// </summary>
    public static bool Same(IEnumerable<RadioTag> a, IEnumerable<RadioTag> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Distinct(a).Select(Key).ToHashSet(StringComparer.Ordinal).SetEquals(Distinct(b).Select(Key));
    }

    /// <summary>Whether <paramref name="tags"/> holds <paramref name="tag"/>'s value (included or left out); null when not.</summary>
    public static RadioTag? Find(IEnumerable<RadioTag> tags, RadioTag tag)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(tag);
        var key = Key(tag with { Excluded = false });
        return tags.FirstOrDefault(t => Key(t with { Excluded = false }) == key);
    }

    /// <summary>
    /// A tag's name: genres and artists as they are, decades as "1960s" (other years "1959", "1990–1995"), formats in
    /// capitals ("FLAC", but "Opus"), "Hi-Res", an album by its title (<paramref name="albumTitle"/>; the value itself when
    /// it isn't known), words in quotes, "Favorites".
    /// </summary>
    public static string Label(RadioTag tag, Func<string, string?>? albumTitle = null)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.Kind switch
        {
            RadioTagKind.Years => YearsLabel(tag.Value),
            RadioTagKind.Format => tag.Value.Equals("opus", StringComparison.OrdinalIgnoreCase) ? "Opus" : tag.Value.ToUpperInvariant(),
            RadioTagKind.HiRes => "Hi-Res",
            RadioTagKind.Album => albumTitle?.Invoke(tag.Value) ?? tag.Value,
            RadioTagKind.Words => $"“{tag.Value}”",
            RadioTagKind.Favorites => "Favorites",
            _ => tag.Value,
        };
    }

    /// <summary>The description as text: "Jazz or Ambient · 1960s · not Kodo Arai", or "Whole library".</summary>
    public static string Describe(IEnumerable<RadioTag> tags, Func<string, string?>? albumTitle = null)
        => string.Concat(DescribeParts(tags, albumTitle).Select(p => p.Text));

    /// <summary>
    /// The description in pieces: included values of one kind joined with "or", the kinds joined with " · " in
    /// <see cref="RadioTagKind"/> order ("Whole library" with none), then the left-out values after "not".
    /// </summary>
    public static IReadOnlyList<DescriptionPart> DescribeParts(IEnumerable<RadioTag> tags, Func<string, string?>? albumTitle = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var all = Distinct(tags).Where(t => !t.Excluded || t.Kind is not (RadioTagKind.HiRes or RadioTagKind.Favorites)).ToList();
        var parts = new List<DescriptionPart>();
        void Between(string text)
        {
            if (parts.Count > 0)
            {
                parts.Add(new DescriptionPart(text, false));
            }
        }

        foreach (var kind in Enum.GetValues<RadioTagKind>())
        {
            var values = all.Where(t => t.Kind == kind && !t.Excluded).ToList();
            for (var i = 0; i < values.Count; i++)
            {
                Between(i == 0 ? " · " : " or ");
                parts.Add(new DescriptionPart(Label(values[i], albumTitle), true));
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new DescriptionPart(WholeLibrary, false));
        }

        var excluded = all.Where(t => t.Excluded).OrderBy(t => t.Kind).ToList();
        for (var i = 0; i < excluded.Count; i++)
        {
            parts.Add(new DescriptionPart(i == 0 ? " · not " : ", ", false));
            parts.Add(new DescriptionPart(Label(excluded[i], albumTitle), true));
        }

        return parts;
    }

    private static string YearsLabel(string value)
        => YearRange.TryParse(value, out _) is not { } range ? value
            : range.From % 10 == 0 && range.To == range.From + 9 ? string.Create(CultureInfo.InvariantCulture, $"{range.From}s")
            : range.From == range.To ? range.From.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{range.From}–{range.To}");

    /// <summary>The tags without repeats (the first of each kept), in their order.</summary>
    private static List<RadioTag> Distinct(IEnumerable<RadioTag> tags)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. tags.Where(t => seen.Add(Key(t)))];
    }

    /// <summary>A tag as the player matches it.</summary>
    private static string Key(RadioTag tag)
    {
        var value = tag.Kind switch
        {
            RadioTagKind.HiRes or RadioTagKind.Favorites => string.Empty,
            RadioTagKind.Format => tag.Value.Trim().ToLowerInvariant(),
            RadioTagKind.Years => YearRange.TryParse(tag.Value, out _)?.ToString() ?? tag.Value.Trim(),
            _ => LibrarySearch.Fold(tag.Value),
        };
        return string.Create(CultureInfo.InvariantCulture, $"{(int)tag.Kind}|{(tag.Excluded ? 1 : 0)}|{value}");
    }
}
