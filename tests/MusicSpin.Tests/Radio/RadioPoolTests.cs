using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using static MusicSpin.Tests.Shared.LibraryTestData;

namespace MusicSpin.Tests.Radio;

/// <summary>Which library tracks a radio station may play: each filter, OR within a filter, AND across filters.</summary>
public sealed class RadioPoolTests : LibraryTestBase
{
    public RadioPoolTests()
    {
        CreateSmallLibrary(Music);
        Write(Path.Combine(Music, "Mixed", "01.flac"), Tiny16, "Crossover", "Artist C", "Mixed", null, 1, 1, 1995, "Rock; Pop");
        AddAndScan();
    }

    private string[] Titles(RadioFilter filter)
    {
        using var library = OpenRead();
        return [.. library.RadioPool(filter).Select(c => c.Title!).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void No_filter_is_the_whole_library_with_what_radio_needs_per_track()
    {
        var track = AllTracks().Single(t => t.Title == "So What");
        using var library = OpenRead();
        var pool = library.RadioPool(new RadioFilter());

        Assert.Equal(7, pool.Count);
        var candidate = pool.Single(c => c.TrackId == track.Id);
        Assert.Equal(new RadioCandidate(track.Id, track.Path, track.ArtistKey, track.AlbumId, "So What", "Miles Davis", "Kind of Blue", "Miles Davis", 1,
            track.DurationSeconds), candidate);
    }

    [Fact]
    public void Artist_is_exact_and_repeatable()
    {
        Assert.Equal(["Freddie Freeloader", "Pharaoh's Dance", "So What"], Titles(new RadioFilter { Artists = ["MILES DAVIS"] }));
        Assert.Equal(["Freddie Freeloader", "Pharaoh's Dance", "So What", "Song B"], Titles(new RadioFilter { Artists = ["Miles Davis", "Artist B"] }));
        Assert.Equal(["Lossy Song", "Song A"], Titles(new RadioFilter { Artists = ["Artist A"] }));
    }

    [Fact]
    public void Unknown_artists_and_albums_are_not_found_like_the_listings()
    {
        using var library = OpenRead();
        var artist = Assert.Throws<MusicSpinException>(() => library.RadioPool(new RadioFilter { Artists = ["Miles Davis", "Nobody"] }));
        Assert.Equal(MusicSpinErrorCode.NotFound, artist.Code);
        Assert.Contains("No artist named 'Nobody'", artist.Message, StringComparison.Ordinal);
        var album = Assert.Throws<MusicSpinException>(() => library.RadioPool(new RadioFilter { Albums = ["Nope"] }));
        Assert.Equal(MusicSpinErrorCode.NotFound, album.Code);
    }

    [Fact]
    public void Genre_is_exact_ignoring_case_and_multi_genre_tags_are_split()
    {
        Assert.Equal(5, Titles(new RadioFilter { Genres = ["jazz"] }).Length);
        Assert.Equal(["Crossover", "Lossy Song"], Titles(new RadioFilter { Genres = ["POP"] }));
        Assert.Equal(["Crossover"], Titles(new RadioFilter { Genres = ["rock; pop"] }));
        Assert.Equal(6, Titles(new RadioFilter { Genres = ["Rock", "Jazz"] }).Length);
        Assert.Empty(Titles(new RadioFilter { Genres = ["Jaz"] })); // exact, not contains
        Assert.Empty(Titles(new RadioFilter { Genres = ["!!!"] })); // folds to nothing
    }

    [Fact]
    public void Years_and_ranges_are_inclusive_and_ored()
    {
        Assert.Equal(["Freddie Freeloader", "So What"], Titles(new RadioFilter { Years = [new YearRange(1959, 1959)] }));
        Assert.Equal(["Freddie Freeloader", "Lossy Song", "So What", "Song A", "Song B"],
            Titles(new RadioFilter { Years = [new YearRange(1959, 1959), new YearRange(2001, 2010)] }));
        Assert.Empty(Titles(new RadioFilter { Years = [new YearRange(1960, 1969)] }));
    }

    [Fact]
    public void Album_hires_format_and_search()
    {
        Assert.Equal(["Freddie Freeloader", "Lossy Song", "So What"], Titles(new RadioFilter { Albums = ["kind of blue", "Singles"] }));
        Assert.Equal(["Freddie Freeloader", "So What"], Titles(new RadioFilter { HiRes = true }));
        Assert.Equal(["Lossy Song"], Titles(new RadioFilter { Formats = [" MP3 "] }));
        Assert.Equal(["Freddie Freeloader", "Lossy Song"], Titles(new RadioFilter { Searches = ["freddie", "lossy"] }));
        Assert.Equal(["Freddie Freeloader", "So What"], Titles(new RadioFilter { Searches = ["album:blue"] }));
        Assert.Equal(["Pharaoh's Dance"], Titles(new RadioFilter { Searches = ["year:1970"] }));
    }

    [Fact]
    public void Different_filters_are_anded()
    {
        Assert.Equal(["Pharaoh's Dance"], Titles(new RadioFilter { Artists = ["Miles Davis"], Years = [new YearRange(1960, 1979)] }));
        Assert.Equal(["Freddie Freeloader", "So What"], Titles(new RadioFilter { Genres = ["Jazz"], HiRes = true }));
        Assert.Equal(["Crossover"], Titles(new RadioFilter { Genres = ["Pop"], Formats = ["flac"] }));
        Assert.Empty(Titles(new RadioFilter { Artists = ["Artist B"], Genres = ["Pop"] }));
        Assert.Equal(["Song A"], Titles(new RadioFilter { Artists = ["Artist A", "Artist B"], Searches = ["\"Song A\""], Albums = ["Jazz Hits"] }));
    }

    [Fact]
    public void Invalid_filters_are_rejected_before_reading_the_library()
    {
        using var library = OpenRead();
        var ex = Assert.Throws<MusicSpinException>(() => library.RadioPool(new RadioFilter { Years = [new YearRange(2000, 1990)] }));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
        Assert.Throws<ArgumentNullException>(() => library.RadioPool(null!));
    }

    [Fact]
    public void The_pool_cache_rereads_only_when_the_library_or_filter_changes()
    {
        var cache = new RadioPoolCache(Data, TimeSpan.FromMilliseconds(500));
        var all = new RadioFilter();

        Assert.Equal(7, cache.Get(all).Count);
        Assert.Same(cache.Get(all), cache.Get(all));
        Assert.Equal(1, cache.Queries);

        Assert.Equal(2, cache.Get(new RadioFilter { HiRes = true }).Count);
        Assert.Equal(2, cache.Queries);

        Write(Path.Combine(Music, "New", "01.flac"), Tiny16, "Brand New", "Artist D", "New", null, 1, 1, 2020, "Pop");
        Rescan();
        Assert.Equal(8, cache.Get(all).Count);
        Assert.Equal(3, cache.Queries);
    }

    [Fact]
    public void The_pool_cache_without_a_library_is_empty()
    {
        var empty = new MusicSpin.Core.Hosting.DataDirectory(Path.Combine(Root, "no-library"), MusicSpin.Core.Hosting.DataDirectorySource.Override);
        Assert.Empty(new RadioPoolCache(empty).Get(new RadioFilter()));
        Assert.Throws<ArgumentNullException>(() => new RadioPoolCache(empty).Get(null!));
    }
}
