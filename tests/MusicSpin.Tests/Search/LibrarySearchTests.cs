using LiteDB;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Search;

/// <summary>Folding, the query syntax, field filters, ranking and the stored search keys.</summary>
public sealed class LibrarySearchTests : LibraryTestBase
{
    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Mötley Crüe", "motley crue")]
    [InlineData("Sigur Rós – Ágætis byrjun", "sigur ros agaetis byrjun")]
    [InlineData("Straße", "strasse")]
    [InlineData("STRASSE", "strasse")]
    [InlineData("Røyksopp", "royksopp")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Œuvre", "oeuvre")]
    [InlineData("Þórr", "thorr")]
    [InlineData("Pharaoh's  Dance", "pharaohs dance")]
    [InlineData("Pharaoh’s Dance", "pharaohs dance")]
    [InlineData("R.E.M.", "rem")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("  Hello,   World!  ", "hello world")]
    [InlineData("ＡＢＣ １２３", "abc 123")]
    [InlineData("ﬁve", "five")]
    [InlineData("한국어", "한국어")]
    [InlineData("日本語", "日本語")]
    [InlineData("Ⅳ", "iv")]
    [InlineData("!!!", "")]
    [InlineData(null, "")]
    public void Fold_ignores_case_diacritics_and_punctuation(string? input, string expected)
        => Assert.Equal(expected, LibrarySearch.Fold(input));

    [Fact]
    public void Parse_understands_words_phrases_prefixes_and_years()
    {
        var q = LibrarySearch.Parse("  Miles  \"Kind of Blue\" ARTIST:davis album:\"kind of\" title:what genre:jazz year:1959 foo:bar AC/DC ");
        Assert.Equal(
            [
                new SearchTerm(SearchField.Any, "miles", false),
                new SearchTerm(SearchField.Any, "kind of blue", true),
                new SearchTerm(SearchField.Artist, "davis", false),
                new SearchTerm(SearchField.Album, "kind of", true),
                new SearchTerm(SearchField.Title, "what", false),
                new SearchTerm(SearchField.Genre, "jazz", false),
                new SearchTerm(SearchField.Any, "foo", false),
                new SearchTerm(SearchField.Any, "bar", false),
                new SearchTerm(SearchField.Any, "ac", false),
                new SearchTerm(SearchField.Any, "dc", false),
            ], q.Terms);
        Assert.Equal((1959, 1959), (q.YearFrom, q.YearTo));

        var range = LibrarySearch.Parse("year:1950-1960 year:1955-1970 “smart quotes”");
        Assert.Equal((1955, 1960), (range.YearFrom, range.YearTo));
        Assert.Equal(new SearchTerm(SearchField.Any, "smart quotes", true), Assert.Single(range.Terms));
        Assert.Equal(new SearchTerm(SearchField.Any, "unterminated phrase", true), Assert.Single(LibrarySearch.Parse("\"unterminated phrase").Terms));
        Assert.Equal(2, LibrarySearch.Parse("- & miles + davis").Terms.Count); // stray punctuation is ignored
    }

    [Fact]
    public void Search_matches_every_word_across_fields_ignoring_diacritics()
    {
        CreateLibrary();
        using var library = OpenRead();
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(library.Search("miles blue").Tracks));
        Assert.Equal(["Crazy in Love"], Titles(library.Search("BEYONCE crazy").Tracks));
        Assert.Equal(["Jóga"], Titles(library.Search("bjork joga").Tracks));
        Assert.Equal(["Pharaoh's Dance"], Titles(library.Search("pharaohs").Tracks));
        Assert.Equal(["Pharaoh's Dance"], Titles(library.Search("PHARAOH’S").Tracks));
        Assert.Empty(library.Search("miles zzz").Tracks.Items);
        Assert.Equal("Björk", Assert.Single(library.Search("bjork").Artists).Name);
        Assert.Equal("Homogenic", Assert.Single(library.Search("homogenic").Albums).Title);
    }

    [Fact]
    public void Phrases_must_appear_as_a_whole_inside_one_field()
    {
        CreateLibrary();
        using var library = OpenRead();
        Assert.Equal(2, library.Search("\"kind of blue\"").Tracks.Total);
        Assert.Equal(0, library.Search("\"blue kind\"").Tracks.Total);
        Assert.Equal(0, library.Search("\"davis kind\"").Tracks.Total); // artist + album: different fields
        Assert.Equal(2, library.Search("kind blue").Tracks.Total);
    }

