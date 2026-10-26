using MusicSpin.Core.Audio;
using MusicSpin.Core.Equalization;

namespace MusicSpin.Tests.Equalization;

/// <summary>The equalizer's filtering (#141): response, bit-exact bypass, click-free changes, clip safety, rates, bad input.</summary>
public sealed class EqualizerProcessorTests
{
    public static readonly TheoryData<int> Rates = new() { 44_100, 48_000, 96_000, 192_000 };

    private static double[] Gains(int band, double db)
    {
        var g = new double[EqualizerSettings.BandCount];
        g[band] = db;
        return g;
    }

    /// <summary>The response in dB at <paramref name="hz"/>, measured from the processor's impulse response.</summary>
    private static double Measure(GraphicEqualizer eq, int rate, double hz)
    {
        var length = rate * 2; // the 31 Hz band rings for a while
        var samples = new float[length];
        samples[0] = 1;
        new EqualizerProcessor(eq, new PcmFormat(rate, 1)).Process(samples, new PcmFormat(rate, 1));
        double re = 0, im = 0;
        var w = 2 * Math.PI * hz / rate;
        for (var n = 0; n < length; n++)
        {
            re += samples[n] * Math.Cos(w * n);
            im -= samples[n] * Math.Sin(w * n);
        }

        return 20 * Math.Log10(Math.Sqrt((re * re) + (im * im)));
    }

