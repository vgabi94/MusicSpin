using MusicSpin.App.Services;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Ui;

/// <summary>Radio stations as the app shows them (#23, #24): the description, tag names, and tags to and from the player's filter.</summary>
public sealed class RadioStationTests
{
    private static RadioTag Genre(string v, bool x = false) => new(RadioTagKind.Genre, v, x);

    private static RadioTag Artist(string v, bool x = false) => new(RadioTagKind.Artist, v, x);

    private static RadioTag Format(string v, bool x = false) => new(RadioTagKind.Format, v, x);

    private static readonly RadioTag HiRes = new(RadioTagKind.HiRes);

    private static string Json(RadioFilter filter) => ProtocolJson.Serialize(filter);

    // ---- the description ---------------------------------------------------------------------------------------

    [Fact]
    public void Values_of_one_kind_read_with_or_and_kinds_in_a_fixed_order()
    {
        // Given in any order, the kinds read genre, decade, format, Hi-Res, artist, album, words.
        RadioTag[] tags =
        [
            new(RadioTagKind.Words, "live"), Artist("Kodo Arai"), new(RadioTagKind.Album, "a1"), HiRes, Format("flac"),
            RadioTag.Decade(1960), Genre("Jazz"), Genre("Ambient"),
        ];

        Assert.Equal("Jazz or Ambient · 1960s · FLAC · Hi-Res · Kodo Arai · Kind of Blue · “live”",
            RadioStation.Describe(tags, id => id == "a1" ? "Kind of Blue" : null));
    }

    [Fact]
    public void Left_out_values_follow_after_not()
    {
        Assert.Equal("Jazz or Ambient · 1960s · not Kodo Arai",
            RadioStation.Describe([Genre("Jazz"), Genre("Ambient"), RadioTag.Decade(1960), Artist("Kodo Arai", x: true)]));
        // Left out, kind by kind too: a format before an artist.
        Assert.Equal("Jazz · not MP3, Kodo Arai", RadioStation.Describe([Artist("Kodo Arai", x: true), Genre("Jazz"), Format("mp3", x: true)]));
    }

    [Fact]
    public void No_tags_is_the_whole_library()
    {
        Assert.Equal("Whole library", RadioStation.Describe([]));
        Assert.Equal("Whole library · not Christmas", RadioStation.Describe([Genre("Christmas", x: true)]));
    }

    [Fact]
    public void The_parts_mark_the_names_so_the_page_can_show_them_strong()
    {
        var parts = RadioStation.DescribeParts([Genre("Jazz"), Genre("Ambient"), Artist("Kodo Arai", x: true)]);

        Assert.Equal(
            [("Jazz", true), (" or ", false), ("Ambient", true), (" · not ", false), ("Kodo Arai", true)],
            parts.Select(p => (p.Text, p.IsValue)));
        Assert.Equal([("Whole library", false)], RadioStation.DescribeParts([]).Select(p => (p.Text, p.IsValue)));
    }

