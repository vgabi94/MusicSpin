using MusicSpin.Core.Audio;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>A decoder read through a converter: content, block sizes, end, seeking and broken converters.</summary>
public sealed class ConvertingDecoderTests
{
    private readonly RampDecoderFactory _decoders = new();

    private IAudioDecoder Open(RampSpec spec)
    {
        var path = $"/c/{spec.Id}.flac";
        _decoders.Add(path, spec);
        return _decoders.Open(path);
    }

    private static float[] ReadAll(ConvertingDecoder decoder, int block)
    {
        var all = new List<float>();
        var buffer = new float[block];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return [.. all];
    }

    [Theory]
    [InlineData(2000, 2, 1000, 2, 7)] // down, odd block: frames split across reads
    [InlineData(500, 2, 1000, 2, 64)] // up
    [InlineData(1000, 1, 1000, 2, 10)] // mono to stereo
    [InlineData(2000, 2, 1000, 1, 3)] // both
    [InlineData(1000, 2, 1000, 2, 100_000)] // one huge block
    public void Reads_the_whole_track_converted_whatever_the_block_size(int rate, int channels, int toRate, int toChannels, int block)
    {
        var spec = new RampSpec(1, 9000, rate, channels, Bits: 16);
        var to = new PcmFormat(toRate, toChannels);
        using var decoder = new ConvertingDecoder(Open(spec), new StepConverter(spec.Format, to));

        Assert.Equal(new PcmFormat(toRate, toChannels, 16), decoder.Format); // the track's own depth is kept
        Assert.Equal(StepConverter.Expected(spec, to), ReadAll(decoder, block));
        Assert.Equal(0, decoder.Read(new float[64])); // stays at the end
    }

    [Fact]
    public void Seeking_resets_the_converter_and_drops_what_was_staged()
    {
        var spec = new RampSpec(2, 8000, 2000, 2);
        var to = new PcmFormat(1000, 2);
        var converter = new StepConverter(spec.Format, to);
        using var decoder = new ConvertingDecoder(Open(spec), converter);
        decoder.Read(new float[6]); // 3 output frames: input frame 5 consumed, phase left mid-pair

        decoder.Seek(TimeSpan.FromSeconds(2)); // input frame 4000

        Assert.Equal(1, converter.Resets);
        Assert.Equal(TimeSpan.FromSeconds(2), decoder.Position);
        var expected = StepConverter.Expected(spec, to).AsSpan(2000 * 2, 200).ToArray();
        Assert.Equal(expected, ReadAll(decoder, 200)[..200]);
    }

    [Fact]
    public void Position_counts_only_what_was_converted()
    {
        var spec = new RampSpec(3, 20_000, 2000, 2);
        using var decoder = new ConvertingDecoder(Open(spec), new StepConverter(spec.Format, new PcmFormat(1000, 2)));

        decoder.Read(new float[200]); // 100 output frames = 200 input frames = 0.1 s

        Assert.Equal(TimeSpan.FromSeconds(0.1), decoder.Position);
    }

    [Fact]
    public void A_converter_for_another_format_is_refused()
    {
        var spec = new RampSpec(4, 100, 1000, 2);
        using var inner = Open(spec);
        using var converter = new StepConverter(new PcmFormat(2000, 2), new PcmFormat(1000, 2));

        Assert.Throws<ArgumentException>(() => new ConvertingDecoder(inner, converter));
        Assert.False(converter.Disposed); // the caller still owns both
    }

    [Fact]
    public void A_converter_that_makes_no_progress_or_overflows_fails_instead_of_spinning()
    {
        var spec = new RampSpec(5, 100, 1000, 2);
        using var stuck = new ConvertingDecoder(Open(spec), new BrokenConverter(spec.Format, produce: 0, consume: 0));
        using var greedy = new ConvertingDecoder(Open(spec with { Id = 6 }), new BrokenConverter(spec.Format, produce: 0, consume: int.MaxValue));

        Assert.Throws<InvalidOperationException>(() => stuck.Read(new float[64]));
        Assert.Throws<InvalidOperationException>(() => greedy.Read(new float[64]));
    }

    [Fact]
    public void Dispose_disposes_the_decoder_and_the_converter_once()
    {
        var spec = new RampSpec(7, 100, 2000, 2);
        var converter = new StepConverter(spec.Format, new PcmFormat(1000, 2));
        var decoder = new ConvertingDecoder(Open(spec), converter);

        decoder.Dispose();
        decoder.Dispose();

        Assert.True(converter.Disposed);
        Assert.Throws<ObjectDisposedException>(() => decoder.Read(new float[8]));
    }

    private sealed class BrokenConverter(PcmFormat from, int produce, int consume) : IPcmConverter
    {
        public PcmFormat From => from;

        public PcmFormat To => from;

        public int Convert(ReadOnlySpan<float> input, Span<float> output, out int consumed)
        {
            consumed = consume;
            return produce;
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
