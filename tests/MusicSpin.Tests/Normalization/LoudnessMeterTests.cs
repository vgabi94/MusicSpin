using MusicSpin.Core.Normalization;

namespace MusicSpin.Tests.Normalization;

/// <summary>The EBU R128 / BS.1770-4 meter against signals whose loudness is known (EBU Tech 3341 style: a sine's level is its loudness).</summary>
public sealed class LoudnessMeterTests
{
    private static float[] Sine(int sampleRate, int channels, double seconds, double dbfs, double hz = 997, double phase = 0)
    {
        var frames = (int)(seconds * sampleRate);
        var amplitude = Math.Pow(10, dbfs / 20);
        var data = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var v = (float)(amplitude * Math.Sin((2 * Math.PI * hz * f / sampleRate) + phase));
            for (var c = 0; c < channels; c++)
            {
                data[(f * channels) + c] = v;
            }
        }

        return data;
    }

    private static LoudnessResult Measure(int sampleRate, int channels, params float[][] parts)
    {
        var meter = new LoudnessMeter(sampleRate, channels);
        foreach (var part in parts)
        {
            meter.Add(part);
        }

        return meter.Finish();
    }

    [Fact]
    public void A_stereo_sine_at_minus_23_dbfs_reads_minus_23_lufs()
    {
        var result = Measure(48_000, 2, Sine(48_000, 2, 20, -23));
        Assert.InRange(result.IntegratedLufs!.Value, -23.1, -22.9);
        Assert.Equal(20, result.DurationSeconds, 3);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(-30)]
    [InlineData(-45)]
    public void The_reading_follows_the_level_one_to_one(double dbfs)
    {
        var result = Measure(48_000, 2, Sine(48_000, 2, 10, dbfs));
        Assert.InRange(result.IntegratedLufs!.Value, dbfs - 0.1, dbfs + 0.1);
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    public void The_reading_does_not_depend_on_the_sample_rate(int rate)
    {
        var result = Measure(rate, 2, Sine(rate, 2, 10, -23));
        Assert.InRange(result.IntegratedLufs!.Value, -23.15, -22.85);
    }

    [Fact]
    public void A_mono_file_reads_like_the_same_signal_on_both_speakers()
    {
        // BS.1770 / libebur128 (EBUR128_DUAL_MONO): mono plays on both speakers, so it is counted twice.
        var mono = Measure(48_000, 1, Sine(48_000, 1, 10, -23));
        var stereo = Measure(48_000, 2, Sine(48_000, 2, 10, -23));
        Assert.InRange(mono.IntegratedLufs!.Value, -23.1, -22.9);
        Assert.Equal(stereo.IntegratedLufs!.Value, mono.IntegratedLufs.Value, 3);
    }

    [Fact]
    public void One_channel_of_a_stereo_file_is_three_decibels_quieter_than_two()
    {
        var both = Sine(48_000, 2, 10, -23);
        var left = (float[])both.Clone();
        for (var i = 1; i < left.Length; i += 2)
        {
            left[i] = 0;
        }

        Assert.InRange(Measure(48_000, 2, left).IntegratedLufs!.Value, -26.15, -25.85);
    }

    [Theory]
    [InlineData(1, null, new[] { 2.0 })]
    [InlineData(1, "mono", new[] { 2.0 })]
    [InlineData(2, "stereo", new[] { 1.0, 1.0 })]
    [InlineData(2, null, new[] { 1.0, 1.0 })]
    [InlineData(4, null, new[] { 1.0, 1.0, S, S })] // by count: quad
    [InlineData(4, "quad", new[] { 1.0, 1.0, S, S })]
    [InlineData(4, "4.0", new[] { 1.0, 1.0, 1.0, 1.0 })] // L R C Cs: no surround pair, not a quad
    [InlineData(6, null, new[] { 1.0, 1.0, 1.0, 0.0, S, S })]
    [InlineData(6, "5.1", new[] { 1.0, 1.0, 1.0, 0.0, S, S })]
    [InlineData(6, "5.1(side)", new[] { 1.0, 1.0, 1.0, 0.0, S, S })]
    [InlineData(6, "6.0", new[] { 1.0, 1.0, 1.0, 1.0, S, S })] // no LFE: nothing is dropped
    [InlineData(3, "FL+FR+LFE", new[] { 1.0, 1.0, 0.0 })]
    [InlineData(3, "3 channels (FL+FR+LFE)", new[] { 1.0, 1.0, 0.0 })]
    [InlineData(6, "5.1(side) ", new[] { 1.0, 1.0, 1.0, 0.0, S, S })]
    public void Channels_are_weighed_by_what_they_are(int channels, string? layout, double[] expected)
        => Assert.Equal(expected, LoudnessMeter.ChannelWeights(channels, layout).Select(w => Math.Round(w, 6)).ToArray(), new WeightComparer());

    private const double S = 1.412538; // +1.5 dB

    private sealed class WeightComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-5;

        public int GetHashCode(double obj) => 0;
    }

    [Theory]
    [InlineData("2 channels")] // unspecified order: nothing to read
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("FL+FR")] // does not match the 4 channels: counted by number
    [Trait("Category", "Unhappy")]
    public void A_layout_that_is_unknown_or_does_not_fit_falls_back_to_the_count(string layout)
        => Assert.Equal(
            LoudnessMeter.ChannelWeights(4),
            LoudnessMeter.ChannelWeights(4, layout));

    [Fact]
    public void A_four_channel_file_that_is_not_a_quad_is_not_given_surround_weights()
    {
        // 4.0 (L R C Cs) at the same level on all channels: four plain channels = +6.02 dB over one; a quad adds 1.5 dB to two of them.
        var data = Sine(48_000, 4, 10, -30);
        var plain = new LoudnessMeter(48_000, 4, "4.0");
        plain.Add(data);
        var quad = new LoudnessMeter(48_000, 4, "quad");
        quad.Add(data);

        var difference = quad.Finish().IntegratedLufs!.Value - plain.Finish().IntegratedLufs!.Value;
        Assert.Equal(10 * Math.Log10((2 + (2 * 1.41253754462275)) / 4), difference, 2);
    }

    [Fact]
    public void Feeding_the_samples_in_odd_pieces_gives_the_same_result()
    {
        var whole = Sine(44_100, 2, 6, -20, hz: 440);
        var expected = Measure(44_100, 2, whole);
        var pieces = new List<float[]>();
        for (var at = 0; at < whole.Length;)
        {
            var take = Math.Min(2 * 7919, whole.Length - at); // whole frames, an awkward count
            pieces.Add(whole[at..(at + take)]);
            at += take;
        }

        var chunked = Measure(44_100, 2, [.. pieces]);
        Assert.Equal(expected.IntegratedLufs!.Value, chunked.IntegratedLufs!.Value, 9);
        Assert.Equal(expected.TruePeak, chunked.TruePeak, 9);
    }

    [Fact]
    public void Silence_has_no_loudness_and_no_peak()
    {
        var result = Measure(48_000, 2, new float[48_000 * 2 * 3]);
        Assert.Null(result.IntegratedLufs);
        Assert.Equal(0, result.TruePeak);
    }

    [Fact]
    public void Nothing_added_is_not_a_measurement()
    {
        var result = new LoudnessMeter(48_000, 2).Finish();
        Assert.Null(result.IntegratedLufs);
        Assert.Equal(0, result.DurationSeconds);
    }

    [Fact]
    public void A_pause_far_below_the_loudness_does_not_pull_the_reading_down()
    {
        var loud = Sine(48_000, 2, 10, -20);
        var quiet = Sine(48_000, 2, 30, -60); // below the -10 LU relative gate
        var result = Measure(48_000, 2, loud, quiet);
        Assert.InRange(result.IntegratedLufs!.Value, -20.2, -19.8);
        var digitalSilence = Measure(48_000, 2, loud, new float[48_000 * 2 * 30]);
        Assert.InRange(digitalSilence.IntegratedLufs!.Value, -20.2, -19.8);
    }

    [Fact]
    public void Content_below_the_absolute_gate_has_no_loudness()
    {
        var result = Measure(48_000, 2, Sine(48_000, 2, 5, -80));
        Assert.Null(result.IntegratedLufs);
    }

    [Fact]
    public void A_clip_shorter_than_one_block_is_still_measured()
    {
        var result = Measure(48_000, 2, Sine(48_000, 2, 0.2, -23));
        Assert.NotNull(result.IntegratedLufs);
        Assert.InRange(result.IntegratedLufs!.Value, -24.5, -21.5);
    }

    [Fact]
    public void True_peak_finds_the_peak_between_the_samples()
    {
        // A sine at a quarter of the sample rate, 45 degrees off: every sample is +-0.707 but the wave reaches 1.0 in between.
        var samples = Sine(48_000, 1, 2, 0, hz: 12_000, phase: Math.PI / 4);
        var result = Measure(48_000, 1, samples);
        Assert.InRange(samples.Max(Math.Abs), 0.70, 0.72);
        Assert.InRange(result.TruePeak, 0.98, 1.03);
    }

    [Fact]
    public void True_peak_of_a_full_scale_square_wave_is_at_least_its_samples()
    {
        var data = new float[48_000 * 2];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (i / 2 / 24) % 2 == 0 ? 1f : -1f;
        }

        Assert.True(Measure(48_000, 2, data).TruePeak >= 1.0);
    }

    [Fact]
    public void Non_finite_samples_are_ignored_instead_of_poisoning_the_result()
    {
        var samples = Sine(48_000, 2, 10, -23);
        samples[1000] = float.NaN;
        samples[2001] = float.PositiveInfinity;
        var result = Measure(48_000, 2, samples);
        Assert.InRange(result.IntegratedLufs!.Value, -23.2, -22.8);
        Assert.True(double.IsFinite(result.TruePeak));
    }

    [Fact]
    public void Surround_channels_count_more_and_the_lfe_not_at_all()
    {
        // 5.1: only the LFE (channel 3) carries signal -> not measured.
        var lfeOnly = new float[48_000 * 6 * 3];
        var sine = Sine(48_000, 1, 3, -10);
        for (var f = 0; f < sine.Length; f++)
        {
            lfeOnly[(f * 6) + 3] = sine[f];
        }

        Assert.Null(Measure(48_000, 6, lfeOnly).IntegratedLufs);

        // Front left alone vs surround left alone: the surround reads 1.5 dB louder.
        float[] OnlyChannel(int channel)
        {
            var data = new float[48_000 * 6 * 5];
            var s = Sine(48_000, 1, 5, -20);
            for (var f = 0; f < s.Length; f++)
            {
                data[(f * 6) + channel] = s[f];
            }

            return data;
        }

        var front = Measure(48_000, 6, OnlyChannel(0)).IntegratedLufs!.Value;
        var surround = Measure(48_000, 6, OnlyChannel(4)).IntegratedLufs!.Value;
        Assert.Equal(1.5, surround - front, 1);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(7_999, 2)]
    [InlineData(768_001, 2)]
    [InlineData(48_000, 0)]
    [InlineData(48_000, 33)]
    [Trait("Category", "Unhappy")]
    public void An_impossible_format_is_refused(int rate, int channels)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LoudnessMeter(rate, channels));

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_finished_meter_cannot_be_fed_again()
    {
        var meter = new LoudnessMeter(48_000, 2);
        meter.Finish();
        Assert.Throws<ObjectDisposedException>(() => meter.Add(new float[8]));
    }
}
