using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

public sealed class GenreQueueTests : LibraryTestBase
{
    private MusicLibrary Library()
    {
        LibraryTestData.CreateGenreLibrary(Music);
        AddAndScan();
        return OpenRead();
    }

    private static List<string?> Titles(IReadOnlyList<TrackRecord> tracks) => [.. tracks.Select(t => t.Title)];

    [Fact]
    public void Matches_the_exact_name_ignoring_case_and_splitting_multi_genre_tags_in_album_artist_then_year_order()
    {
        using var library = Library();

        // John Coltrane (Blues/Jazz), then Miles Davis: Kind of Blue (1959, disc/track order) before Bitches Brew (1970, "Jazz; Fusion").
        Assert.Equal(["Blue Train", "So What", "Freddie Freeloader", "Pharaoh's Dance"], Titles(library.GenreQueue(["Jazz"])));
        Assert.Equal(Titles(library.GenreQueue(["Jazz"])), Titles(library.GenreQueue(["  jAzZ "])));
        Assert.Equal(["Pharaoh's Dance"], Titles(library.GenreQueue(["fusion"])));
    }

    [Fact]
    public void Ignores_diacritics_both_ways()
    {
        using var library = Library();

        Assert.Equal(["La Femme"], Titles(library.GenreQueue(["electronique"])));
        Assert.Equal(["La Femme"], Titles(library.GenreQueue(["ÉLECTRONIQUE"])));
    }

    [Fact]
    public void Is_not_a_contains_match()
    {
        using var library = Library();

        Assert.Equal(["Folk Song"], Titles(library.GenreQueue(["folk rock"])));
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.GenreQueue(["Rock"])).Code);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.GenreQueue(["Jaz"])).Code);
    }

    [Fact]
    public void Several_genres_are_any_of_them_without_duplicates()
    {
        using var library = Library();

        // Blue Train is both Blues and Jazz: listed once. Nina Simone sorts after Miles Davis.
        Assert.Equal(["Blue Train", "So What", "Freddie Freeloader", "Pharaoh's Dance", "Pastel Blues"], Titles(library.GenreQueue(["jazz", "blues"])));
        Assert.Equal(["Blue Train", "Pastel Blues"], Titles(library.GenreQueue(["blues"])));
        Assert.Equal(Titles(library.GenreQueue(["jazz"])), Titles(library.GenreQueue(["jazz", "polka", "JAZZ"])));
    }

    [Fact]
    public void Artist_narrows_the_genre()
    {
        using var library = Library();

        Assert.Equal(["So What", "Freddie Freeloader", "Pharaoh's Dance"], Titles(library.GenreQueue(["jazz"], "miles davis")));
        Assert.Equal(["Blue Train"], Titles(library.GenreQueue(["blues"], "John Coltrane")));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void No_match_unknown_artist_and_empty_names_fail_with_stable_codes()
    {
        using var library = Library();

        var none = Assert.Throws<MusicSpinException>(() => library.GenreQueue(["Polka", "Ska"]));
        Assert.Equal(MusicSpinErrorCode.NotFound, none.Code);
        Assert.Contains("'Polka' or 'Ska'", none.Message, StringComparison.Ordinal);

        // A known artist with no tracks of the genre, and an unknown artist.
        var mismatch = Assert.Throws<MusicSpinException>(() => library.GenreQueue(["Blues"], "Miles Davis"));
        Assert.Equal(MusicSpinErrorCode.NotFound, mismatch.Code);
        Assert.Contains("by 'Miles Davis'", mismatch.Message, StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.GenreQueue(["Jazz"], "Nobody Known")).Code);

        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Assert.Throws<MusicSpinException>(() => library.GenreQueue([" ", ""])).Code);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Assert.Throws<MusicSpinException>(() => library.GenreQueue([])).Code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_empty_library_has_no_genre()
    {
        AddAndScan();
        using var library = OpenRead();

        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.GenreQueue(["Jazz"])).Code);
    }
}
