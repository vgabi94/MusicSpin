using MusicSpin.Core.Equalization;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Equalization;

/// <summary>Equalizer settings, presets and their persistence (#141).</summary>
public sealed class GraphicEqualizerTests
{
    [Fact]
    public void Presets_apply_and_turn_it_on()
    {
        var eq = new GraphicEqualizer();
        var changes = 0;
        eq.Changed += (_, _) => changes++;

        eq.ApplyPreset("Bass Boost");

        Assert.True(eq.Settings.Enabled);
        Assert.Equal("bass-boost", eq.Settings.Preset);
        Assert.Equal(EqualizerSettings.BuiltIn("bass-boost")!.Gains, eq.Settings.BandGains);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void The_required_presets_are_built_in_and_in_range()
    {
        foreach (var name in new[] { "flat", "rock", "pop", "jazz", "classical", "bass-boost", "treble-boost", "vocal", "loudness" })
        {
            var preset = EqualizerSettings.BuiltIn(name);
            Assert.NotNull(preset);
            Assert.Null(EqualizerSettings.CheckGains(preset.Gains));
        }

        Assert.All(EqualizerSettings.BuiltIn("flat")!.Gains, g => Assert.Equal(0, g));
    }

    [Fact]
    public void Setting_a_band_makes_the_gains_custom_until_they_match_a_preset()
    {
        var eq = new GraphicEqualizer(new EqualizerSettings(true));
        eq.SetBand(4, -3);
        Assert.Null(eq.Settings.Preset);
        Assert.Equal(-3, eq.Settings.BandGains[4]);

        eq.SetBand(4, 0);
        Assert.Equal("flat", eq.Settings.Preset);
    }

    [Fact]
    public void User_presets_save_replace_and_delete()
    {
        var eq = new GraphicEqualizer(new EqualizerSettings(true));
        eq.SetBand(0, 4);
        eq.SavePreset("My Car");
        eq.SetBand(1, 2);
        eq.SavePreset("my car"); // same name: replaced

        Assert.Equal("my-car", eq.Settings.Preset);
        var saved = Assert.Single(eq.UserPresets);
        Assert.Equal(2, saved.Gains[1]);
        Assert.Contains(eq.Presets, p => p.Name == "rock");

        eq.DeletePreset("MY CAR");
        Assert.Empty(eq.UserPresets);
        Assert.Null(eq.Settings.Preset);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData(-1, 0)]
    [InlineData(10, 0)]
    [InlineData(0, 12.5)]
    [InlineData(0, -13)]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.PositiveInfinity)]
    public void A_bad_band_or_gain_is_refused_and_changes_nothing(int band, double db)
    {
        var eq = new GraphicEqualizer(new EqualizerSettings(true));
        var before = eq.Settings;

        Assert.ThrowsAny<ArgumentException>(() => eq.SetBand(band, db));

        Assert.Same(before, eq.Settings);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rock")]
    [InlineData("Bass_Boost")]
    [InlineData("a\u0001b")]
    [InlineData("an-extremely-long-preset-name-that-goes-on-and-on")]
    public void Bad_preset_names_cannot_be_saved(string name)
        => Assert.Throws<ArgumentException>(() => new GraphicEqualizer().SavePreset(name));

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Unknown_presets_are_refused()
    {
        var eq = new GraphicEqualizer();
        Assert.Throws<ArgumentException>(() => eq.ApplyPreset("dubstep"));
        Assert.Throws<ArgumentException>(() => eq.DeletePreset("dubstep"));
        Assert.Throws<ArgumentException>(() => eq.DeletePreset("rock"));
        Assert.False(eq.Settings.Enabled);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void There_is_a_limit_on_saved_presets()
    {
        var eq = new GraphicEqualizer();
        for (var i = 0; i < EqualizerSettings.MaxUserPresets; i++)
        {
            eq.SavePreset($"p{i}");
        }

        Assert.Throws<ArgumentException>(() => eq.SavePreset("one-too-many"));
        eq.SavePreset("p0"); // replacing is still fine
    }

    [Theory]
    [InlineData("1", 0)]
    [InlineData("10", 9)]
    [InlineData("31", 0)]
    [InlineData("31.25", 0)]
    [InlineData("62.5", 1)]
    [InlineData("1k", 5)]
    [InlineData("1kHz", 5)]
    [InlineData("1000", 5)]
    [InlineData("16k", 9)]
    [InlineData("16000", 9)]
    [InlineData(" 4K ", 7)]
    public void Bands_are_named_by_number_or_frequency(string text, int band)
    {
        Assert.True(EqualizerSettings.TryParseBand(text, out var parsed));
        Assert.Equal(band, parsed);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("700")]
    [InlineData("50k")]
    [InlineData("bass")]
    [InlineData("NaN")]
    public void Other_band_names_are_refused(string? text) => Assert.False(EqualizerSettings.TryParseBand(text, out _));

    [Fact]
    public void Settings_round_trip_through_settings_json()
    {
        var dir = new DataDirectory(TempDir.Create("eq"), DataDirectorySource.Override);
        var store = new SettingsStore(dir);
        var eq = new GraphicEqualizer(new EqualizerSettings(true, new double[10]));
        eq.ApplyPreset("rock");
        eq.SetBand(3, 5.5);
        eq.SavePreset("Mine");

        store.Save(eq.Settings.ApplyTo(store.Load(), eq.UserPresets));
        var loaded = new SettingsStore(dir).Load();
        var problems = new List<string>();
        var again = new GraphicEqualizer(EqualizerSettings.From(loaded), EqualizerSettings.UserPresets(loaded, problems));

        Assert.Empty(problems);
        Assert.Equal(eq.Settings.ToString(), again.Settings.ToString());
        Assert.Equal(eq.UserPresets.Single().Gains, again.UserPresets.Single().Gains);
        Assert.Equal("mine", again.Settings.Preset);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Corrupt_settings_are_brought_into_range_and_corrupt_presets_reported()
    {
        var settings = new AppSettings
        {
            EqualizerEnabled = true,
            EqualizerGains = [99, -99, double.NaN, 0, 0, 0, 0, 0, 0, 0],
            EqualizerPreampDb = double.PositiveInfinity,
            EqualizerPresets = new()
            {
                ["ok"] = new double[10],
                ["short"] = [1, 2],
                ["loud"] = [40, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                ["rock"] = new double[10],
                ["OK"] = new double[10],
            },
        };

        var eq = EqualizerSettings.From(settings);
        var problems = new List<string>();
        var presets = EqualizerSettings.UserPresets(settings, problems);

        Assert.Equal([12, -12, 0, 0, 0, 0, 0, 0, 0, 0], eq.BandGains);
        Assert.Equal(0, eq.PreampDb);
        Assert.Equal("ok", Assert.Single(presets).Name);
        Assert.Equal(4, problems.Count);

        var wrongLength = EqualizerSettings.From(new AppSettings { EqualizerEnabled = true, EqualizerGains = [3] });
        Assert.False(wrongLength.IsActive); // flat: plays untouched
    }
}
