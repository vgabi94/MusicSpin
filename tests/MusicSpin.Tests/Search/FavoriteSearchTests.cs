using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Search;

/// <summary><c>is:favorite</c> (<c>is:fav</c>) in the search syntax (#50): parsing, tracks, search, play and radio searches.</summary>
public sealed class FavoriteSearchTests : LibraryTestBase
{
    private string SoWhat => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");
    private string Pharaoh => Path.Combine(Music, "Miles", "Bitches Brew", "01 Pharaoh's Dance.flac");
    private string Lossy => Path.Combine(Music, "Lossy", "track.mp3");

    public FavoriteSearchTests()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
    }

    private void Favorite(params string[] paths)
    {
        var ids = paths.Select(p => Assert.Single(AllTracks(), t => t.Path == Path.GetFullPath(p)).Id).ToList();
        using var library = Open();
        library.AddFavorites(ids);
    }

    private string?[] Titles(TrackQuery query)
    {
        using var library = OpenRead();
        return [.. library.Tracks(query).Items.Select(t => t.Title).Order(StringComparer.Ordinal)];
    }

    // ---- parsing -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("is:favorite")]
    [InlineData("is:fav")]
    [InlineData("IS:Favorite")]
    [InlineData("is:\"favorite\"")]
    public void Is_favorite_and_its_alias_parse_on_their_own(string text)
    {
        var query = LibrarySearch.Parse(text);

        Assert.True(query.Favorite);
        Assert.Empty(query.Terms);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void Is_favorite_is_ANDed_with_the_other_terms_and_survives_combining()
    {
        var query = LibrarySearch.Parse("miles is:fav year:1959");

        Assert.True(query.Favorite);
        Assert.Equal(new SearchTerm(SearchField.Any, "miles", false), Assert.Single(query.Terms));
        Assert.Equal((1959, 1959), (query.YearFrom, query.YearTo));
        Assert.True(LibrarySearch.Parse("blue").And(query).Favorite);
        Assert.True(query.And(LibrarySearch.Parse("blue")).Favorite);
        Assert.False(LibrarySearch.Parse("blue").And(LibrarySearch.Parse("green")).Favorite);
        Assert.False(LibrarySearch.Parse("blue").Favorite);
    }

    [Theory]
    [InlineData("is:liked", "Unknown 'is:liked': use is:favorite (or is:fav).")]
    [InlineData("miles is:hires", "Unknown 'is:hires': use is:favorite (or is:fav).")]
    [InlineData("is:", "'is:' needs a value: is:favorite (or is:fav).")]
    [InlineData("is: favorite", "'is:' needs a value: is:favorite (or is:fav).")]
    public void Unknown_or_missing_is_values_are_usage_errors_that_list_the_valid_ones(string text, string message)
    {
        var ex = Assert.Throws<MusicSpinException>(() => LibrarySearch.Parse(text));

        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void A_word_that_merely_contains_is_is_plain_text()
        => Assert.Equal(new SearchTerm(SearchField.Any, "this", false), Assert.Single(LibrarySearch.Parse("this:").Terms));

    // ---- in the library ------------------------------------------------------------------------------------

    [Fact]
    public void Tracks_with_is_favorite_are_the_favorites_matching_the_other_terms()
    {
        Favorite(SoWhat, Pharaoh, Lossy);

        Assert.Equal(["Lossy Song", "Pharaoh's Dance", "So What"], Titles(new TrackQuery { Text = "is:favorite" }));
        Assert.Equal(["Pharaoh's Dance", "So What"], Titles(new TrackQuery { Text = "miles is:fav" }));
        Assert.Equal(["So What"], Titles(new TrackQuery { Text = "is:fav year:1959" }));
        Assert.Empty(Titles(new TrackQuery { Text = "freddie is:favorite" })); // Freddie Freeloader isn't one
        Assert.Equal(["Pharaoh's Dance", "So What"], Titles(new TrackQuery { Text = "is:favorite", Favorite = true, Artist = "Miles Davis" }));
    }

    [Fact]
    public void Is_favorite_with_favorites_false_matches_nothing()
    {
        Favorite(SoWhat);

        using var library = OpenRead();
        var page = library.Tracks(new TrackQuery { Text = "is:favorite", Favorite = false });

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Without_favorites_is_favorite_matches_nothing()
    {
        using var library = OpenRead();

        Assert.Equal(0, library.Tracks(new TrackQuery { Text = "is:favorite" }).Total);
        Assert.Equal(0, library.Search("miles is:favorite").Tracks.Total);
    }

    [Fact]
    public void Search_with_is_favorite_has_no_artists_or_albums_only_favorite_tracks()
    {
        Favorite(SoWhat, Pharaoh);
        using var library = OpenRead();

        var plain = library.Search("miles");
        var favorites = library.Search("miles is:favorite");

        Assert.NotEmpty(plain.Artists);
        Assert.NotEmpty(plain.Albums);
        Assert.Empty(favorites.Artists);
        Assert.Empty(favorites.Albums);
        Assert.Equal(["Pharaoh's Dance", "So What"], favorites.Tracks.Items.Select(t => t.Title).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_favorite_found_by_its_path_after_the_track_came_back_still_counts()
    {
        Favorite(SoWhat);
        using (var library = Open())
        {
            library.RemoveFolder(Music);
        }

        Assert.Empty(Titles(new TrackQuery { Text = "is:favorite" }));
        AddAndScan(); // back under a new id, the same path

        Assert.Equal(["So What"], Titles(new TrackQuery { Text = "is:favorite" }));
    }

    [Fact]
    public void Radio_searches_and_exclude_searches_take_is_favorite()
    {
        Favorite(SoWhat, Lossy);
        using var library = OpenRead();

        static string?[] Pool(MusicLibrary library, RadioFilter filter)
            => [.. library.RadioPool(filter).Select(c => c.Title).Order(StringComparer.Ordinal)];

        Assert.Equal(["Lossy Song", "So What"], Pool(library, new RadioFilter { Searches = ["is:favorite"] }));
        Assert.Equal(["So What"], Pool(library, new RadioFilter { Searches = ["is:fav jazz"] }));
        Assert.DoesNotContain("So What", Pool(library, new RadioFilter { ExcludeSearches = ["is:favorite"] }));
        Assert.DoesNotContain("Lossy Song", Pool(library, new RadioFilter { ExcludeSearches = ["is:favorite"] }));
        Assert.Equal(4, Pool(library, new RadioFilter { ExcludeSearches = ["is:favorite"] }).Length); // 6 tracks, 2 favorites
        Assert.Equal(["Pharaoh's Dance"], Pool(library, new RadioFilter { Searches = ["miles"], ExcludeSearches = ["is:fav", "freddie"] }));
    }

    [Fact]
    public void A_radio_search_with_a_bad_is_value_is_refused()
    {
        using var library = OpenRead();

        var ex = Assert.Throws<MusicSpinException>(() => library.RadioPool(new RadioFilter { Searches = ["is:loved"] }).ToList());

        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
    }
}
