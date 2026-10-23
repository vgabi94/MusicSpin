using MusicSpin.Core.Normalization;
using MusicSpin.Core.Settings;

namespace MusicSpin.Tests.Normalization;

public sealed class NormalizationSettingsTests
{
    [Fact]
    public void Levelling_is_on_by_default_in_auto_mode_at_the_replaygain_reference()
    {
        var defaults = new AppSettings();
        Assert.Equal("auto", defaults.Normalization);
        Assert.Equal(-18, defaults.NormalizationTargetLufs);
        Assert.Equal(0, defaults.NormalizationPreampDb);
        Assert.Equal(NormalizationSettings.Default, NormalizationSettings.From(defaults));
        Assert.True(NormalizationSettings.Default.IsOn);
        Assert.False(NormalizationSettings.Disabled.IsOn);
    }

    [Theory]
    [InlineData("off", NormalizationMode.Off)]
    [InlineData("OFF", NormalizationMode.Off)]
    [InlineData("false", NormalizationMode.Off)]
    [InlineData("none", NormalizationMode.Off)]
    [InlineData("track", NormalizationMode.Track)]
    [InlineData(" Album ", NormalizationMode.Album)]
    [InlineData("auto", NormalizationMode.Auto)]
    [InlineData("on", NormalizationMode.Auto)]
    public void Mode_text_is_understood_in_any_case(string text, NormalizationMode expected)
    {
        Assert.True(NormalizationSettings.TryParseMode(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("loud")]
    [InlineData("tracks")]
    public void Unknown_mode_text_is_refused_and_settings_fall_back_to_auto(string? text)
    {
        Assert.False(NormalizationSettings.TryParseMode(text, out _));
        Assert.Equal(NormalizationMode.Auto, NormalizationSettings.ModeOrDefault(text));
    }

    [Fact]
    public void Out_of_range_values_in_settings_json_are_brought_back_into_range()
    {
        var from = NormalizationSettings.From(new AppSettings { Normalization = "track", NormalizationTargetLufs = -99, NormalizationPreampDb = 40 });
        Assert.Equal(new NormalizationSettings(NormalizationMode.Track, NormalizationSettings.MinTargetLufs, NormalizationSettings.MaxPreampDb), from);

        var nan = NormalizationSettings.From(new AppSettings { NormalizationTargetLufs = double.NaN, NormalizationPreampDb = double.PositiveInfinity });
        Assert.Equal(NormalizationSettings.Default, nan);
    }

    [Fact]
    public void Settings_round_trip_through_the_settings_file_values()
    {
        var chosen = new NormalizationSettings(NormalizationMode.Album, -16, 1.5);
        var saved = chosen.ApplyTo(new AppSettings { Volume = 0.3 });
        Assert.Equal(("album", -16.0, 1.5, 0.3), (saved.Normalization, saved.NormalizationTargetLufs, saved.NormalizationPreampDb, saved.Volume));
        Assert.Equal(chosen, NormalizationSettings.From(saved));
    }
}
