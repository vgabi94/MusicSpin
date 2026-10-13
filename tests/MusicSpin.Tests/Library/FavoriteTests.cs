using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using Microsoft.Extensions.Time.Testing;

namespace MusicSpin.Tests.Library;

/// <summary>Favorite tracks: marking, the Favorites view, favorites radio, and favorites following their tracks.</summary>
public sealed class FavoriteTests : LibraryTestBase
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private string SoWhat => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");
    private string Freddie => Path.Combine(Music, "Miles", "Kind of Blue", "02 Freddie Freeloader.flac");
    private string Pharaoh => Path.Combine(Music, "Miles", "Bitches Brew", "01 Pharaoh's Dance.flac");
    private string Lossy => Path.Combine(Music, "Lossy", "track.mp3");

    public FavoriteTests()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
    }

    private MusicLibrary OpenTimed() => MusicLibrary.OpenWritable(Data, null, TimeSpan.FromMilliseconds(200), _time);

    private string IdOf(string path) => Assert.Single(AllTracks(), t => t.Path == Path.GetFullPath(path)).Id;

    /// <summary>Favorites the files in order, a minute apart.</summary>
    private void Favorite(params string[] paths)
    {
        foreach (var path in paths)
        {
            var id = IdOf(path);
            using var library = OpenTimed();
            Assert.Equal(1, library.AddFavorites([id]));
            _time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    private string[] FavoritePaths()
    {
        IReadOnlySet<string> ids;
        using (var library = OpenRead())
        {
            ids = library.FavoriteTrackIds();
        }

        return [.. AllTracks().Where(t => ids.Contains(t.Id)).Select(t => t.Path).Order(StringComparer.Ordinal)];
    }

    private static void AssertSummary(FavoritesSummary summary, int count, string[] missing)
    {
        Assert.Equal((count, missing.Length), (summary.Count, summary.Missing));
        Assert.Equal(missing, summary.MissingPaths);
    }

    private string[] Titles(TrackQuery query)
    {
        using var library = OpenRead();
        return [.. library.Tracks(query).Items.Select(t => t.Title!)];
    }

    // ---- marking -----------------------------------------------------------------------------------------------

    [Fact]
    public void Add_and_remove_report_what_changed_and_repeats_change_nothing()
    {
        var (soWhat, freddie, lossy) = (IdOf(SoWhat), IdOf(Freddie), IdOf(Lossy));
        using var library = OpenTimed();

        Assert.Equal(2, library.AddFavorites([soWhat, freddie, soWhat.ToUpperInvariant()]));
        Assert.Equal(0, library.AddFavorites([soWhat]));
        Assert.Equal(1, library.AddFavorites([soWhat, lossy]));
        Assert.Equal(3, library.FavoriteTrackIds().Count);

        Assert.Equal(2, library.RemoveFavorites([soWhat, freddie]));
        Assert.Equal(0, library.RemoveFavorites([soWhat]));
        Assert.Equal([lossy], library.FavoriteTrackIds());
        AssertSummary(library.FavoritesSummary(), 1, []);
    }

    [Fact]
    public void Adding_again_keeps_the_time_it_was_first_favorited()
    {
        Favorite(SoWhat, Freddie);
        var id = IdOf(SoWhat);
        using (var library = OpenTimed())
        {
            library.AddFavorites([id]);
        }

        Assert.Equal(["Freddie Freeloader", "So What"], Titles(new TrackQuery { Favorite = true, Sort = TrackSort.Favorited, Descending = true }));
    }

    [Fact]
    public void Unknown_or_malformed_ids_change_nothing()
    {
        var known = IdOf(SoWhat);
        using var library = OpenTimed();
        var unknown = Guid.CreateVersion7().ToString();

        var ex = Assert.Throws<MusicSpinException>(() => library.AddFavorites([known, unknown]));
        Assert.Equal(MusicSpinErrorCode.NotFound, ex.Code);
        Assert.Contains(unknown, ex.Message, StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.RemoveFavorites([unknown])).Code);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Assert.Throws<MusicSpinException>(() => library.AddFavorites(["not-an-id"])).Code);
        Assert.Empty(library.FavoriteTrackIds());
        Assert.Throws<ArgumentNullException>(() => library.AddFavorites(null!));
    }

    [Fact]
    public void Favorites_need_a_writable_library()
    {
        RequirePosixNonRoot();
        var id = IdOf(SoWhat);
        Favorite(Freddie);
        Chmod(Data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var library = OpenRead();
            Assert.True(library.IsReadOnly);
            Assert.Single(library.FavoriteTrackIds()); // reading works
            Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, Assert.Throws<MusicSpinException>(() => library.AddFavorites([id])).Code);
            Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, Assert.Throws<MusicSpinException>(() => library.RemoveFavorites([id])).Code);
            Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, Assert.Throws<MusicSpinException>(() => library.RemoveMissingFavorites()).Code);
        }
        finally
        {
            Chmod(Data.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_library_without_favorites_reads_as_none()
    {
        using var library = OpenRead();
        Assert.Empty(library.FavoriteTrackIds());
        AssertSummary(library.FavoritesSummary(), 0, []);
        Assert.Empty(library.FavoritesQueue());
        Assert.Equal(0, library.Tracks(new TrackQuery { Favorite = true }).Total);
        Assert.Equal(6, library.Tracks(new TrackQuery { Favorite = false }).Total);
    }

    // ---- following the track -----------------------------------------------------------------------------------

    [Fact]
    public void A_favorite_survives_a_rescan_of_a_changed_file()
    {
        Favorite(SoWhat);
        var id = IdOf(SoWhat);
        LibraryTestData.Write(SoWhat, LibraryTestData.Tiny24, "So What (Remastered)", "Miles Davis", "Kind of Blue", "Miles Davis", 1, 1, 1959, "Jazz");
        Touch(SoWhat);

        Assert.Equal(1, Rescan().Updated);

        Assert.Equal(id, IdOf(SoWhat));
        Assert.Equal(["So What (Remastered)"], Titles(new TrackQuery { Favorite = true }));
    }

    [Fact]
    public void A_favorite_follows_a_move_found_by_a_scan()
    {
        Favorite(SoWhat);
        var moved = Path.Combine(Music, "Elsewhere", "So What.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(SoWhat, moved);

        Assert.Equal(1, Rescan().Moved);

        Assert.Equal([moved], FavoritePaths());
        using var library = OpenRead();
        AssertSummary(library.FavoritesSummary(), 1, []);
    }

    [Fact]
    public void A_favorite_follows_library_relocate()
    {
        Favorite(SoWhat);
        var miles = Path.Combine(Music, "Miles");
        var target = Path.Combine(Music, "Miles Davis");
        Directory.Move(miles, target);
        using (var library = Open())
        {
            library.Relocate(miles, target);
        }

        var path = Path.Combine(target, "Kind of Blue", "01 So What.flac");
        Assert.Equal([path], FavoritePaths());
        File.Delete(path);
        Rescan();
        using var read = OpenRead();
        Assert.Equal([path], read.FavoritesSummary().MissingPaths); // the stored path was kept current
    }

    [Fact]
    public void A_favorite_whose_track_left_is_kept_and_counts_again_when_the_file_is_back()
    {
        Favorite(SoWhat, Freddie);
        var oldId = IdOf(SoWhat);
        using (var library = Open())
        {
            library.RemoveFolder(Music);
        }

        using (var library = OpenRead())
        {
            Assert.Empty(library.FavoriteTrackIds());
            AssertSummary(library.FavoritesSummary(), 0, [Path.GetFullPath(SoWhat), Path.GetFullPath(Freddie)]);
            Assert.Equal(0, library.Tracks(new TrackQuery { Favorite = true }).Total);
        }

        AddAndScan();
        Assert.NotEqual(oldId, IdOf(SoWhat)); // a new track id, the same path
        Assert.Equal([Path.GetFullPath(SoWhat), Path.GetFullPath(Freddie)], FavoritePaths());
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(new TrackQuery { Favorite = true, Sort = TrackSort.Favorited }));

        // ... and it follows that new track when it moves.
        var moved = Path.Combine(Music, "So What.flac");
        File.Move(SoWhat, moved);
        Rescan();
        Assert.Equal([Path.GetFullPath(Freddie), moved], FavoritePaths());
        var (movedId, freddieId) = (IdOf(moved), IdOf(Freddie));
        using (var library = OpenTimed())
        {
            Assert.Equal(1, library.RemoveFavorites([movedId]));
            Assert.Equal([freddieId], library.FavoriteTrackIds());
        }
    }

    [Fact]
    public void Missing_favorites_can_be_listed_and_removed_by_path_or_all_at_once()
    {
        Favorite(SoWhat, Freddie, Pharaoh, Lossy);
        File.Delete(SoWhat);
        File.Delete(Freddie);
        File.Delete(Pharaoh);
        Rescan();

        using var library = OpenTimed();
        var summary = library.FavoritesSummary();
        Assert.Equal((1, 3), (summary.Count, summary.Missing));
        Assert.Equal(new[] { Path.GetFullPath(Pharaoh), Path.GetFullPath(SoWhat), Path.GetFullPath(Freddie) }.Order(StringComparer.Ordinal), summary.MissingPaths);

        Assert.Equal(1, library.RemoveMissingFavorites([SoWhat, Lossy, Path.Combine(Music, "never.flac")])); // Lossy is in the library: kept
        Assert.Equal(2, library.FavoritesSummary().Missing);
        Assert.Equal(2, library.RemoveMissingFavorites());
        AssertSummary(library.FavoritesSummary(), 1, []);
        Assert.Equal(0, library.RemoveMissingFavorites());
    }

    // ---- the Favorites view ------------------------------------------------------------------------------------

    [Fact]
    public void Favorite_true_lists_only_favorites_and_false_only_the_rest_with_the_other_filters_and_paging()
    {
        Favorite(SoWhat, Pharaoh, Lossy);

        Assert.Equal(["Lossy Song", "Pharaoh's Dance", "So What"], Titles(new TrackQuery { Favorite = true }));
        Assert.Equal(["Freddie Freeloader", "Song A", "Song B"], Titles(new TrackQuery { Favorite = false }));
        Assert.Equal(["Pharaoh's Dance", "So What"], Titles(new TrackQuery { Favorite = true, Artist = "Miles Davis" }));
        Assert.Equal(["So What"], Titles(new TrackQuery { Favorite = true, HiRes = true }));
        Assert.Equal(["Lossy Song"], Titles(new TrackQuery { Favorite = true, Text = "lossy" }));
        Assert.Equal(["Song A"], Titles(new TrackQuery { Favorite = false, Text = "\"song a\"" }));
        Assert.Equal(["Freddie Freeloader"], Titles(new TrackQuery { Favorite = false, Album = "Kind of Blue" }));

        using var library = OpenRead();
        var page = library.Tracks(new TrackQuery { Favorite = true, Paging = new Paging(1, 1) });
        Assert.Equal((3, "Pharaoh's Dance"), (page.Total, Assert.Single(page.Items).Title));
        Assert.Equal(3, library.Tracks(new TrackQuery { Favorite = false, Paging = new Paging(0, 1) }).Total);
    }

    [Fact]
    public void Sorting_by_when_favorited_both_ways_and_with_a_search()
    {
        Favorite(Lossy, SoWhat, Pharaoh);

        Assert.Equal(["Lossy Song", "So What", "Pharaoh's Dance"], Titles(new TrackQuery { Favorite = true, Sort = TrackSort.Favorited }));
        Assert.Equal(["Pharaoh's Dance", "So What", "Lossy Song"], Titles(new TrackQuery { Favorite = true, Sort = TrackSort.Favorited, Descending = true }));
        Assert.Equal(["Pharaoh's Dance", "So What"], Titles(new TrackQuery { Favorite = true, Artist = "Miles Davis", Sort = TrackSort.Favorited, Descending = true }));
        Assert.Equal(["Pharaoh's Dance", "So What"], Titles(new TrackQuery { Favorite = true, Text = "miles", Sort = TrackSort.Favorited, Descending = true }));
        Assert.Equal(["Lossy Song", "So What"], Titles(new TrackQuery { Favorite = true, Sort = TrackSort.Favorited, Paging = new Paging(0, 2) }));

        using var library = OpenRead();
        Assert.Equal(["Pharaoh's Dance", "So What", "Lossy Song"], library.FavoritesQueue().Select(t => t.Title!));
    }

    [Fact]
    public void Sorting_by_when_favorited_without_favorite_true_is_the_default_order()
    {
        Favorite(Lossy, SoWhat);
        using var library = OpenRead();
        string[] Default(bool descending, bool? favorite = null)
            => [.. library.Tracks(new TrackQuery { Favorite = favorite, Descending = descending }).Items.Select(t => t.Title!)];
        string[] Favorited(bool descending, bool? favorite = null)
            => [.. library.Tracks(new TrackQuery { Favorite = favorite, Sort = TrackSort.Favorited, Descending = descending }).Items.Select(t => t.Title!)];

        Assert.Equal(Default(false), Favorited(false));
        Assert.Equal(Default(true), Favorited(true));
        Assert.Equal(Default(false, favorite: false), Favorited(false, favorite: false));
    }

    // ---- radio -------------------------------------------------------------------------------------------------

    private string[] RadioTitles(RadioFilter filter)
    {
        using var library = OpenRead();
        return [.. library.RadioPool(filter).Select(c => c.Title!).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void Favorites_radio_plays_only_favorites_and_narrows_the_other_filters()
    {
        Favorite(SoWhat, Pharaoh, Lossy);

        Assert.Equal(["Lossy Song", "Pharaoh's Dance", "So What"], RadioTitles(new RadioFilter { Favorites = true }));
        Assert.Equal(["Pharaoh's Dance", "So What"], RadioTitles(new RadioFilter { Favorites = true, Genres = ["jazz"] }));
        Assert.Equal(["So What"], RadioTitles(new RadioFilter { Favorites = true, HiRes = true }));
        Assert.Equal(["Pharaoh's Dance", "So What"], RadioTitles(new RadioFilter { Favorites = true, Artists = ["Miles Davis"] }));
        Assert.Equal(["Lossy Song", "So What"], RadioTitles(new RadioFilter { Favorites = true, ExcludeAlbums = ["Bitches Brew"] }));
        Assert.Equal(["Lossy Song", "Pharaoh's Dance", "So What"], RadioTitles(new RadioFilter { Favorites = true, Searches = ["s"] }));

        using var library = OpenRead();
        Assert.True(library.HasRadioTracks(new RadioFilter { Favorites = true }));
        Assert.False(library.HasRadioTracks(new RadioFilter { Favorites = true, Genres = ["Pop"], HiRes = true }));
    }

    [Fact]
    public void Favorites_count_as_an_include_and_are_described()
    {
        Assert.False(new RadioFilter { Favorites = true }.IsEmpty);
        Assert.True(new RadioFilter().IsEmpty);
        Assert.Equal("favorites", new RadioFilter { Favorites = true }.Describe());
        Assert.Equal("genre Jazz · hi-res · favorites · excluding artist Kenny G",
            new RadioFilter { Favorites = true, HiRes = true, Genres = ["Jazz"], ExcludeArtists = ["Kenny G"] }.Describe());
    }

    [Fact]
    public void Empty_favorites_stations_say_why()
    {
        var favorites = new RadioFilter { Favorites = true };
        using (var library = OpenRead())
        {
            Assert.Empty(library.RadioPool(favorites));
            Assert.Contains("no favorites yet", library.RadioEmptyPoolMessage(favorites), StringComparison.Ordinal);
            Assert.Equal(RadioFilter.NoFavoritesMessage, favorites.EmptyPoolMessage());
        }

        Favorite(SoWhat, Freddie);
        var popFavorites = favorites with { Genres = ["Pop"] };
        using (var library = OpenRead())
        {
            Assert.Empty(library.RadioPool(popFavorites));
            Assert.Equal("No library tracks match the radio filters (genre Pop · favorites).", library.RadioEmptyPoolMessage(popFavorites));
            Assert.Equal(popFavorites.EmptyPoolMessage(), library.RadioEmptyPoolMessage(popFavorites));
            Assert.Equal("No library tracks are left after the excludes (favorites · excluding album Kind of Blue).",
                library.RadioEmptyPoolMessage(favorites with { ExcludeAlbums = ["Kind of Blue"] }));
        }

        File.Delete(SoWhat);
        File.Delete(Freddie);
        Rescan();
        using (var library = OpenRead())
        {
            Assert.Equal("None of your 2 favorites are in the library now. See 'music-spin favorites --missing'.", library.RadioEmptyPoolMessage(favorites));
            Assert.Equal(new RadioFilter().EmptyPoolMessage(), library.RadioEmptyPoolMessage(new RadioFilter()));
        }
    }

    [Fact]
    public void The_radio_pool_cache_picks_up_a_new_favorite()
    {
        Favorite(SoWhat);
        var cache = new RadioPoolCache(Data, TimeSpan.FromMilliseconds(500));
        var favorites = new RadioFilter { Favorites = true };
        Assert.Equal(["So What"], cache.Get(favorites).Select(c => c.Title));
        Assert.Same(cache.Get(favorites), cache.Get(favorites));

        Favorite(Lossy);

        Assert.Equal(["Lossy Song", "So What"], cache.Get(favorites).Select(c => c.Title!).Order(StringComparer.Ordinal));
        Assert.Equal(2, cache.Queries);
    }
}
