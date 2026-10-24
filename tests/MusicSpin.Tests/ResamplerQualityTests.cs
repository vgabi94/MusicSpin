using MusicSpin.Core.Audio;
using MusicSpin.Core.Output.MiniAudio;

namespace MusicSpin.Tests;

/// <summary>
/// What the shared-mode resampler (native/msaudio: libsamplerate's SRC_SINC_BEST_QUALITY) does to a signal, measured on
/// the real native converter: the passband stays flat, what lies above the output's Nyquist is removed instead of
/// aliased back down, upsampling leaves no images, and a tone comes out with distortion and noise far below anything
/// audible. The converter measures about -140 to -155 dB on these; the thresholds leave 10 to 20 dB for other compilers
/// and CPUs (float rounding), and are still far beyond what a linear resampler can do.
/// </summary>
public sealed class ResamplerQualityTests(ITestOutputHelper log)
{
    private const double Amplitude = 0.5;

    /// <summary>One second of a sine (every channel the same) through the converter, fed and drained in uneven blocks.</summary>
    private static float[] Convert(int from, int to, double hz, int channels = 2, int frames = 0)
    {
        frames = frames > 0 ? frames : from;
        var input = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var v = (float)(Amplitude * Math.Sin(2 * Math.PI * hz * f / from));
            for (var c = 0; c < channels; c++)
            {
                input[(f * channels) + c] = v;
            }
        }

        using var converter = new MiniAudioConverter(new PcmFormat(from, channels), new PcmFormat(to, channels));
        var output = new List<float>();
        var block = new float[389 * channels];
        var at = 0;
        var step = 0;
        while (at < input.Length)
        {
            var take = Math.Min(input.Length - at, ((step++ % 7) + 1) * 61 * channels);
            var written = converter.Convert(input.AsSpan(at, take), block, out var consumed);
            output.AddRange(block.AsSpan(0, written).ToArray());
            at += consumed;
        }

        int tail;
        while ((tail = converter.Drain(block)) > 0)
        {
            output.AddRange(block.AsSpan(0, tail).ToArray());
        }

