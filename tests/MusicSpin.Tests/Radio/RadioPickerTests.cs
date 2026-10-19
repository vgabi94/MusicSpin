using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;

namespace MusicSpin.Tests.Radio;

public sealed class RadioPickerTests
{
    internal static RadioCandidate C(int i, string artist = "a", string album = "x")
        => new($"id{i}", $"/music/radio/{i}.flac", artist, $"{artist}/{album}", $"Track {i}", artist, album);

    internal static List<RadioCandidate> Pool(int count, int artists = 1, int albumsPerArtist = 1)
        => [.. Enumerable.Range(0, count).Select(i => C(i, $"artist{i % artists}", $"album{i / artists % albumsPerArtist}"))];

    private static List<RadioCandidate> PickMany(IReadOnlyList<RadioCandidate> pool, int count, int seed = 1, bool preferFavorites = false)
    {
        var history = new RadioPicker.History();
        var random = new Random(seed);
        var all = new List<RadioCandidate>();
        while (all.Count < count)
        {
            all.AddRange(RadioPicker.Pick(pool, Math.Min(20, count - all.Count), history, random, preferFavorites)); // top-ups of 20, like the player
        }

        return all;
    }

    private static int ShortestRepeatGap(List<RadioCandidate> picks)
    {
        var last = new Dictionary<string, int>();
        var gap = int.MaxValue;
        for (var i = 0; i < picks.Count; i++)
        {
            if (last.TryGetValue(picks[i].TrackId, out var j))
            {
                gap = Math.Min(gap, i - j);
            }

            last[picks[i].TrackId] = i;
        }

        return gap;
    }

    [Fact]
    public void No_track_repeats_within_the_last_100_picks_on_a_big_pool()
    {
        var picks = PickMany(Pool(150, artists: 30, albumsPerArtist: 2), 2000);

        Assert.True(ShortestRepeatGap(picks) > RadioPicker.RepeatWindow, $"gap {ShortestRepeatGap(picks)}");
        Assert.Equal(150, picks.Select(p => p.TrackId).Distinct().Count()); // and every track comes round
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(37)]
    public void Small_pools_clamp_the_window_to_pool_size_minus_one(int size)
    {
        var picks = PickMany(Pool(size, artists: size), 300);

        Assert.Equal(size, ShortestRepeatGap(picks)); // every track exactly once per round of `size`
    }

    [Fact]
    public void A_pool_of_one_track_replays_it()
    {
        var picks = PickMany(Pool(1), 10);

        Assert.All(picks, p => Assert.Equal("id0", p.TrackId));
    }

    /// <summary>A pool of <paramref name="count"/> tracks (distinct artists and albums) where every <paramref name="every"/>th one is a favorite.</summary>
    private static List<RadioCandidate> FavoritePool(int count, int every)
        => [.. Pool(count, artists: count).Select((c, i) => c with { IsFavorite = i % every == 0 })];

    private static double FavoriteShare(List<RadioCandidate> picks) => picks.Count(p => p.IsFavorite) / (double)picks.Count;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void Preferring_favorites_picks_them_about_three_times_as_often(int seed)
    {
        // 1 track in 10 is a favorite: 10% of plain picks; weighted 3:1 that is 3 / (3 + 9) = 25%.
        var pool = FavoritePool(1000, every: 10);

        var plain = FavoriteShare(PickMany(pool, 4000, seed));
        var preferred = FavoriteShare(PickMany(pool, 4000, seed, preferFavorites: true));

        Assert.InRange(plain, 0.07, 0.13);
        Assert.InRange(preferred, 0.21, 0.29);
    }

    [Fact]
    public void Preferring_favorites_still_never_repeats_within_the_window_or_plays_an_artist_twice_in_a_row()
    {
        // Every favorite is by the same artist: weighting must not break the artist spacing or the no-repeat window.
        var pool = Pool(300, artists: 30).Select(c => c with { IsFavorite = c.ArtistKey == "artist0" }).ToList();

        var picks = PickMany(pool, 3000, preferFavorites: true);

        Assert.True(ShortestRepeatGap(picks) > RadioPicker.RepeatWindow, $"gap {ShortestRepeatGap(picks)}");
        Assert.All(picks.Zip(picks.Skip(1)), p => Assert.NotEqual(p.First.ArtistKey, p.Second.ArtistKey));
        Assert.True(FavoriteShare(picks) > 1 / 30.0); // still favored as far as the spacing allows
    }

