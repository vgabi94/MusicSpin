using MusicSpin.Core.Normalization;

namespace MusicSpin.Tests.Normalization;

public sealed class GainCalculatorTests
{
    private static NormalizationSettings Auto(double target = -18, double preamp = 0) => new(NormalizationMode.Auto, target, preamp);

    private static TrackLoudness Tagged(double? track = null, double? trackPeak = null, double? album = null, double? albumPeak = null)
        => new(new ReplayGainTags(track, trackPeak, album, albumPeak));

    [Fact]
    public void Off_never_changes_the_audio_whatever_is_known()
    {
        var gain = GainCalculator.Compute(NormalizationSettings.Disabled, Tagged(track: -9, trackPeak: 0.2), albumContext: true);
        Assert.Equal((1.0, 0.0, GainSource.Off), (gain.Linear, gain.Db, gain.Source));
        Assert.Equal("off", gain.SourceName);
    }

    [Fact]
    public void A_track_nobody_measured_yet_plays_unchanged()
    {
        Assert.Equal(GainSource.Unknown, GainCalculator.Compute(Auto(), null, false).Source);
        var noData = GainCalculator.Compute(Auto(), new TrackLoudness(ReplayGainTags.None), false);
        Assert.Equal((1.0, GainSource.Unknown), (noData.Linear, noData.Source));
        Assert.Equal("unknown", noData.SourceName);
    }

    [Fact]
    public void A_track_gain_tag_is_used_as_it_is_at_the_replaygain_reference()
    {
        var gain = GainCalculator.Compute(Auto(), Tagged(track: -6.0, trackPeak: 0.9), false);
        Assert.Equal(-6.0, gain.Db, 9);
        Assert.Equal(Math.Pow(10, -6.0 / 20), gain.Linear, 9);
        Assert.Equal(("track-tag", false), (gain.SourceName, gain.Limited));
    }

    [Fact]
    public void A_different_target_moves_the_tag_gain_by_the_difference()
    {
        var gain = GainCalculator.Compute(Auto(target: -23), Tagged(track: -6.0, trackPeak: 0.5), false);
        Assert.Equal(-11.0, gain.Db, 9);
    }

    [Fact]
    public void The_preamp_is_added_on_top()
    {
        var gain = GainCalculator.Compute(Auto(preamp: -3), Tagged(track: -6.0, trackPeak: 0.5), false);
        Assert.Equal(-9.0, gain.Db, 9);
    }

    [Fact]
    public void An_analysis_brings_the_measured_loudness_to_the_target()
    {
        var gain = GainCalculator.Compute(Auto(target: -18), new TrackLoudness(ReplayGainTags.None, -24.0, 0.25, Analyzed: true), false);
        Assert.Equal((6.0, GainSource.Analysis, false), (Math.Round(gain.Db, 9), gain.Source, gain.Limited));
        Assert.Equal("analysis", gain.SourceName);
    }

    [Fact]
    public void Tags_win_over_the_analysis()
    {
        var loudness = new TrackLoudness(new ReplayGainTags(-2, 0.99, null, null), -30, 0.1, Analyzed: true);
        var gain = GainCalculator.Compute(Auto(), loudness, false);
        Assert.Equal((-2.0, GainSource.TrackTag), (gain.Db, gain.Source));
    }

    [Fact]
    public void Boosting_stops_where_the_peak_would_clip()
    {
        // +10 dB wanted, but the peak is 0.5 (-6.02 dBFS): only 6.02 dB fit below full scale.
        var gain = GainCalculator.Compute(Auto(), Tagged(track: 10, trackPeak: 0.5), false);
        Assert.True(gain.Limited);
        Assert.Equal(-20 * Math.Log10(0.5), gain.Db, 9);
        Assert.Equal(1.0, 0.5 * gain.Linear, 9); // the peak ends exactly at full scale
    }

    [Fact]
    public void Clipping_prevention_also_holds_with_a_preamp()
    {
        var gain = GainCalculator.Compute(Auto(preamp: 12), Tagged(track: 0, trackPeak: 0.8), false);
        Assert.True(gain.Limited);
        Assert.True(0.8 * gain.Linear <= 1.0 + 1e-9);
    }

    [Fact]
    public void A_cut_is_never_limited()
    {
        var gain = GainCalculator.Compute(Auto(), Tagged(track: -8, trackPeak: 1.5), false);
        Assert.False(gain.Limited);
        Assert.Equal(-8.0, gain.Db, 9);
    }

    [Fact]
    public void A_tagged_track_without_a_peak_is_not_boosted_above_unity()
    {
        var boost = GainCalculator.Compute(Auto(), Tagged(track: 5), false);
        Assert.Equal((1.0, 0.0, true), (boost.Linear, boost.Db, boost.Limited));
        Assert.Equal(-4.0, GainCalculator.Compute(Auto(), Tagged(track: -4), false).Db, 9);
    }