    private static double PreampDb(EqualizerSettings settings, int rate)
    {
        EqualizerDesign.Design(settings, rate, new Biquad[EqualizerSettings.BandCount], out var gain, out _);
        return 20 * Math.Log10(gain);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Every_band_reaches_its_gain_at_its_centre(int rate)
    {
        foreach (var db in new[] { -12.0, -6.0, 6.0, 12.0 })
        {
            for (var band = 0; band < EqualizerSettings.BandCount; band++)
            {
                var settings = new EqualizerSettings(true, Gains(band, db));
                var measured = Measure(new GraphicEqualizer(settings), rate, EqualizerSettings.Bands[band]);
                // Boosts come with automatic headroom (the pre-amp); the band itself must still be exactly db above it.
                Assert.InRange(measured - PreampDb(settings, rate), db - 0.1, db + 0.1);
                if (db < 0)
                {
                    Assert.InRange(measured, db - 0.1, db + 0.1); // a cut takes no headroom
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void A_preset_measures_as_designed_and_never_above_0_dB(int rate)
    {
        foreach (var preset in EqualizerSettings.BuiltInPresets)
        {
            var settings = new EqualizerSettings(true, preset.Gains, 0, preset.Name);
            var filters = new Biquad[EqualizerSettings.BandCount];
            var count = EqualizerDesign.Design(settings, rate, filters, out var gain, out _);
            var eq = new GraphicEqualizer(settings);
            foreach (var hz in EqualizerSettings.Bands.Append(100).Append(3_000))
            {
                var expected = EqualizerDesign.ResponseDb(filters.AsSpan(0, count), gain, hz, rate);
                var measured = Measure(eq, rate, hz);
                Assert.InRange(measured, expected - 0.1, expected + 0.1);
                Assert.True(measured <= 0.05, $"{preset.Name} at {hz} Hz: {measured:0.00} dB");
            }
        }
    }

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 0)]
    public void Off_or_flat_is_bit_exact(bool enabled, double gain)
    {
        var eq = new GraphicEqualizer(new EqualizerSettings(enabled, Enumerable.Repeat(gain, 10).ToArray()));
        var format = new PcmFormat(48_000, 2);
        var random = new Random(141);
        var input = Enumerable.Range(0, 9_600).Select(_ => (float)((random.NextDouble() * 2) - 1)).ToArray();
        var output = input.ToArray();

        new EqualizerProcessor(eq, format).Process(output, format);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Turning_it_off_fades_out_and_then_is_bit_exact_again()
    {
        var eq = new GraphicEqualizer(new EqualizerSettings(true, Gains(5, 6)));
        var format = new PcmFormat(48_000, 2);
        var processor = new EqualizerProcessor(eq, format);
        var block = new float[960];
        processor.Process(block, format);

        eq.Configure(eq.Settings with { Enabled = false });
        var fade = Enumerable.Repeat(0.25f, 48_000 * 2 * EqualizerProcessor.FadeMilliseconds / 1000).ToArray();
        processor.Process(fade, format);
        var after = Enumerable.Range(0, 400).Select(i => (float)Math.Sin(i * 0.1)).ToArray();
        var copy = after.ToArray();
        processor.Process(copy, format);

        Assert.Equal(after, copy);
        Assert.False(processor.Applied!.IsActive);
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void Boosts_never_push_a_full_scale_tone_past_full_scale(int rate)
    {
        var allUp = Enumerable.Repeat(EqualizerSettings.MaxGainDb, 10).ToArray();
        var settingsList = EqualizerSettings.BuiltInPresets.Select(p => new EqualizerSettings(true, p.Gains, 0))
            .Append(new EqualizerSettings(true, allUp, EqualizerSettings.MaxPreampDb))
            .Append(new EqualizerSettings(true, Gains(0, 12), 6));
        foreach (var settings in settingsList)
        {
            foreach (var hz in EqualizerSettings.Bands.Concat([45.0, 90, 700, 1_400, 11_000]).Where(f => f < 0.45 * rate))
            {
                var format = new PcmFormat(rate, 1);
                var processor = new EqualizerProcessor(new GraphicEqualizer(settings), format);
                var samples = Enumerable.Range(0, rate).Select(n => (float)Math.Sin(2 * Math.PI * hz * n / rate)).ToArray();
                processor.Process(samples, format);
                var settled = samples.AsSpan(rate / 2).ToArray().Max(Math.Abs);
                Assert.True(settled <= 1.0 + 1e-3, $"{settings} at {hz} Hz peaks at {settled}");
            }
        }
    }

    [Fact]
    public void Changing_a_band_while_playing_does_not_click()
    {
        const int rate = 48_000;
        var format = new PcmFormat(rate, 1);
        var eq = new GraphicEqualizer(new EqualizerSettings(true, Gains(2, 0), -1));
        var processor = new EqualizerProcessor(eq, format);
        var n = 0;
        float[] Block(int length) => Enumerable.Range(0, length).Select(_ => (float)(0.5 * Math.Sin(2 * Math.PI * 125 * n++ / rate))).ToArray();

        var steady = Block(4_800);
        processor.Process(steady, format);
        var last = steady[^1];
        double SteadyStep(float[] s) => s.Zip(s.Skip(1), (a, b) => Math.Abs(b - a)).Max();
        var normal = SteadyStep(steady);

        double worst = 0;
        foreach (var db in new[] { 12.0, -12.0, 12.0, 0.0 }) // big jumps, also one arriving mid-fade
        {
            eq.SetBand(2, db);
            var block = Block(480);
            processor.Process(block, format);
            worst = Math.Max(worst, Math.Max(Math.Abs(block[0] - last), SteadyStep(block)));
            last = block[^1];
        }

        var tail = Block(9_600);
        processor.Process(tail, format);
        worst = Math.Max(worst, Math.Max(Math.Abs(tail[0] - last), SteadyStep(tail)));

        // A 125 Hz tone at 0.5 moves at most ~0.008 per sample; +12 dB of boost (minus its headroom) can at most quadruple
        // that. A click (a jump of the filter state) would show as a step far above it.
        Assert.True(worst <= normal * 4.5, $"largest step {worst:0.0000} vs steady {normal:0.0000}");
        Assert.Equal(eq.Settings, processor.Applied); // every change was applied in the end
    }

    [Fact]
    public void A_new_rate_redesigns_the_filters()
    {
        var settings = new EqualizerSettings(true, Gains(9, -12));
        var eq = new GraphicEqualizer(settings);
        var processor = new EqualizerProcessor(eq, new PcmFormat(48_000, 1));
        processor.Process(new float[480], new PcmFormat(48_000, 1));

        var impulse = new float[96_000 * 2];
        impulse[0] = 1;
        processor.Process(impulse, new PcmFormat(96_000, 1));

        double re = 0, im = 0;
        var w = 2 * Math.PI * 16_000 / 96_000;
        for (var i = 0; i < impulse.Length; i++)
        {
            re += impulse[i] * Math.Cos(w * i);
            im -= impulse[i] * Math.Sin(w * i);
        }

        Assert.InRange(20 * Math.Log10(Math.Sqrt((re * re) + (im * im))), -12.1, -11.9); // 48 kHz coefficients would miss it badly
    }

    [Fact]
    public void Bands_above_the_rate_are_left_out_not_built_wrongly()
    {
        var count = EqualizerDesign.Design(new EqualizerSettings(true, Gains(9, 12)), 22_050, new Biquad[10], out var gain, out var headroom);
        Assert.Equal((0, 1.0, 0.0), (count, gain, headroom));
    }

    [Fact]
    public void A_corrupt_sample_is_silenced_and_the_filters_recover()
    {
        var eq = new GraphicEqualizer(new EqualizerSettings(true, Gains(0, 12)));
        var format = new PcmFormat(48_000, 2);
        var processor = new EqualizerProcessor(eq, format);
        var bad = Enumerable.Repeat(0.5f, 200).ToArray();
        bad[10] = float.NaN;
        bad[51] = float.PositiveInfinity;
        processor.Process(bad, format);
        Assert.All(bad, s => Assert.True(float.IsFinite(s)));

        var next = Enumerable.Range(0, 48_000).Select(i => (float)(0.5 * Math.Sin(i * 0.01))).ToArray();
        processor.Process(next, format);
        Assert.All(next, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1.001f));
    }

    [Fact]
    public void Silence_after_music_decays_to_exact_zero()
    {
        var eq = new GraphicEqualizer(new EqualizerSettings(true, EqualizerSettings.BuiltIn("loudness")!.Gains));
        var format = new PcmFormat(44_100, 2);
        var processor = new EqualizerProcessor(eq, format);
        processor.Process(Enumerable.Range(0, 4_410).Select(i => (float)Math.Sin(i * 0.05)).ToArray(), format);

        var silence = new float[44_100 * 2 * 4];
        processor.Process(silence, format);

        Assert.Equal(0f, silence[^1]);
        Assert.Equal(0f, silence[^2]);
    }
}