        return [.. output];
    }

    /// <summary>
    /// Least-squares fit of a sine at <paramref name="hz"/> (plus DC) to channel 0 over the middle of the signal (the
    /// filter's start and end transients left out): its amplitude, and the RMS of everything else.
    /// </summary>
    private static (double Amplitude, double ResidualRms) Fit(float[] pcm, int rate, int channels, double hz)
    {
        var frames = pcm.Length / channels;
        int start = frames / 10, end = frames - (frames / 10);
        double ss = 0, sc = 0, cc = 0, s1 = 0, c1 = 0, n = 0, ys = 0, yc = 0, y1 = 0;
        for (var f = start; f < end; f++)
        {
            double w = 2 * Math.PI * hz * f / rate, s = Math.Sin(w), c = Math.Cos(w), y = pcm[f * channels];
            ss += s * s; sc += s * c; cc += c * c; s1 += s; c1 += c; n++;
            ys += y * s; yc += y * c; y1 += y;
        }

        // Solve the 3x3 normal equations [ss sc s1; sc cc c1; s1 c1 n] x = [ys yc y1] (Cramer).
        static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i)
            => (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        var det = Det(ss, sc, s1, sc, cc, c1, s1, c1, n);
        var a = Det(ys, sc, s1, yc, cc, c1, y1, c1, n) / det;
        var b = Det(ss, ys, s1, sc, yc, c1, s1, y1, n) / det;
        var dc = Det(ss, sc, ys, sc, cc, yc, s1, c1, y1) / det;
        double residual = 0;
        for (var f = start; f < end; f++)
        {
            var w = 2 * Math.PI * hz * f / rate;
            var e = pcm[f * channels] - ((a * Math.Sin(w)) + (b * Math.Cos(w)) + dc);
            residual += e * e;
        }

        return (Math.Sqrt((a * a) + (b * b)), Math.Sqrt(residual / n));
    }

    private static double Rms(float[] pcm, int channels)
    {
        var frames = pcm.Length / channels;
        int start = frames / 10, end = frames - (frames / 10);
        double sum = 0;
        for (var f = start; f < end; f++)
        {
            sum += (double)pcm[f * channels] * pcm[f * channels];
        }

        return Math.Sqrt(sum / (end - start));
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-12));

    [Theory]
    [InlineData(44_100, 48_000, 1_000)]
    [InlineData(48_000, 44_100, 1_000)]
    [InlineData(96_000, 44_100, 1_000)]
    [InlineData(44_100, 96_000, 1_000)]
    [InlineData(192_000, 48_000, 997)]
    public void A_tone_comes_out_with_distortion_and_noise_below_minus_130_dB(int from, int to, double hz)
    {
        var output = Convert(from, to, hz);

        var (amplitude, residual) = Fit(output, to, 2, hz);
        var thdN = Db(residual / (amplitude / Math.Sqrt(2))); // THD+N: everything but the tone, against the tone's RMS
        log.WriteLine($"{from}->{to} {hz} Hz: level {Db(amplitude / Amplitude):0.0000} dB, THD+N {thdN:0.0} dB");
        Assert.InRange(Db(amplitude / Amplitude), -0.001, 0.001);
        Assert.True(thdN < -130, $"THD+N {thdN:0.0} dB");
    }

    [Theory]
    [InlineData(96_000, 44_100, 20)]
    [InlineData(96_000, 44_100, 20_000)]
    [InlineData(48_000, 44_100, 20_000)]
    [InlineData(44_100, 48_000, 20_000)]
    [InlineData(44_100, 96_000, 20_000)]
    public void The_passband_is_flat_to_20_kHz(int from, int to, double hz)
    {
        var output = Convert(from, to, hz);

        var (amplitude, residual) = Fit(output, to, 2, hz);
        var level = Db(amplitude / Amplitude);
        log.WriteLine($"{from}->{to} {hz} Hz: level {level:0.0000} dB, rest {Db(residual / (amplitude / Math.Sqrt(2))):0.0} dB");
        Assert.InRange(level, -0.01, 0.01);
        Assert.True(Db(residual / (amplitude / Math.Sqrt(2))) < -125, "a clean tone at the band edge too");
    }

    [Theory]
    [InlineData(96_000, 44_100, 30_000)] // would alias to 14.1 kHz
    [InlineData(96_000, 44_100, 23_000)] // just above the output's Nyquist (22.05 kHz): would alias to 21.1 kHz
    [InlineData(48_000, 44_100, 23_500)]
    [InlineData(192_000, 48_000, 60_000)]
    public void Content_above_the_output_nyquist_is_removed_not_aliased(int from, int to, double hz)
    {
        var output = Convert(from, to, hz);

        var left = Db(Rms(output, 2) / (Amplitude / Math.Sqrt(2)));
        log.WriteLine($"{from}->{to} {hz} Hz: what is left {left:0.0} dB");
        Assert.True(left < -130, $"{left:0.0} dB of the tone got through");
    }

    [Theory]
    [InlineData(44_100, 96_000, 20_000)] // its first image would be at 24.1 kHz
    [InlineData(44_100, 48_000, 21_000)] // image at 23.1 kHz, just above the input's Nyquist
    public void Upsampling_leaves_no_images(int from, int to, double hz)
    {
        var output = Convert(from, to, hz);

        var (amplitude, residual) = Fit(output, to, 2, hz);
        var images = Db(residual / (amplitude / Math.Sqrt(2)));
        log.WriteLine($"{from}->{to} {hz} Hz: images and noise {images:0.0} dB");
        Assert.True(images < -125, $"images at {images:0.0} dB");
    }

    [Theory]
    [InlineData(44_100, 48_000)]
    [InlineData(96_000, 44_100)]
    [InlineData(44_100, 192_000)]
    public void Draining_gives_the_whole_track_at_the_new_rate(int from, int to)
    {
        var output = Convert(from, to, 1_000, channels: 1, frames: from / 2);

        Assert.InRange(output.Length, (to / 2) - 1, (to / 2) + 1); // none of the end lost in the filter, none added
    }

    [Fact]
    public void Several_channels_are_converted_alike()
    {
        var output = Convert(44_100, 48_000, 1_000, channels: 6, frames: 4_410);

        for (var f = 0; f < output.Length / 6; f++)
        {
            for (var c = 1; c < 6; c++)
            {
                Assert.Equal(output[f * 6], output[(f * 6) + c]);
            }
        }
    }
}