    [Theory]
    [InlineData(RadioTagKind.Years, "1960-1969", "1960s")]
    [InlineData(RadioTagKind.Years, "1959", "1959")]
    [InlineData(RadioTagKind.Years, "1990-1995", "1990–1995")]
    [InlineData(RadioTagKind.Format, "flac", "FLAC")]
    [InlineData(RadioTagKind.Format, "opus", "Opus")]
    [InlineData(RadioTagKind.Format, "dsd", "DSD")]
    [InlineData(RadioTagKind.HiRes, "", "Hi-Res")]
    [InlineData(RadioTagKind.Words, "live", "“live”")]
    [InlineData(RadioTagKind.Favorites, "", "Favorites")]
    [InlineData(RadioTagKind.Genre, "Jazz", "Jazz")]
    public void Tag_names(RadioTagKind kind, string value, string label)
        => Assert.Equal(label, RadioStation.Label(new RadioTag(kind, value)));

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_album_not_known_reads_as_its_value_and_a_year_that_isnt_one_as_written()
    {
        Assert.Equal("Kind of Blue", RadioStation.Label(new RadioTag(RadioTagKind.Album, "Kind of Blue"), _ => null));
        Assert.Equal("someday", RadioStation.Label(new RadioTag(RadioTagKind.Years, "someday")));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Hi_res_and_favorites_cant_be_left_out_so_such_tags_say_and_do_nothing()
    {
        RadioTag[] tags = [Genre("Jazz"), HiRes.WithExcluded(true), new RadioTag(RadioTagKind.Favorites, Excluded: true)];

        Assert.Equal("Jazz", RadioStation.Describe(tags));
        var filter = RadioStation.FilterOf(tags);
        Assert.False(filter.HiRes);
        Assert.False(filter.Favorites);
        Assert.False(filter.HasExcludes);
    }

    // ---- tags and filters --------------------------------------------------------------------------------------

    [Fact]
    public void The_filter_is_the_one_music_spin_radio_builds_from_the_same_options()
    {
        // music-spin radio --genre Jazz --genre Electronic --exclude-artist "Kodo Arai" --exclude-format mp3 --hires --years 1960-1969
        var cli = new RadioFilter
        {
            Genres = ["Jazz", "Electronic"],
            Years = [new YearRange(1960, 1969)],
            HiRes = true,
            ExcludeArtists = ["Kodo Arai"],
            ExcludeFormats = ["mp3"],
        };
        RadioTag[] tags = [Genre("Jazz"), Genre("Electronic"), Artist("Kodo Arai", x: true), Format("mp3", x: true), HiRes, RadioTag.Decade(1960)];

        Assert.Equal(Json(cli), Json(RadioStation.FilterOf(tags)));
        Assert.Equal(Json(new RadioFilter()), Json(RadioStation.FilterOf([])));
    }

    [Fact]
    public void Every_kind_goes_to_its_option_and_back()
    {
        RadioTag[] tags =
        [
            Genre("Jazz"), Genre("Pop", x: true), RadioTag.Decade(1970), new(RadioTagKind.Years, "1959", true), Format("flac"), Format("mp3", x: true), HiRes,
            Artist("Miles Davis"), Artist("Kenny G", x: true), new(RadioTagKind.Album, "a1"), new(RadioTagKind.Album, "a2", true),
            new(RadioTagKind.Words, "live"), new(RadioTagKind.Words, "remix", true), new(RadioTagKind.Favorites),
        ];

        var filter = RadioStation.FilterOf(tags);

        Assert.Equal(["Jazz"], filter.Genres!);
        Assert.Equal(["Pop"], filter.ExcludeGenres!);
        Assert.Equal([new YearRange(1970, 1979)], filter.Years!);
        Assert.Equal([new YearRange(1959, 1959)], filter.ExcludeYears!);
        Assert.Equal(["flac"], filter.Formats!);
        Assert.Equal(["mp3"], filter.ExcludeFormats!);
        Assert.Equal(["Miles Davis"], filter.Artists!);
        Assert.Equal(["Kenny G"], filter.ExcludeArtists!);
        Assert.Equal(["a1"], filter.Albums!);
        Assert.Equal(["a2"], filter.ExcludeAlbums!);
        Assert.Equal(["live"], filter.Searches!);
        Assert.Equal(["remix"], filter.ExcludeSearches!);
        Assert.True(filter.HiRes && filter.Favorites);
        Assert.True(RadioStation.Same(tags, RadioStation.TagsOf(filter)));
    }

    [Fact]
    public void The_same_station_in_any_order_and_any_case()
    {
        // As the player matches them: --genre jazz from the CLI is the app's Jazz.
        Assert.True(RadioStation.Same([Genre("Jazz"), Artist("Kodo Arai", x: true)], [Artist("KODO ARAI", x: true), Genre("jazz")]));
        Assert.True(RadioStation.Same([RadioTag.Decade(1960)], [new RadioTag(RadioTagKind.Years, " 1960 - 1969 ")]));
        Assert.True(RadioStation.Same([Format("FLAC")], [Format("flac")]));
        Assert.True(RadioStation.Same([], []));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Another_station_differs_by_one_tag_or_by_leaving_one_out()
    {
        Assert.False(RadioStation.Same([Genre("Jazz")], [Genre("Jazz", x: true)]));
        Assert.False(RadioStation.Same([Genre("Jazz")], [Genre("Jazz"), Genre("Pop")]));
        Assert.False(RadioStation.Same([Genre("Jazz")], [Artist("Jazz")]));
        Assert.False(RadioStation.Same([Genre("Jazz")], []));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_tag_given_twice_counts_once_and_no_filter_has_no_tags()
    {
        var filter = RadioStation.FilterOf([Genre("Jazz"), Genre("JAZZ"), HiRes, HiRes]);

        Assert.Equal(["Jazz"], filter.Genres!);
        Assert.Equal("Jazz · Hi-Res", RadioStation.Describe([Genre("Jazz"), Genre("jazz"), HiRes, HiRes]));
        Assert.Empty(RadioStation.TagsOf(null));
        Assert.Empty(RadioStation.TagsOf(new RadioFilter()));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_year_that_isnt_one_is_left_out_of_the_filter()
    {
        var filter = RadioStation.FilterOf([new RadioTag(RadioTagKind.Years, "someday"), Genre("Jazz")]);

        Assert.Null(filter.Years);
        Assert.Null(filter.Validate());
    }

    [Fact]
    public void A_station_the_cli_started_reads_in_the_apps_words()
    {
        // music-spin radio --genre Jazz --year 1959 --exclude-format mp3
        var cli = new RadioFilter { Genres = ["Jazz"], Years = [new YearRange(1959, 1959)], ExcludeFormats = ["MP3"] };

        Assert.Equal("Jazz · 1959 · not MP3", RadioStation.Describe(RadioStation.TagsOf(cli)));
        Assert.True(RadioStation.Find(RadioStation.TagsOf(cli), Format("mp3")) is { Excluded: true });
        Assert.Null(RadioStation.Find(RadioStation.TagsOf(cli), Format("flac")));
    }
}
