using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using static MusicSpin.Tests.Shared.LibraryTestData;

namespace MusicSpin.Tests.Radio;

/// <summary>Radio excludes: any matching exclude removes a track, whatever the includes say; unknown names exclude nothing.</summary>
public sealed class RadioExcludeTests : LibraryTestBase
{
    public RadioExcludeTests()
    {
        CreateSmallLibrary(Music);
        Write(Path.Combine(Music, "Mixed", "01.flac"), Tiny16, "Crossover", "Artist C", "Mixed", null, 1, 1, 1995, "Rock; Pop");
        Write(Path.Combine(Music, "Guest", "01.flac"), Tiny16, "Guest Spot", "Artist D", "Hosted", "Host Band", 1, 1, 1980, "Funk");
        AddAndScan();
    }

    private string[] Titles(RadioFilter filter)
    {
        using var library = OpenRead();
        return [.. library.RadioPool(filter).Select(c => c.Title!).Order(StringComparer.Ordinal)];
    }

    private const int All = 8;

    [Fact]
    public void Exclude_artist_matches_track_or_album_artist_exactly()
    {
        Assert.Equal(["Crossover", "Guest Spot", "Lossy Song", "Song A", "Song B"], Titles(new RadioFilter { ExcludeArtists = ["MILES DAVIS"] }));
        Assert.DoesNotContain("Guest Spot", Titles(new RadioFilter { ExcludeArtists = ["host band"] })); // album artist only
        Assert.Equal(All - 2, Titles(new RadioFilter { ExcludeArtists = ["Artist A"] }).Length);
        Assert.Equal(All, Titles(new RadioFilter { ExcludeArtists = ["Artist"] }).Length); // exact, not contains
    }

    [Fact]
    public void Exclude_album_genre_year_format_and_search()
    {
        Assert.Equal(All - 2, Titles(new RadioFilter { ExcludeAlbums = ["kind of blue"] }).Length);
        Assert.Equal(["Crossover", "Guest Spot", "Lossy Song"], Titles(new RadioFilter { ExcludeGenres = ["JAZZ"] }));
        Assert.Equal(["Guest Spot", "Song A", "Song B"], Titles(new RadioFilter { ExcludeGenres = ["pop", "rock"], ExcludeArtists = ["miles davis"] }));
        Assert.DoesNotContain("Crossover", Titles(new RadioFilter { ExcludeGenres = ["rock"] })); // multi-genre tag split
        Assert.Equal(All - 3, Titles(new RadioFilter { ExcludeYears = [new YearRange(1959, 1959), new YearRange(1995, 1995)] }).Length);
        Assert.Equal(All - 4, Titles(new RadioFilter { ExcludeYears = [new YearRange(1990, 2010)] }).Length);
        Assert.Equal(All - 1, Titles(new RadioFilter { ExcludeFormats = [" MP3 "] }).Length);
        Assert.Equal(["Crossover", "Guest Spot", "Pharaoh's Dance", "Song A", "Song B"], Titles(new RadioFilter { ExcludeSearches = ["album:blue", "lossy"] }));
    }

    [Fact]
    public void Excludes_combine_with_includes_and_win()
    {
        Assert.Equal(["Pharaoh's Dance"], Titles(new RadioFilter { Artists = ["Miles Davis"], ExcludeYears = [new YearRange(1959, 1959)] }));
        Assert.Equal(["Song A", "Song B"], Titles(new RadioFilter { Genres = ["jazz"], ExcludeArtists = ["Miles Davis"] }));
        Assert.Empty(Titles(new RadioFilter { Artists = ["Artist C"], ExcludeArtists = ["Artist C"] }));
        Assert.Equal(["So What"], Titles(new RadioFilter { HiRes = true, ExcludeSearches = ["freddie"] }));
        Assert.Equal(["Freddie Freeloader"], Titles(new RadioFilter { Searches = ["album:blue"], ExcludeSearches = ["\"so what\""] }));
        Assert.Equal(["Lossy Song"], Titles(new RadioFilter { Formats = ["mp3", "flac"], ExcludeFormats = ["flac"] }));
    }