    [Fact]
    public void The_fallback_scan_is_weighted_too()
    {
        // 10,000 tracks by the artist just played, and 10 by others (5 of them favorites): the probes almost always hit
        // the artist rule, so the scan of the eligible tracks chooses. Weighted, a favorite is 15 / 20 = 75% likely.
        var pool = Enumerable.Range(0, 10_000).Select(i => C(i, "same")).Concat(
            Enumerable.Range(0, 10).Select(i => C(10_000 + i, $"other{i}") with { IsFavorite = i < 5 })).ToList();
        double Share(bool prefer)
        {
            var random = new Random(11);
            var favorites = 0;
            for (var n = 0; n < 2000; n++)
            {
                var pick = Assert.Single(RadioPicker.Pick(pool, 1, new RadioPicker.History(lastArtist: "same"), random, prefer));
                Assert.NotEqual("same", pick.ArtistKey);
                favorites += pick.IsFavorite ? 1 : 0;
            }

            return favorites / 2000.0;
        }

        Assert.InRange(Share(prefer: false), 0.42, 0.58);
        Assert.InRange(Share(prefer: true), 0.69, 0.81);
    }

    [Fact]
    public void Round_robin_pools_stay_round_robin_when_preferring_favorites()
    {
        // Ten tracks: the window (pool size - 1) forces each once per round, so there is nothing to weight.
        var pool = Pool(10, artists: 2).Select(c => c with { IsFavorite = c.ArtistKey == "artist0" }).ToList();
        var picks = PickMany(pool, 2000, preferFavorites: true);

        Assert.Equal(10, ShortestRepeatGap(picks));
        Assert.InRange(FavoriteShare(picks), 0.49, 0.51);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Preferring_favorites_without_any_in_the_pool_picks_exactly_as_before()
    {
        var pool = Pool(200, artists: 20, albumsPerArtist: 2);

        var plain = PickMany(pool, 500, seed: 5).Select(p => p.TrackId);
        var preferred = PickMany(pool, 500, seed: 5, preferFavorites: true).Select(p => p.TrackId);

        Assert.Equal(plain, preferred); // no extra random draws, so the same seed gives the same station
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_all_favorites_pool_is_not_skewed()
    {
        var pool = FavoritePool(200, every: 1);

        var picks = PickMany(pool, 2000, seed: 3, preferFavorites: true);

        Assert.True(ShortestRepeatGap(picks) > RadioPicker.RepeatWindow);
        Assert.Equal(200, picks.Select(p => p.TrackId).Distinct().Count());
    }

    [Fact]
    public void An_empty_pool_picks_nothing()
        => Assert.Empty(RadioPicker.Pick([], 20, new RadioPicker.History(), new Random(1)));

    [Fact]
    public void The_same_artist_never_plays_twice_in_a_row_when_others_exist()
    {
        var picks = PickMany(Pool(60, artists: 3, albumsPerArtist: 2), 1000);

        for (var i = 1; i < picks.Count; i++)
        {
            Assert.NotEqual(picks[i - 1].ArtistKey, picks[i].ArtistKey);
        }
    }

    [Fact]
    public void A_single_artist_pool_still_spaces_albums()
    {
        var picks = PickMany(Pool(30, artists: 1, albumsPerArtist: 3), 600);

        Assert.All(picks, p => Assert.Equal("artist0", p.ArtistKey));
        for (var i = 1; i < picks.Count; i++)
        {
            Assert.NotEqual(picks[i - 1].AlbumId, picks[i].AlbumId);
        }
    }

    [Fact]
    public void A_single_album_pool_plays_on_without_repeats_inside_the_window()
    {
        var picks = PickMany(Pool(8), 200);

        Assert.Equal(8, ShortestRepeatGap(picks));
    }

    [Fact]
    public void Spacing_gives_way_to_the_no_repeat_rule_when_only_one_track_is_fresh()
    {
        // 2 tracks by A, 1 by B: after A1, B, the only fresh track inside the window (2) is A2... then A1 again.
        var pool = new List<RadioCandidate> { C(1, "A", "1"), C(2, "A", "2"), C(3, "B", "3") };

        var picks = PickMany(pool, 300, seed: 7);

        Assert.Equal(3, ShortestRepeatGap(picks));
    }

    [Fact]
    public void History_carries_over_so_a_restored_station_keeps_its_rules()
    {
        var pool = Pool(10, artists: 10);
        var history = new RadioPicker.History(["id0", "id1", "id2", "id3", "id4", "id5", "id6", "id7", "id8"], "artist8", "artist8/album0");

        var pick = Assert.Single(RadioPicker.Pick(pool, 1, history, new Random(3)));

        Assert.Equal("id9", pick.TrackId); // the only track not heard within the last 9
        Assert.Equal(["id0", "id1", "id2", "id3", "id4", "id5", "id6", "id7", "id8", "id9"], history.Recent);
        Assert.Equal(("artist9", "artist9/album0"), (history.LastArtist, history.LastAlbum));
    }

    [Fact]
    public void History_keeps_only_the_window()
    {
        var history = new RadioPicker.History(Enumerable.Range(0, 250).Select(i => $"id{i}"));

        Assert.Equal(RadioPicker.RepeatWindow, history.Recent.Count);
        Assert.Equal("id150", history.Recent[0]);
        Assert.True(history.WithinLast("id249", 1));
        Assert.False(history.WithinLast("id150", 99));
        Assert.True(history.WithinLast("id150", 100));
        Assert.False(history.WithinLast("id0", 100));
    }

    [Fact]
    public void Same_seed_same_station()
        => Assert.Equal(PickMany(Pool(500, 40), 100, seed: 42), PickMany(Pool(500, 40), 100, seed: 42));

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => RadioPicker.Pick(null!, 1, new RadioPicker.History(), new Random()));
        Assert.Throws<ArgumentNullException>(() => RadioPicker.Pick([], 1, null!, new Random()));
        Assert.Throws<ArgumentNullException>(() => RadioPicker.Pick([], 1, new RadioPicker.History(), null!));
    }
}

