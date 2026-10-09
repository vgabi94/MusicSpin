using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Search;

[Trait("Category", "Unhappy")]
public sealed class LibrarySearchUnhappyTests : LibraryTestBase
{
    [Theory]
    [InlineData("", "must not be empty")]
    [InlineData("   ", "must not be empty")]
    [InlineData("!!!", "no letters or digits")]
    [InlineData("- & ... ?", "no letters or digits")]
    [InlineData("\"\"", "no letters or digits")]
    [InlineData("\"   \"", "no letters or digits")]
    [InlineData("artist:", "'artist:' needs a value")]
    [InlineData("ARTIST:", "'ARTIST:' needs a value")]
    [InlineData("miles album:", "'album:' needs a value")]
    [InlineData("title:\"\"", "'title:' needs a value")]
    [InlineData("genre:   jazz", "'genre:' needs a value")]
    [InlineData("title:!!!", "no letters or digits")]
    [InlineData("year:", "'year:' needs a value")]
    [InlineData("year:abc", "Bad year")]
    [InlineData("year:12345", "Bad year")]
    [InlineData("year:-5", "Bad year")]
    [InlineData("year:1959-", "Bad year")]
    [InlineData("year:2000-1990", "first year is after")]
    public void Unusable_queries_are_invalid_arguments(string text, string message)
    {
        var ex = Assert.Throws<MusicSpinException>(() => LibrarySearch.Parse(text));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Very_long_queries_and_too_many_terms_are_refused_with_the_limits()
    {
        Assert.Single(LibrarySearch.Parse(new string('a', LibrarySearch.MaxQueryLength)).Terms);
        Assert.Contains("too long (1001 characters; at most 1000)",
            Assert.Throws<MusicSpinException>(() => LibrarySearch.Parse(new string('a', 1001))).Message, StringComparison.Ordinal);
        Assert.Equal(32, LibrarySearch.Parse(string.Join(' ', Enumerable.Repeat("w", 32))).Terms.Count);
        Assert.Contains("Too many search terms (33",
            Assert.Throws<MusicSpinException>(() => LibrarySearch.Parse(string.Join(' ', Enumerable.Repeat("w", 33)))).Message, StringComparison.Ordinal);
        Assert.Contains("too long", Assert.Throws<MusicSpinException>(() => LibrarySearch.Contains(SearchField.Title, new string('x', 1001), "--title")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_unicode_and_query_metacharacters_are_harmless()
    {
        Assert.Equal("abc def", LibrarySearch.Fold("\ud800abc\udc00def"));
        Assert.Equal("ok", LibrarySearch.Fold("ok 🎹")); // symbols and emoji are word breaks, not errors
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();
        foreach (var text in new[] { "50% off_", "'); DROP TABLE tracks; --", "$.Path", "zz* qq?", "\\\\server\\share", "[playlist]", "@q9" })
        {
            Assert.Equal(0, library.Search(text).Tracks.Total);
        }

        Assert.Equal(0, library.Search(new string('z', 1000)).Tracks.Total);
        Assert.Equal(6, library.Tracks(new TrackQuery()).Total); // nothing was harmed
    }

    [Fact]
    public void A_tag_with_an_unpaired_surrogate_is_scanned_and_searchable()
    {
        LibraryTestData.Write(Path.Combine(Music, "bad.flac"), LibraryTestData.Tiny16, "Broken\ud800Title", "Someone");
        var result = AddAndScan();
        Assert.Equal(0, result.Failed);
        using var library = OpenRead();
        Assert.Equal(1, library.Search("broken title").Tracks.Total);
    }

    [Theory]
    [InlineData(null, "  ", null, "--title needs some text")]
    [InlineData(null, "!!!", null, "--title '!!!' has no letters")]
    [InlineData(null, null, "", "--album-contains needs some text")]
    [InlineData("", null, null, "must not be empty")]
    public void Empty_or_punctuation_only_filters_are_invalid(string? text, string? title, string? album, string message)
    {
        using var library = OpenRead();
        var ex = Assert.Throws<MusicSpinException>(() => library.Tracks(new TrackQuery { Text = text, Title = title, AlbumContains = album }));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument,
            Assert.Throws<MusicSpinException>(() => library.Artists(new ArtistQuery { Name = " " })).Code);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument,
            Assert.Throws<MusicSpinException>(() => library.Albums(new AlbumQuery { ArtistContains = "..." })).Code);
    }

    [Fact]
    public void Unknown_album_or_artist_is_not_found_everywhere_while_text_filters_just_match_nothing()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();
        foreach (var call in new Action[]
                 {
                     () => library.Tracks(new TrackQuery { Album = "No Such Album" }),
                     () => library.Tracks(new TrackQuery { Artist = "Nobody" }),
                     () => library.Tracks(new TrackQuery { Artist = "Miles Davis", Album = "No Such Album" }),
                     () => library.Albums(new AlbumQuery { Artist = "Nobody" }),
                     () => library.AlbumQueue("No Such Album"),
                     () => library.ArtistQueue("Nobody"),
                 })
        {
            Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(call).Code);
        }

        Assert.Equal(0, library.Tracks(new TrackQuery { AlbumContains = "No Such Album" }).Total);
        Assert.Equal(0, library.Tracks(new TrackQuery { Title = "zzz" }).Total);
        Assert.Equal(0, library.Albums(new AlbumQuery { Title = "zzz" }).Total);
        Assert.Equal(0, library.Artists(new ArtistQuery { Name = "zzz" }).Total);
        // A known album with filters that exclude everything is an empty result, not an error.
        Assert.Equal(0, library.Tracks(new TrackQuery { Album = "Kind of Blue", Title = "zzz" }).Total);
        // Ambiguity is still reported by play (AlbumQueue) only; listings show every album with that title.
        Assert.Equal(2, library.Tracks(new TrackQuery { Album = "kind of blue" }).Total);
    }

    [Fact]
    public void Searching_an_empty_or_missing_library_returns_nothing()
    {
        using (var library = OpenRead())
        {
            var result = library.Search("anything");
            Assert.Empty(result.Artists);
            Assert.Empty(result.Albums);
            Assert.Equal(0, result.Tracks.Total);
            Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.Tracks(new TrackQuery { Album = "x" })).Code);
        }

        Assert.False(Directory.Exists(Data.Path)); // queries never create the data folder
    }
}
