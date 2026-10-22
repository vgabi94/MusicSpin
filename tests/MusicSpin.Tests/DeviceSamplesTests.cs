using System.Buffers.Binary;
using MusicSpin.Core.Output;

namespace MusicSpin.Tests;

/// <summary>The WASAPI exclusive path's format choice and conversion (#148), testable without Windows.</summary>
public class DeviceSamplesTests
{
    [Theory]
    [InlineData(16, new[] { nameof(DeviceSample.S16), nameof(DeviceSample.S24), nameof(DeviceSample.S24In32), nameof(DeviceSample.S32) })]
    [InlineData(24, new[] { nameof(DeviceSample.S24), nameof(DeviceSample.S24In32), nameof(DeviceSample.S32) })]
    [InlineData(20, new[] { nameof(DeviceSample.S24), nameof(DeviceSample.S24In32), nameof(DeviceSample.S32) })]
    [InlineData(32, new[] { nameof(DeviceSample.F32), nameof(DeviceSample.S32), nameof(DeviceSample.S24In32), nameof(DeviceSample.S24) })]
    [InlineData(1, new[] { nameof(DeviceSample.F32), nameof(DeviceSample.S32), nameof(DeviceSample.S24In32), nameof(DeviceSample.S24) })] // DSD, decoded to float
    public void Exclusive_candidates_start_with_the_source_depth_and_never_offer_a_narrower_one(int bits, string[] expected)
        => Assert.Equal(expected, DeviceSamples.ExclusiveCandidates(bits).Select(f => f.ToString()));

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
}