    [Fact]
    public void A_tagged_track_without_a_peak_uses_the_analysed_peak_when_there_is_one()
    {
        var loudness = new TrackLoudness(new ReplayGainTags(10, null, null, null), -23, 0.5, Analyzed: true);
        Assert.Equal(-20 * Math.Log10(0.5), GainCalculator.Compute(Auto(), loudness, false).Db, 9);
    }

    [Fact]
    public void The_gain_stays_between_the_cut_and_boost_limits()
    {
        var quiet = new TrackLoudness(ReplayGainTags.None, -80, 0.0001, Analyzed: true);
        var boosted = GainCalculator.Compute(Auto(preamp: 12), quiet, false);
        Assert.Equal(GainCalculator.MaxBoostDb, boosted.Db, 9);

        var loud = new TrackLoudness(ReplayGainTags.None, 40, 1.0, Analyzed: true);
        var cut = GainCalculator.Compute(Auto(preamp: -12), loud, false);
        Assert.Equal(GainCalculator.MaxCutDb, cut.Db, 9);
    }

    [Fact]
    public void A_gain_of_a_few_thousandths_of_a_decibel_is_exactly_unity_so_the_audio_stays_bit_exact()
    {
        var gain = GainCalculator.Compute(Auto(), Tagged(track: -0.004, trackPeak: 1.0), false);
        Assert.Equal((1.0, 0.0), (gain.Linear, gain.Db));
        Assert.Equal(GainSource.TrackTag, gain.Source);
    }

    [Fact]
    public void Album_gain_is_used_for_an_album_in_auto_mode_and_always_in_album_mode()
    {
        var loudness = Tagged(track: -4, trackPeak: 0.9, album: -7, albumPeak: 0.95);
        Assert.Equal((-7.0, GainSource.AlbumTag), (GainCalculator.Compute(Auto(), loudness, albumContext: true).Db, GainCalculator.Compute(Auto(), loudness, true).Source));
        Assert.Equal((-4.0, GainSource.TrackTag), (GainCalculator.Compute(Auto(), loudness, albumContext: false).Db, GainCalculator.Compute(Auto(), loudness, false).Source));
        var albumMode = new NormalizationSettings(NormalizationMode.Album);
        Assert.Equal(-7.0, GainCalculator.Compute(albumMode, loudness, albumContext: false).Db, 9);
        var trackMode = new NormalizationSettings(NormalizationMode.Track);
        Assert.Equal(-4.0, GainCalculator.Compute(trackMode, loudness, albumContext: true).Db, 9);
    }

    [Fact]
    public void Without_an_album_gain_an_album_track_uses_its_own_gain()
    {
        var gain = GainCalculator.Compute(new NormalizationSettings(NormalizationMode.Album), Tagged(track: -4, trackPeak: 0.9), albumContext: true);
        Assert.Equal((-4.0, GainSource.TrackTag), (gain.Db, gain.Source));
    }

    [Fact]
    public void Out_of_range_settings_are_clamped_before_they_are_used()
    {
        var gain = GainCalculator.Compute(new NormalizationSettings(NormalizationMode.Track, -99, 99), Tagged(track: -20, trackPeak: 0.01), false);
        // target clamps to -30 (12 dB below -18) and the preamp to +12: -20 - 12 + 12 = -20.
        Assert.Equal(-20.0, gain.Db, 9);
    }

    [Fact]
    public void Tracks_of_one_album_play_as_an_album_only_in_order_and_with_a_neighbour_of_the_same_album()
    {
        (string?, string?) a = ("Kind of Blue", "Miles Davis");
        (string?, string?) b = ("kind of blue", "MILES DAVIS");
        (string?, string?) other = ("Bitches Brew", "Miles Davis");
        (string?, string?) sameNameOtherArtist = ("Kind of Blue", "Someone Else");

        Assert.True(GainCalculator.IsAlbumContext(a, null, b, false, false));
        Assert.True(GainCalculator.IsAlbumContext(a, b, null, false, false));
        Assert.False(GainCalculator.IsAlbumContext(a, null, null, false, false)); // a single track
        Assert.False(GainCalculator.IsAlbumContext(a, other, other, false, false));
        Assert.False(GainCalculator.IsAlbumContext(a, sameNameOtherArtist, sameNameOtherArtist, false, false));
        Assert.False(GainCalculator.IsAlbumContext(a, b, b, shuffled: true, radio: false));
        Assert.False(GainCalculator.IsAlbumContext(a, b, b, shuffled: false, radio: true));
        Assert.False(GainCalculator.IsAlbumContext((null, null), (null, null), (null, null), false, false)); // no album at all
        Assert.False(GainCalculator.IsAlbumContext(("  ", null), ("  ", null), null, false, false));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Missing_settings_are_refused()
        => Assert.Throws<ArgumentNullException>(() => GainCalculator.Compute(null!, null, false));
}