    [Fact]
    public void Field_prefixes_restrict_terms_to_one_field()
    {
        CreateLibrary();
        using var library = OpenRead();
        Assert.Equal(3, library.Search("artist:miles").Tracks.Total);
        Assert.Equal(0, library.Search("title:miles").Tracks.Total);
        Assert.Equal(2, library.Search("album:blue").Tracks.Total);
        Assert.Equal(["Lossy Song"], Titles(library.Search("genre:pop").Tracks));
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(library.Search("year:1959").Tracks));
        Assert.Equal(["Pharaoh's Dance"], Titles(library.Search("year:1960-1980 artist:davis").Tracks));
        Assert.Equal(["Lossy Song", "Song A"], Titles(library.Search("artist:\"artist a\"").Tracks));
        Assert.Equal(["Song A", "Song B"], Titles(library.Search("artist:various").Tracks)); // the effective album artist of a compilation

        var titleOnly = library.Search("title:freddie");
        Assert.Empty(titleOnly.Artists);
        Assert.Empty(titleOnly.Albums);
        Assert.Single(titleOnly.Tracks.Items);
        var albumAndArtist = library.Search("album:blue artist:miles");
        Assert.Equal("Kind of Blue", Assert.Single(albumAndArtist.Albums).Title);
        Assert.Empty(albumAndArtist.Artists);
        Assert.Equal("Bitches Brew", Assert.Single(library.Search("year:1970").Albums).Title);
    }

    [Fact]
    public void Results_are_ranked_exact_then_prefix_then_word_start_then_anywhere()
    {
        foreach (var (file, title) in new[] { ("1 a.flac", "Royalblue"), ("2 b.flac", "Kind of Blue"), ("3 c.flac", "Blues"), ("4 d.flac", "Blue") })
        {
            LibraryTestData.Write(Path.Combine(Music, "Rank", file), LibraryTestData.Tiny16, title, "Ranker", "Test Ranks", track: 1);
        }

        AddAndScan();
        using var library = OpenRead();
        Assert.Equal(["Blue", "Blues", "Kind of Blue", "Royalblue"], Titles(library.Search("blue").Tracks));
        Assert.Equal(["Royalblue", "Kind of Blue", "Blues", "Blue"], Titles(library.Tracks(new TrackQuery { Text = "blue", Sort = TrackSort.Relevance, Descending = true })));
        // Without relevance sort, matches keep the library order.
        Assert.Equal(["Royalblue", "Kind of Blue", "Blues", "Blue"], Titles(library.Tracks(new TrackQuery { Text = "blue" })));
        Assert.Equal(4, library.Search("blue", new Paging(0, 2)).Tracks.Total);
        Assert.Equal(["Kind of Blue", "Royalblue"], Titles(library.Search("blue", new Paging(2, 2)).Tracks));
    }

    [Fact]
    public void Artist_and_album_results_are_ranked_too()
    {
        CreateLibrary();
        LibraryTestData.Write(Path.Combine(Music, "X", "x.flac"), LibraryTestData.Tiny16, "X", "Smiles Club", "Miles Away");
        Rescan();
        using var library = OpenRead();
        var result = library.Search("miles");
        Assert.Equal(["Miles Davis", "Smiles Club"], result.Artists.Select(a => a.Name));
        Assert.Equal("Miles Away", result.Albums[0].Title); // album title prefix beats album artist matches
    }

    [Fact]
    public void Contains_filters_work_per_field_and_combine()
    {
        CreateLibrary();
        using var library = OpenRead();
        Assert.Equal(["So What"], Titles(library.Tracks(new TrackQuery { Title = "WHAT so" })));
        Assert.Equal(2, library.Tracks(new TrackQuery { AlbumContains = "blue" }).Total);
        Assert.Equal(3, library.Tracks(new TrackQuery { ArtistContains = "davis" }).Total);
        Assert.Equal(["Freddie Freeloader"], Titles(library.Tracks(new TrackQuery { ArtistContains = "davis", Title = "fred", AlbumContains = "kind" })));
        Assert.Equal(0, library.Tracks(new TrackQuery { Title = "blue" }).Total); // album words don't count for --title
        Assert.Equal(["Freddie Freeloader"], Titles(library.Tracks(new TrackQuery { Text = "miles", Title = "freddie" })));
        Assert.Equal(["Pharaoh's Dance"], Titles(library.Tracks(new TrackQuery { ArtistContains = "davis", MinBitDepth = 1, YearFrom = 1960 })));

        Assert.Equal(["Kind of Blue"], library.Albums(new AlbumQuery { Title = "BLUE" }).Items.Select(a => a.Title));
        Assert.Equal(["Bitches Brew", "Kind of Blue"], library.Albums(new AlbumQuery { ArtistContains = "davis" }).Items.Select(a => a.Title).Order());
        Assert.Equal(["Jazz Hits"], library.Albums(new AlbumQuery { ArtistContains = "artist b" }).Items.Select(a => a.Title)); // a track artist of a compilation
        Assert.Equal(["Björk"], library.Artists(new ArtistQuery { Name = "BJORK" }).Items.Select(a => a.Name));
        Assert.Equal(["Artist A", "Artist B", "Various Artists"], library.Artists(new ArtistQuery { Name = "artist" }).Items.Select(a => a.Name));
    }

    [Fact]
    public void Exact_artist_and_album_selectors_fall_back_to_ignoring_diacritics()
    {
        CreateLibrary();
        using var library = OpenRead();
        Assert.Equal(2, library.ArtistQueue("bjork").Count);
        Assert.Equal(2, library.Tracks(new TrackQuery { Artist = "BJÖRK" }).Total);
        Assert.Equal(2, library.AlbumQueue("homogénic").Count);
        Assert.Equal(2, library.Tracks(new TrackQuery { Album = "Homogenic" }).Total);
        Assert.Equal(["Homogenic"], library.Albums(new AlbumQuery { Artist = "bjork" }).Items.Select(a => a.Title));
    }

    [Fact]
    public void Scan_stores_folded_keys()
    {
        CreateLibrary();
        var joga = Assert.Single(AllTracks(), t => t.Title == "Jóga");
        Assert.Equal("joga", joga.SearchTitle);
        Assert.Equal("bjork", joga.SearchArtist);
        Assert.Equal("homogenic", joga.SearchAlbum);
        Assert.Equal("electronic", joga.SearchGenre);
        Assert.Equal("01 joga", joga.SearchFile);
        Assert.Equal("joga\u001fbjork\u001fhomogenic\u001felectronic\u001f01 joga", joga.SearchText);
        var compilation = Assert.Single(AllTracks(), t => t.Title == "Song A");
        Assert.Equal("artist a\u001fvarious artists", compilation.SearchArtist);
    }

    [Fact]
    public void Opening_an_older_database_upgrades_once_and_rebuilds_search_keys()
    {
        CreateLibrary();
        var before = AllTracks().ToDictionary(t => t.Path, t => t.Id, StringComparer.Ordinal);
        DowngradeToSchema2();
        using (var library = Open())
        {
            Assert.NotNull(library.Migration);
            Assert.Equal(["Jóga"], Titles(library.Search("joga").Tracks));
            Assert.Equal(before.Count, library.Migration!.Tracks);
        }

        using var db = new LiteDatabase(new ConnectionString { Filename = Data.LibraryDatabaseFile, Connection = ConnectionType.Direct, ReadOnly = true });
        Assert.Equal(LibraryDatabase.SchemaVersion, LibraryMigration.StoredVersion(db));
        Assert.All(db.GetCollection("tracks").FindAll(), d =>
        {
            Assert.NotEqual(string.Empty, d["SearchText"].AsString);
            Assert.True(LibraryKeys.IsId(d["_id"].AsString));
            Assert.Equal(32, d["PathKey"].AsString.Length);
        });
        Assert.True(File.Exists(Data.LibraryDatabaseFile + ".bak-2"));
        // Ids are new GUIDs (schema 2 used path hashes); playlists would be remapped the same way.
        Assert.All(AllTracks(), t => Assert.NotEqual(before[t.Path], t.Id));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_older_database_on_read_only_media_needs_a_writable_upgrade()
    {
        RequirePosixNonRoot();
        CreateLibrary();
        DowngradeToSchema2();
        Chmod(Data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var ex = Assert.Throws<MusicSpinException>(() => OpenRead());
            Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, ex.Code);
            Assert.Contains("upgrade", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Chmod(Data.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void CreateLibrary()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        LibraryTestData.Write(Path.Combine(Music, "Bjork", "01 Jóga.flac"), LibraryTestData.Tiny16, "Jóga", "Björk", "Homogenic", "Björk", 1, 1, 1997, "Electronic");
        LibraryTestData.Write(Path.Combine(Music, "Bjork", "02 Bachelorette.flac"), LibraryTestData.Tiny16, "Bachelorette", "Björk", "Homogenic", "Björk", 2, 1, 1997, "Electronic");
        LibraryTestData.Write(Path.Combine(Music, "Beyonce", "01.flac"), LibraryTestData.Tiny16, "Crazy in Love", "Beyoncé", "Dangerously in Love", null, 1, 1, 2003, "R&B");
        AddAndScan();
    }

    private void DowngradeToSchema2() => LegacyLibrary.DowngradeToSchema2(Data);

    private static string?[] Titles(Page<TrackRecord> page) => [.. page.Items.Select(t => t.Title)];
}