public sealed class RadioFilterTests
{
    [Theory]
    [InlineData("1959", 1959, 1959)]
    [InlineData(" 1990-1999 ", 1990, 1999)]
    [InlineData("1990 - 1999", 1990, 1999)]
    [InlineData("0-9999", 0, 9999)]
    [InlineData("2001-2001", 2001, 2001)]
    public void Year_ranges_parse(string text, int from, int to)
    {
        var range = YearRange.TryParse(text, out var error);

        Assert.Null(error);
        Assert.Equal(new YearRange(from, to), range);
    }

    [Theory]
    [InlineData("1999-1990", "backwards")]
    [InlineData("10000", "between 0 and 9999")]
    [InlineData("-5", "Invalid year range")]
    [InlineData("1990-", "Invalid year range")]
    [InlineData("nineties", "Invalid year range")]
    [InlineData("", "Invalid year range")]
    [InlineData(null, "Invalid year range")]
    [InlineData("1990-1995-1999", "Invalid year range")]
    public void Bad_year_ranges_say_why(string? text, string reason)
    {
        Assert.Null(YearRange.TryParse(text, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Year_range_formatting_and_membership()
    {
        Assert.Equal("1959", new YearRange(1959, 1959).ToString());
        Assert.Equal("1990-1999", new YearRange(1990, 1999).ToString());
        Assert.True(new YearRange(1990, 1999).Contains(1990));
        Assert.True(new YearRange(1990, 1999).Contains(1999));
        Assert.False(new YearRange(1990, 1999).Contains(2000));
        Assert.False(new YearRange(1990, 1999).Contains(null));
    }

    [Fact]
    public void Filters_validate_their_values()
    {
        Assert.Null(new RadioFilter().Validate());
        Assert.Contains("--artist values must not be empty", new RadioFilter { Artists = [" "] }.Validate(), StringComparison.Ordinal);
        Assert.Contains("At most 50 --genre", new RadioFilter { Genres = [.. Enumerable.Repeat("x", 51)] }.Validate(), StringComparison.Ordinal);
        Assert.Contains("backwards", new RadioFilter { Years = [new YearRange(2000, 1990)] }.Validate(), StringComparison.Ordinal);
        Assert.Contains("must not be null", new RadioFilter { Years = [null!] }.Validate(), StringComparison.Ordinal);
        Assert.Contains("At most 50 year", new RadioFilter { Years = [.. Enumerable.Repeat(new YearRange(1, 1), 51)] }.Validate(), StringComparison.Ordinal);
        var ex = Assert.Throws<MusicSpinException>(() => new RadioFilter { Searches = [""] }.EnsureValid());
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Filters_describe_themselves()
    {
        Assert.True(new RadioFilter().IsEmpty);
        Assert.Equal("whole library", new RadioFilter().Describe());
        var filter = new RadioFilter
        {
            Artists = ["Miles Davis", "Coltrane"], Genres = ["Jazz"], Years = [new YearRange(1955, 1965), new YearRange(1970, 1970)],
            Albums = ["Kind of Blue"], Formats = ["flac"], Searches = ["blue"], HiRes = true,
        };
        Assert.False(filter.IsEmpty);
        Assert.Equal("artist Miles Davis or Coltrane · album Kind of Blue · genre Jazz · year 1955-1965 or 1970 · format flac · search \"blue\" · hi-res",
            filter.Describe());
        Assert.False(new RadioFilter { HiRes = true }.IsEmpty);
    }
}
