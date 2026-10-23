using MusicSpin.Core.Normalization;

namespace MusicSpin.Tests.Normalization;

/// <summary>ReplayGain / R128 tag values as taggers write them: the usual forms, the odd ones, and rubbish.</summary>
public sealed class ReplayGainTagsTests
{
    [Theory]
    [InlineData("-6.54 dB", -6.54)]
    [InlineData("+3.2 dB", 3.2)]
    [InlineData("  -0.5DB ", -0.5)]
    [InlineData("-1,5 dB", -1.5)] // a locale's decimal comma
    [InlineData("0", 0)]
    [InlineData("60", 60)]
    public void Gain_text_becomes_decibels(string text, double expected)
        => Assert.Equal(expected, ReplayGainTags.ParseGain(text)!.Value, 6);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loud")]
    [InlineData("NaN dB")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("-60.5 dB")]
    [InlineData("1234 dB")]
    public void Unusable_gain_text_is_no_gain(string? text) => Assert.Null(ReplayGainTags.ParseGain(text));

    [Theory]
    [InlineData("0.988553", 0.988553)]
    [InlineData("1", 1)]
    [InlineData("1,25", 1.25)]
    [InlineData("16", 16)]
    public void Peak_text_becomes_a_linear_peak(string text, double expected)
        => Assert.Equal(expected, ReplayGainTags.ParsePeak(text)!.Value, 6);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("17")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void Unusable_peak_text_is_no_peak(string? text) => Assert.Null(ReplayGainTags.ParsePeak(text));

    [Theory]
    [InlineData("0", 5.0)] // -23 LUFS reference: 5 dB louder than the -18 one
    [InlineData("-256", 4.0)] // Q7.8: -1 dB
    [InlineData("-1280", 0.0)] // -5 dB: right at the -18 LUFS reference
    [InlineData("512", 7.0)]
    [InlineData(" -768 ", 2.0)]
    public void R128_gain_is_q7_8_relative_to_minus_23_and_returned_against_minus_18(string text, double expected)
        => Assert.Equal(expected, ReplayGainTags.ParseR128(text)!.Value, 6);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-5.5")] // not a Q7.8 integer
    [InlineData("abc")]
    [InlineData("40000")] // outside the 16-bit field
    [InlineData("-40000")]
    public void Unusable_r128_text_is_no_gain(string? text) => Assert.Null(ReplayGainTags.ParseR128(text));

    [Fact]
    public void ReplayGain_tags_win_over_r128_ones_and_each_value_stands_alone()
    {
        var both = ReplayGainTags.FromText("-6 dB", "0.9", "-3 dB", "0.8", r128Track: "0", r128Album: "0");
        Assert.Equal((-6.0, 0.9, -3.0, 0.8), (both.TrackGainDb, both.TrackPeak, both.AlbumGainDb, both.AlbumPeak));

        var opus = ReplayGainTags.FromText(null, null, null, null, r128Track: "-256", r128Album: "256");
        Assert.Equal((4.0, null, 6.0, null), (opus.TrackGainDb, opus.TrackPeak, opus.AlbumGainDb, opus.AlbumPeak));
        Assert.True(opus.HasAny);

        var broken = ReplayGainTags.FromText("loud", "-1", "NaN", "0", r128Track: "x");
        Assert.Equal(ReplayGainTags.None, broken);
        Assert.False(broken.HasAny);
    }

    [Fact]
    public void Validation_rejects_non_finite_and_out_of_range_numbers()
    {
        Assert.Null(ReplayGainTags.ValidGain(double.NaN));
        Assert.Null(ReplayGainTags.ValidGain(double.PositiveInfinity));
        Assert.Null(ReplayGainTags.ValidGain(61));
        Assert.Equal(-60, ReplayGainTags.ValidGain(-60));
        Assert.Null(ReplayGainTags.ValidPeak(0));
        Assert.Null(ReplayGainTags.ValidPeak(double.NaN));
        Assert.Null(ReplayGainTags.ValidPeak(16.5));
        Assert.Equal(1.0, ReplayGainTags.ValidPeak(1.0));
    }
}
