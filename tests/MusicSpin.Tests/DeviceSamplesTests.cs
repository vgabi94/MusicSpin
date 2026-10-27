using System.Buffers.Binary;
using MusicSpin.Core.Output;

namespace MusicSpin.Tests;

/// <summary>The WASAPI exclusive path's format choice and conversion (#148), testable without Windows.</summary>
public class DeviceSamplesTests
{
    [Theory]
    [InlineData(16, new[] { nameof(DeviceSample.S16), nameof(DeviceSample.S24), nameof(DeviceSample.S24In32), nameof(DeviceSample.S32) })]
    [InlineData(8, new[] { nameof(DeviceSample.S16), nameof(DeviceSample.S24), nameof(DeviceSample.S24In32), nameof(DeviceSample.S32) })]
    [InlineData(24, new[] { nameof(DeviceSample.S24In32), nameof(DeviceSample.S32), nameof(DeviceSample.S24), nameof(DeviceSample.S16) })]
    [InlineData(20, new[] { nameof(DeviceSample.S24In32), nameof(DeviceSample.S32), nameof(DeviceSample.S24), nameof(DeviceSample.S16) })]
    [InlineData(32, new[] { nameof(DeviceSample.F32), nameof(DeviceSample.S32), nameof(DeviceSample.S24In32), nameof(DeviceSample.S24), nameof(DeviceSample.S16) })]
    [InlineData(1, new[] { nameof(DeviceSample.F32), nameof(DeviceSample.S32), nameof(DeviceSample.S24In32), nameof(DeviceSample.S24), nameof(DeviceSample.S16) })] // DSD, decoded to float
    [InlineData(0, new[] { nameof(DeviceSample.F32), nameof(DeviceSample.S32), nameof(DeviceSample.S24In32), nameof(DeviceSample.S24), nameof(DeviceSample.S16) })] // depth unknown
    public void Exclusive_candidates_start_with_the_source_depth_and_offer_a_narrower_one_only_last_as_s16(int bits, string[] expected)
        => Assert.Equal(expected, DeviceSamples.ExclusiveCandidates(bits).Select(f => f.ToString()));

    [Theory]
    [InlineData(16, nameof(DeviceSample.S16), false)]
    [InlineData(8, nameof(DeviceSample.S16), false)]
    [InlineData(24, nameof(DeviceSample.S16), true)]
    [InlineData(32, nameof(DeviceSample.S16), true)]
    [InlineData(1, nameof(DeviceSample.S16), true)] // DSD: float samples
    [InlineData(0, nameof(DeviceSample.S16), true)]
    [InlineData(24, nameof(DeviceSample.S24In32), false)]
    [InlineData(32, nameof(DeviceSample.F32), false)]
    public void Only_a_deeper_source_written_as_s16_is_narrower_than_the_source(int bits, string format, bool narrows)
        => Assert.Equal(narrows, DeviceSamples.NarrowsSource(bits, Enum.Parse<DeviceSample>(format)));

    [Fact]
    public void Every_24bit_value_survives_packed_s24_and_s24in32_bit_exact()
    {
        int[] values = [0, 1, -1, 2, -2, 8_388_607, -8_388_608, 1_234_567, -7_654_321, 4_194_304];
        var floats = values.Select(v => (float)(v / 8_388_608.0)).ToArray();

        var packed = new byte[floats.Length * 3];
        DeviceSamples.Convert(floats, packed, DeviceSample.S24);
        var in32 = new byte[floats.Length * 4];
        DeviceSamples.Convert(floats, in32, DeviceSample.S24In32);

        for (var i = 0; i < values.Length; i++)
        {
            var p = packed[(i * 3)] | (packed[(i * 3) + 1] << 8) | ((sbyte)packed[(i * 3) + 2] << 16);
            Assert.Equal(values[i], p);
            var c = BinaryPrimitives.ReadInt32LittleEndian(in32.AsSpan(i * 4));
            Assert.Equal(0, c & 0xFF); // left-justified: the low byte is padding
            Assert.Equal(values[i], c >> 8);
        }
    }

