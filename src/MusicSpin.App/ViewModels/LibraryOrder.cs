using System.Globalization;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>The toolbar's Sort (mockup: #sortBtn): albums, and tracks by their albums.</summary>
public enum LibrarySort
{
    /// <summary>By album artist, then year.</summary>
    Artist,

    /// <summary>By album title; on the Tracks page, by track title when the tracks aren't grouped by album.</summary>
    Title,

    /// <summary>By year (unknown last), then title.</summary>
    Year,
}

/// <summary>The Albums page's Group (mockup: state.group).</summary>
public enum AlbumGrouping
{
    None,
    Artist,
    Genre,
}

/// <summary>The Tracks page's Group (mockup: state.tgroup). The rows leave out the column the group header names.</summary>
public enum TrackGrouping
{
    None,
    Album,
    Genre,
}

/// <summary>How the Albums page shows albums (mockup: #viewSeg).</summary>
public enum AlbumsView
{
    Cards,
    Shelf,
    Crate,
}

/// <summary>A group of a sorted list: its name and its items, in list order.</summary>
public sealed record Group<T>(string Key, IReadOnlyList<T> Items);

/// <summary>Sorting and grouping as the mockup does it ("Library: filter, sort, group").</summary>
public static class LibraryOrder
{
    /// <summary>The group of albums and tracks without a genre tag; it comes last.</summary>
    public const string UnknownGenre = "Unknown genre";

    public static string GenreOf(string? genre) => string.IsNullOrWhiteSpace(genre) ? UnknownGenre : genre.Trim();

    /// <summary>The order albums appear in for <paramref name="sort"/> (mockup: cmpAlbums).</summary>
    public static IComparer<AlbumSummary> Albums(LibrarySort sort) => Comparer<AlbumSummary>.Create((x, y) => sort switch
    {
        LibrarySort.Year => Years(x.Year, y.Year) is var c and not 0 ? c : Text(x.Title, y.Title),
        LibrarySort.Title => Text(x.Title, y.Title),
        _ => Text(x.Artist, y.Artist) is var c and not 0 ? c : Years(x.Year, y.Year),
    });

    /// <summary>Names compare as people read them (mockup: localeCompare), not by code point.</summary>
    public static int Text(string? x, string? y) => CultureInfo.CurrentCulture.CompareInfo.Compare(x, y, CompareOptions.IgnoreCase);

    /// <summary>
    /// Groups <paramref name="items"/> by <paramref name="keyOf"/> (case-insensitively), keeping their order inside each
    /// group. Groups come A–Z with <see cref="UnknownGenre"/> last, or in the order of their first item.
    /// </summary>
    public static IReadOnlyList<Group<T>> GroupBy<T>(IEnumerable<T> items, Func<T, string> keyOf, bool sortKeys = true)
    {
        var groups = new Dictionary<string, List<T>>(StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true));
        var order = new List<string>();
        foreach (var item in items)
        {
            var key = keyOf(item);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }

            list.Add(item);
        }

        IEnumerable<string> keys = sortKeys ? order.OrderBy(k => k == UnknownGenre).ThenBy(k => k, Comparer<string>.Create(Text)) : order;
        return [.. keys.Select(k => new Group<T>(k, groups[k]))];
    }

    private static int Years(int? x, int? y) => (x, y) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        _ => x.Value.CompareTo(y.Value),
    };
}
