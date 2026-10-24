using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output;
using MusicSpin.Core.Output.MiniAudio;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>
/// The real native converter (miniaudio's data converter through native/msaudio) and when the miniaudio output hands one
/// out: only in shared mode, never in exclusive (the DAC must get the track's own rate).
/// </summary>
public sealed class MiniAudioConverterTests : IDisposable
{
    private readonly MiniAudioBackend _backend = new(nullBackend: true); // loads and checks the native layer

    public void Dispose() => _backend.Dispose();

    private static float[] Sine(int rate, int channels, double hz, int frames)
    {
        var pcm = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var v = (float)(0.5 * Math.Sin(2 * Math.PI * hz * f / rate));
            for (var c = 0; c < channels; c++)
            {
                pcm[(f * channels) + c] = v;
            }
        }

        return pcm;
    }

    /// <summary>Runs all of <paramref name="input"/> through in small, uneven blocks (the way a decoder feeds it).</summary>
    private static float[] ConvertAll(MiniAudioConverter converter, float[] input)
    {
        var output = new List<float>();
        var block = new float[333 * converter.To.Channels];
        var at = 0;
        var step = 0;
        while (at < input.Length)
        {
            var take = Math.Min(input.Length - at, ((step++ % 5) + 1) * 97 * converter.From.Channels);
            var written = converter.Convert(input.AsSpan(at, take), block, out var consumed);
            Assert.InRange(consumed, 0, take);
            Assert.True(consumed > 0 || written > 0, "no progress");
            output.AddRange(block.AsSpan(0, written).ToArray());
            at += consumed;
        }

        return [.. output];
    }

    private static double Frequency(float[] pcm, int rate, int channels, int skipFrames)
    {
        var crossings = 0;
        var frames = pcm.Length / channels;
        for (var f = skipFrames + 1; f < frames; f++)
        {
            if (pcm[(f - 1) * channels] < 0 && pcm[f * channels] >= 0)
            {
                crossings++;
            }
        }

        return crossings / ((double)(frames - skipFrames) / rate);
    }

    [Theory]
    [InlineData(44_100, 48_000)]
    [InlineData(96_000, 44_100)]
    [InlineData(48_000, 192_000)]
    public void A_tone_keeps_its_pitch_and_length_across_rates(int from, int to)
    {
        using var converter = new MiniAudioConverter(new PcmFormat(from, 2), new PcmFormat(to, 2));
        var input = Sine(from, 2, 1000, from); // one second

        var output = ConvertAll(converter, input);

        var frames = output.Length / 2;
        Assert.InRange(frames, to - (to / 100), to); // a second, less the filter's few frames of delay
        Assert.InRange(Frequency(output, to, 2, to / 100), 990, 1010);
        Assert.All(output, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 0.6f));
    }

    [Fact]
    public void A_decoders_NaN_or_infinity_does_not_poison_the_rest_of_a_converted_track()
    {
        // One broken sample (NaN on the left, +inf on the right) a tenth of a second in. The resampler's filters would
        // carry it into every later sample; it must become one silent sample instead, and the sine go on after it.
        var track = TestAudio.Track("/c/broken.flac", sampleRate: 44_100, channels: 2, seconds: 1);
        var inner = new FakeDecoder(track, (frame, channel) => frame == 4410
            ? (channel == 0 ? float.NaN : float.PositiveInfinity)
            : (float)(0.5 * Math.Sin(2 * Math.PI * 440 * frame / 44_100)));
        using var decoder = new ConvertingDecoder(inner, new MiniAudioConverter(new PcmFormat(44_100, 2), new PcmFormat(48_000, 2)));

        var output = new List<float>();
        var block = new float[1000];
        int read;
        while ((read = decoder.Read(block)) > 0)
        {
            output.AddRange(block.AsSpan(0, read).ToArray());
        }

        Assert.All(output, x => Assert.True(float.IsFinite(x), $"non-finite sample {x}"));
        var pcm = output.ToArray();
        var tail = pcm.AsSpan(pcm.Length / 2);
        Assert.InRange(tail.ToArray().Max(), 0.45f, 0.55f); // still the sine, at its level
        Assert.Equal(440, Frequency(pcm, 48_000, 2, 9600), 1.0);
    }

    [Fact]
    public void Mono_is_spread_to_both_channels_at_the_same_rate()
    {
        using var converter = new MiniAudioConverter(new PcmFormat(48_000, 1), new PcmFormat(48_000, 2));
        var input = Sine(48_000, 1, 440, 4800);

        var output = ConvertAll(converter, input);

        Assert.Equal(input.Length * 2, output.Length);
        for (var f = 0; f < input.Length; f++)
        {
            Assert.Equal(output[2 * f], output[(2 * f) + 1]);
        }
    }

    [Fact]
    public void Reset_forgets_the_filter_history()
    {
        using var converter = new MiniAudioConverter(new PcmFormat(44_100, 2), new PcmFormat(48_000, 2));
        var loud = Sine(44_100, 2, 3000, 4410);
        var silence = new float[4410 * 2];
        var fresh = new float[512];
        var output = new float[512];

        ConvertAll(converter, loud);
        converter.Reset();
        var written = converter.Convert(silence, output, out _);

        Assert.True(written > 0);
        Assert.All(output[..written], s => Assert.Equal(0f, s)); // nothing of the tone leaks past a seek
        using var other = new MiniAudioConverter(new PcmFormat(44_100, 2), new PcmFormat(48_000, 2));
        Assert.Equal(written, other.Convert(silence, fresh, out _)); // and it starts like a new one
    }

    [Theory]
    [InlineData(0, 44_100, 2, 48_000)]
    [InlineData(2, 0, 2, 48_000)]
    [InlineData(2, 44_100, 0, 48_000)]
    [InlineData(2, 44_100, 2, -1)]
    [InlineData(2, 44_100, 300, 48_000)]
    public void Impossible_formats_are_refused(int inCh, int inRate, int outCh, int outRate)
    {
        var ex = Assert.Throws<MusicSpinException>(() => new MiniAudioConverter(new PcmFormat(inRate, inCh), new PcmFormat(outRate, outCh)));
        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
    }

    [Fact]
    public void A_disposed_converter_refuses_work_and_disposes_once()
    {
        var converter = new MiniAudioConverter(new PcmFormat(44_100, 2), new PcmFormat(48_000, 2));
        converter.Dispose();
        converter.Dispose();

        Assert.Throws<ObjectDisposedException>(() => converter.Convert(new float[4], new float[4], out _));
        Assert.Throws<ObjectDisposedException>(converter.Reset);
    }

    [Fact]
    public void Only_a_shared_mode_output_converts()
    {
        using var provider = new MiniAudioOutputProvider(nullBackend: true);
        using var shared = provider.Create(new AudioOutputOptions());
        using var exclusive = provider.Create(new AudioOutputOptions(Exclusive: true));

        using var converter = shared.CreateConverter(new PcmFormat(96_000, 2, 24), new PcmFormat(44_100, 2, 16));
        Assert.NotNull(converter);
        Assert.Equal(96_000, converter.From.SampleRate);
        Assert.Equal(44_100, converter.To.SampleRate);
        Assert.Null(exclusive.CreateConverter(new PcmFormat(96_000, 2, 24), new PcmFormat(44_100, 2, 16)));
        Assert.Null(shared.CreateConverter(new PcmFormat(96_000, 0), new PcmFormat(44_100, 2))); // nothing to convert from
    }
}