    [Fact]
    public void Every_16bit_value_survives_s16_and_padding_into_32_bits()
    {
        short[] values = [0, 1, -1, short.MaxValue, short.MinValue, 12_345, -23_456];
        var floats = values.Select(v => v / 32_768f).ToArray();

        var s16 = new byte[floats.Length * 2];
        DeviceSamples.Convert(floats, s16, DeviceSample.S16);
        var s32 = new byte[floats.Length * 4];
        DeviceSamples.Convert(floats, s32, DeviceSample.S32);

        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], BinaryPrimitives.ReadInt16LittleEndian(s16.AsSpan(i * 2)));
            Assert.Equal(values[i] << 16, BinaryPrimitives.ReadInt32LittleEndian(s32.AsSpan(i * 4)));
        }
    }

    [Fact]
    public void Out_of_range_and_nan_samples_clip_or_go_silent_instead_of_wrapping()
    {
        float[] floats = [1f, 1.5f, -1f, -3f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        var s24 = new byte[floats.Length * 4];
        DeviceSamples.Convert(floats, s24, DeviceSample.S24In32);
        var s16 = new byte[floats.Length * 2];
        DeviceSamples.Convert(floats, s16, DeviceSample.S16);

        int[] expected24 = [8_388_607, 8_388_607, -8_388_608, -8_388_608, 0, 8_388_607, -8_388_608];
        short[] expected16 = [short.MaxValue, short.MaxValue, short.MinValue, short.MinValue, 0, short.MaxValue, short.MinValue];
        for (var i = 0; i < floats.Length; i++)
        {
            Assert.Equal(expected24[i], BinaryPrimitives.ReadInt32LittleEndian(s24.AsSpan(i * 4)) >> 8);
            Assert.Equal(expected16[i], BinaryPrimitives.ReadInt16LittleEndian(s16.AsSpan(i * 2)));
        }
    }

    [Fact]
    public void Float_passes_through_unchanged()
    {
        float[] floats = [0.123456789f, -0.5f, 1e-9f];
        var bytes = new byte[floats.Length * 4];
        DeviceSamples.Convert(floats, bytes, DeviceSample.F32);

        for (var i = 0; i < floats.Length; i++)
        {
            Assert.Equal(floats[i], BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4)));
        }
    }

    [Fact]
    public void A_destination_too_small_for_the_container_is_refused()
        => Assert.Throws<ArgumentException>(() => DeviceSamples.Convert(new float[4], new byte[11], DeviceSample.S24));

    [Theory]
    [InlineData(nameof(DeviceSample.S16), 2, 16, "s16")]
    [InlineData(nameof(DeviceSample.S24), 3, 24, "s24")]
    [InlineData(nameof(DeviceSample.S24In32), 4, 24, "s24in32")]
    [InlineData(nameof(DeviceSample.S32), 4, 32, "s32")]
    [InlineData(nameof(DeviceSample.F32), 4, 32, "f32")]
    public void Container_size_valid_bits_and_names(string sample, int bytes, int bits, string name)
    {
        var format = Enum.Parse<DeviceSample>(sample);
        Assert.Equal((bytes, bits, name), (DeviceSamples.ContainerBytes(format), DeviceSamples.ValidBits(format), DeviceSamples.Name(format)));
    }

    private const int UnsupportedFormat = unchecked((int)0x88890008);
    private const int DeviceInUse = unchecked((int)0x8889000A);

    [Fact]
    public void A_24bit_source_opens_24_in_32_on_a_usb_dac_that_refuses_packed_24()
    {
        var probed = new List<DeviceSample>();
        var result = DeviceSamples.Negotiate(DeviceSamples.ExclusiveCandidates(24),
            f => { probed.Add(f); return f == DeviceSample.S24 ? UnsupportedFormat : 0; }, _ => 0);

        Assert.Equal(DeviceSample.S24In32, result.Chosen);
        Assert.Equal([DeviceSample.S24In32], probed);
    }

    [Fact]
    public void A_format_accepted_by_IsFormatSupported_but_refused_by_Initialize_moves_on_to_the_next_one()
    {
        var initialized = new List<DeviceSample>();
        var result = DeviceSamples.Negotiate(DeviceSamples.ExclusiveCandidates(24), _ => 0,
            f => { initialized.Add(f); return f == DeviceSample.S24In32 ? UnsupportedFormat : 0; });

        Assert.Equal(DeviceSample.S32, result.Chosen);
        Assert.Equal([DeviceSample.S24In32, DeviceSample.S32], initialized);
        Assert.Equal(["s24in32 (refused at initialize)"], result.Refused);
    }

    [Fact]
    public void Every_format_refused_reports_each_one_tried_and_no_failure_code()
    {
        var result = DeviceSamples.Negotiate(DeviceSamples.ExclusiveCandidates(24), f => f == DeviceSample.S24 ? 0 : UnsupportedFormat, _ => UnsupportedFormat);

        Assert.Null(result.Chosen);
        Assert.Equal(0, result.FailedHr);
        Assert.Equal(["s24in32", "s32", "s24 (refused at initialize)", "s16"], result.Refused);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_24bit_source_on_a_device_that_takes_only_16bit_opens_s16_after_trying_every_wider_format()
    {
        // A 16-bit-only device (#168): IsFormatSupported refuses every 24- and 32-bit format and takes s16 at any rate.
        var probed = new List<DeviceSample>();
        var result = DeviceSamples.Negotiate(DeviceSamples.ExclusiveCandidates(24),
            f => { probed.Add(f); return f == DeviceSample.S16 ? 0 : UnsupportedFormat; }, _ => 0);

        Assert.Equal(DeviceSample.S16, result.Chosen);
        Assert.Equal([DeviceSample.S24In32, DeviceSample.S32, DeviceSample.S24, DeviceSample.S16], probed);
        Assert.Equal(["s24in32", "s32", "s24"], result.Refused);
        Assert.True(DeviceSamples.NarrowsSource(24, result.Chosen!.Value));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_device_that_refuses_s16_too_still_ends_in_no_format_naming_every_one_tried()
    {
        var result = DeviceSamples.Negotiate(DeviceSamples.ExclusiveCandidates(24), _ => UnsupportedFormat, _ => 0);

        Assert.Null(result.Chosen);
        Assert.Equal(0, result.FailedHr);
        Assert.Equal(["s24in32", "s32", "s24", "s16"], result.Refused);
    }

    [Fact]
    public void The_dither_notice_names_the_formats_refused_but_no_depth_so_it_stays_true_for_the_next_tracks()
    {
        Assert.Equal("The device takes only 16-bit in exclusive mode (refused s24in32, s32, s24): tracks deeper than 16-bit are dithered to it.",
            DeviceSamples.DitherNotice(["s24in32", "s32", "s24"]));
        Assert.Equal("The device takes only 16-bit in exclusive mode: tracks deeper than 16-bit are dithered to it.", DeviceSamples.DitherNotice([]));
    }

    // ---- TPDF dither to s16 (#168) --------------------------------------------------------------------------------

    private static short[] Dither(float[] samples, uint seed = 12345)
    {
        var bytes = new byte[samples.Length * 2];
        new Tpdf16Dither(seed).Convert(samples, bytes);
        return [.. Enumerable.Range(0, samples.Length).Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)))];
    }

    [Fact]
    public void Dither_stays_within_one_lsb_of_the_rounded_sample()
    {
        var random = new Random(7);
        var floats = Enumerable.Range(0, 10_000).Select(_ => (float)((random.NextDouble() * 1.8) - 0.9)).ToArray();

        var s16 = Dither(floats);

        for (var i = 0; i < floats.Length; i++)
        {
            var exact = floats[i] * 32_768.0;
            Assert.InRange(s16[i] - exact, -1.5, 1.5);
        }
    }

    [Fact]
    public void Dither_keeps_detail_below_one_lsb_that_rounding_would_drop()
    {
        // A quarter of a 16-bit step (a 24-bit sample of 64): rounding gives 0 every time; dithered, it averages to 0.25.
        var floats = Enumerable.Repeat((float)(0.25 / 32_768.0), 100_000).ToArray();

        var rounded = new byte[floats.Length * 2];
        DeviceSamples.Convert(floats, rounded, DeviceSample.S16);
        var s16 = Dither(floats);

        Assert.All(rounded, b => Assert.Equal(0, b));
        Assert.InRange(s16.Average(v => (double)v), 0.22, 0.28);
        Assert.All(s16, v => Assert.InRange(v, (short)-1, (short)2));
    }

    [Fact]
    public void Dither_is_noise_not_a_fixed_offset_and_repeats_for_the_same_seed()
    {
        var floats = Enumerable.Repeat((float)(0.5 / 32_768.0), 4096).ToArray(); // half a step: not an exact 16-bit value

        var a = Dither(floats, seed: 1);
        var b = Dither(floats, seed: 1);
        var c = Dither(floats, seed: 2);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.InRange(a.Average(v => (double)v), 0.4, 0.6);
        Assert.All(a, v => Assert.InRange(v, (short)0, (short)2)); // triangular over (-1, 1) LSB around 0.5
    }

    [Fact]
    public void Exact_16bit_samples_pass_bit_for_bit_and_silence_stays_silent()
    {
        // A 16-bit track at volume 1.0 with no levelling or equalizer: dither would only add noise.
        short[] values = [0, 1, -1, short.MaxValue, short.MinValue, 12_345, -23_456];
        var floats = values.Select(v => v / 32_768f).ToArray();

        Assert.Equal(values, Dither(floats));
        Assert.All(Dither(new float[4096]), v => Assert.Equal(0, v));
    }

    [Fact]
    public void A_16bit_track_the_volume_or_equalizer_changed_is_dithered_and_the_whole_buffer_with_it()
    {
        // Half volume on a 16-bit track: odd samples land between two steps. Not exact, so the buffer is dithered, its
        // exact samples (here the silence around the one changed sample) too: dither that came and went would follow the music.
        var floats = new float[4096];
        floats[100] = (float)(0.5 * 12_345 / 32_768.0);

        var s16 = Dither(floats);

        Assert.InRange(s16[100], (short)6171, (short)6174);
        Assert.Contains(s16.Where((_, i) => i != 100), v => v != 0);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Dither_clips_full_scale_and_beyond_and_makes_nan_silent()
    {
        float[] floats = [1f, 1.5f, -1f, -3f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];

        var s16 = Dither(floats);

        Assert.Equal(short.MaxValue, s16[0]);
        Assert.Equal(short.MaxValue, s16[1]);
        Assert.InRange(s16[2], short.MinValue, (short)(short.MinValue + 1));
        Assert.Equal(short.MinValue, s16[3]);
        Assert.Equal(0, s16[4]);
        Assert.Equal(short.MaxValue, s16[5]);
        Assert.Equal(short.MinValue, s16[6]);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Dither_refuses_a_destination_too_small_for_s16()
        => Assert.Throws<ArgumentException>(() => new Tpdf16Dither().Convert(new float[4], new byte[7]));

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_zero_seed_still_gives_noise()
        => Assert.True(Dither(Enumerable.Repeat((float)(0.5 / 32_768.0), 256).ToArray(), seed: 0).Distinct().Count() > 1); // xorshift's zero state would stay zero forever

    [Fact]
    public void A_device_in_use_stops_the_search_instead_of_trying_other_formats()
    {
        var initialized = 0;
        var result = DeviceSamples.Negotiate(DeviceSamples.ExclusiveCandidates(24), _ => 0, _ => { initialized++; return DeviceInUse; });

        Assert.Null(result.Chosen);
        Assert.Equal(DeviceInUse, result.FailedHr);
        Assert.Equal(1, initialized);
    }
}
