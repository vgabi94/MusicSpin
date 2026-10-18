using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using static MusicSpin.Tests.Shared.LibraryTestData;

namespace MusicSpin.Tests.Radio;

/// <summary>The values the app's Radio page offers as tags, and each one's track count (a station of that value alone).</summary>
public sealed class RadioTagsTests : LibraryTestBase
{
    private void Scan(bool extra = true)
    {
        CreateSmallLibrary(Music);
        if (extra)
        {
            Write(Path.Combine(Music, "Mixed", "01.flac"), Tiny16, "Crossover", "Artist C", "Mixed", null, 1, 1, 1995, "Rock; Pop");
            Write(Path.Combine(Music, "Mixed", "02.flac"), Tiny16, "Undated", "Artist C", "Mixed", null, 2, 1, 0, "!!!");
            Write(Path.Combine(Music, "Mixed", "03.flac"), Tiny16, "Loud", "Artist C", "Mixed", null, 3, 1, 1999, "ROCK");
            Write(Path.Combine(Music, "Mixed", "04.flac"), Tiny16, "Louder", "Artist C", "Mixed", null, 4, 1, 1999, "Rock");
        }

        AddAndScan();
    }

    private RadioTagCounts Tags()
    {
        using var library = OpenRead();
        return library.RadioTags();
    }

    private int Pool(RadioFilter filter)
    {
        using var library = OpenRead();
        return library.RadioPool(filter).Count;
    }

    [Fact]
    public void Each_count_is_what_a_station_of_that_value_alone_plays()
    {
        Scan();
        var tags = Tags();

        Assert.All(tags.Genres, g => Assert.Equal(Pool(new RadioFilter { Genres = [g.Value] }), g.Tracks));
        Assert.All(tags.Decades, d => Assert.Equal(Pool(new RadioFilter { Years = [YearRange.TryParse(d.Value, out _)!] }), d.Tracks));
        Assert.All(tags.Formats, f => Assert.Equal(Pool(new RadioFilter { Formats = [f.Value] }), f.Tracks));
        Assert.All(tags.Artists, a => Assert.Equal(Pool(new RadioFilter { Artists = [a.Value] }), a.Tracks));
        Assert.All(tags.Albums, a => Assert.Equal(Pool(new RadioFilter { Albums = [a.Value] }), a.Tracks));
        Assert.Equal(Pool(new RadioFilter { HiRes = true }), tags.HiRes);
        Assert.NotEmpty(tags.Genres);
        Assert.NotEmpty(tags.Artists);
    }

    [Fact]
    public void Genres_are_split_like_the_genre_filter_and_merged_ignoring_case()
    {
        Scan();
        var genres = Tags().Genres;

        // "Rock; Pop" counts for Rock and for Pop, "ROCK" is the same genre as "Rock" (named as most of its tracks spell it),
        // and "!!!" folds to nothing.
        Assert.Equal(["Jazz", "Pop", "Rock"], genres.Select(g => g.Name));
        Assert.Equal([5, 2, 3], genres.Select(g => g.Tracks));
    }

    [Fact]
    public void Decades_run_from_a_round_year_and_tracks_without_a_year_count_in_none()
    {
        Scan();
        var decades = Tags().Decades;

        Assert.Equal(["1950s", "1970s", "1990s", "2000s", "2010s"], decades.Select(d => d.Name));
        Assert.Equal(["1950-1959", "1970-1979", "1990-1999", "2000-2009", "2010-2019"], decades.Select(d => d.Value));
        Assert.Equal(9, decades.Sum(d => d.Tracks)); // ten tracks, one without a year
    }

    [Fact]
    public void Artists_come_most_tracks_first_and_albums_by_title_with_their_ids()
    {
        Scan();
        var tags = Tags();

        // Most tracks first: Artist C has four, Miles Davis three ("miles davis" is the same artist).
        Assert.Equal([("Artist C", 4), ("Miles Davis", 3)], tags.Artists.Take(2).Select(a => (a.Name, a.Tracks)));
        Assert.Equal(tags.Artists.OrderByDescending(a => a.Tracks).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase), tags.Artists);
        var kindOfBlue = tags.Albums.Single(a => a.Name == "Kind of Blue");
        Assert.Equal(AllTracks().First(t => t.Album == "Kind of Blue").AlbumId, kindOfBlue.Value);
        Assert.Equal(2, kindOfBlue.Tracks);
        Assert.Equal("Miles Davis", kindOfBlue.Detail); // two albums of one name are told apart by their artist
        Assert.Equal(tags.Albums.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase), tags.Albums.Select(a => a.Name));
    }

    [Fact]
    public void Formats_and_hi_res()
    {
        Scan(extra: false);
        var tags = Tags();

        Assert.Equal([("flac", 5), ("mp3", 1)], tags.Formats.Select(f => (f.Value, f.Tracks)));
        Assert.Equal(2, tags.HiRes);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_empty_library_has_no_tags()
    {
        AddAndScan();
        var tags = Tags();

        Assert.Empty(tags.Genres);
        Assert.Empty(tags.Decades);
        Assert.Empty(tags.Formats);
        Assert.Empty(tags.Artists);
        Assert.Empty(tags.Albums);
        Assert.Equal(0, tags.HiRes);
    }
}