    [Fact]
    public void Unknown_exclude_names_are_ignored_not_errors()
    {
        Assert.Equal(All, Titles(new RadioFilter { ExcludeArtists = ["Nobody"], ExcludeAlbums = ["Nope"], ExcludeGenres = ["polka", "!!!"] }).Length);
        Assert.Equal(All - 3, Titles(new RadioFilter { ExcludeArtists = ["Nobody", "Miles Davis"] }).Length);
        using var library = OpenRead();
        Assert.True(library.HasRadioTracks(new RadioFilter { ExcludeArtists = ["Nobody"] }));
    }

    [Fact]
    public void Excluding_everything_leaves_an_empty_pool_with_a_message_naming_the_excludes()
    {
        var filter = new RadioFilter { ExcludeYears = [new YearRange(0, 9999)] };
        Assert.Empty(Titles(filter));
        using (var library = OpenRead())
        {
            Assert.False(library.HasRadioTracks(filter));
        }

        Assert.Equal("No library tracks are left after the excludes (whole library · excluding year 0-9999).", filter.EmptyPoolMessage());
        Assert.Equal("No library tracks match the radio filters (genre polka).", new RadioFilter { Genres = ["polka"] }.EmptyPoolMessage());
        Assert.StartsWith("The library is empty.", new RadioFilter().EmptyPoolMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Exclude_values_are_validated_like_includes()
    {
        Assert.Contains("backwards", new RadioFilter { ExcludeYears = [new YearRange(2000, 1990)] }.Validate(), StringComparison.Ordinal);
        Assert.Equal("--exclude-artist values must not be empty.", new RadioFilter { ExcludeArtists = [" "] }.Validate());
        Assert.Equal("--exclude-search values must not be empty.", new RadioFilter { ExcludeSearches = [""] }.Validate());
        Assert.Equal("At most 50 --exclude-format values.", new RadioFilter { ExcludeFormats = [.. Enumerable.Repeat("flac", 51)] }.Validate());
        Assert.Equal("Year ranges must not be null.", new RadioFilter { ExcludeYears = [null!] }.Validate());
        using var library = OpenRead();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument,
            Assert.Throws<MusicSpinException>(() => library.RadioPool(new RadioFilter { ExcludeGenres = [""] })).Code);
    }

    [Fact]
    public void Description_lists_includes_then_excludes()
    {
        Assert.Equal("genre jazz · year 1955-1965 · excluding artist Kenny G or Michael Bolton, genre Smooth Jazz",
            new RadioFilter { Genres = ["jazz"], Years = [new YearRange(1955, 1965)], ExcludeArtists = ["Kenny G", "Michael Bolton"], ExcludeGenres = ["Smooth Jazz"] }.Describe());
        Assert.Equal("whole library · excluding album X, year 1990, format mp3, search \"live\"",
            new RadioFilter { ExcludeAlbums = ["X"], ExcludeYears = [new YearRange(1990, 1990)], ExcludeFormats = ["mp3"], ExcludeSearches = ["live"] }.Describe());
        var excludesOnly = new RadioFilter { ExcludeFormats = ["mp3"] };
        Assert.True(excludesOnly.IsEmpty);
        Assert.True(excludesOnly.HasExcludes);
        Assert.False(new RadioFilter { Artists = ["a"] }.HasExcludes);
    }

    [Fact]
    public void The_pool_cache_keys_on_excludes_too()
    {
        var cache = new RadioPoolCache(Data, TimeSpan.FromMilliseconds(200));
        Assert.Equal(All, cache.Get(new RadioFilter()).Count);
        Assert.Equal(All - 1, cache.Get(new RadioFilter { ExcludeFormats = ["mp3"] }).Count);
        Assert.Equal(All - 1, cache.Get(new RadioFilter { ExcludeFormats = ["mp3"] }).Count);
        Assert.Equal(2, cache.Queries);
    }
}
