using System.Buffers.Binary;
using MusicSpin.Core.Audio;

namespace MusicSpin.Core.Output;

/// <summary>A sample format a device can be opened with (miniaudio formats plus WASAPI exclusive's 24-in-32).</summary>
internal enum DeviceSample
{
    S16,

    /// <summary>24-bit packed in 3 bytes.</summary>
    S24,

    /// <summary>24 valid bits, left-justified in a 32-bit container (what many USB DACs accept for 24-bit).</summary>
    S24In32,

    S32,
    F32,
}

/// <summary>Format choice and float-to-device conversion for the device outputs. Pure, so it is tested on every OS.</summary>
internal static class DeviceSamples
{
    /// <summary>
    /// The formats to try, best first, for a source of <paramref name="sourceBits"/> bits. Every candidate holds the
    /// source's samples exactly (a 16-bit sample padded into 24 or 32 bits is the same value); only float/32-bit
    /// sources fall back to integer containers, which still hold every 24-bit value float32 can carry.
    /// </summary>
    public static IReadOnlyList<DeviceSample> ExclusiveCandidates(int sourceBits) => sourceBits switch
    {
        > 0 and <= 16 when sourceBits != 1 => [DeviceSample.S16, DeviceSample.S24, DeviceSample.S24In32, DeviceSample.S32],
        // 24-in-32 first: it is what USB Audio Class 2 DACs (and most drivers) take for 24-bit; packed 24 is rarer.
        > 16 and <= 24 => [DeviceSample.S24In32, DeviceSample.S32, DeviceSample.S24],
        _ => [DeviceSample.F32, DeviceSample.S32, DeviceSample.S24In32, DeviceSample.S24],
    };

    /// <summary>AUDCLNT_E_UNSUPPORTED_FORMAT and E_INVALIDARG: this format is refused, the next one may not be.</summary>
    private static bool IsFormatRefusal(int hr) => unchecked((uint)hr) is 0x88890008 or 0x80070057;

    /// <summary>
    /// Picks the first candidate the device both reports as supported and actually initializes with. Some drivers
    /// accept a format in IsFormatSupported and then refuse it in Initialize, so a format refusal from either moves on
    /// to the next candidate; any other failure (device in use, exclusive not allowed, unplugged) ends the search, as
    /// no other format would help. Pure over the two probes, so the order and fallbacks are tested on every OS.
    /// </summary>
    public static ExclusiveNegotiation Negotiate(IReadOnlyList<DeviceSample> candidates, Func<DeviceSample, int> isSupported, Func<DeviceSample, int> initialize)
    {
        var refused = new List<string>();
        foreach (var candidate in candidates)
        {
            if (isSupported(candidate) != 0)
            {
                refused.Add(Name(candidate));
                continue;
            }

            var hr = initialize(candidate);
            if (hr == 0)
            {
                return new ExclusiveNegotiation(candidate, 0, refused);
            }

            if (!IsFormatRefusal(hr))
            {
                return new ExclusiveNegotiation(null, hr, refused);
            }

            refused.Add(Name(candidate) + " (refused at initialize)");
        }

        return new ExclusiveNegotiation(null, 0, refused);
    }

    public static int ContainerBytes(DeviceSample format) => format switch
    {
        DeviceSample.S16 => 2,
        DeviceSample.S24 => 3,
        _ => 4,
    };

    public static int ValidBits(DeviceSample format) => format switch
    {
        DeviceSample.S16 => 16,
        DeviceSample.S24 or DeviceSample.S24In32 => 24,
        _ => 32,
    };

    public static string Name(DeviceSample format) => format switch
    {
        DeviceSample.S16 => "s16",
        DeviceSample.S24 => "s24",
        DeviceSample.S24In32 => "s24in32",
        DeviceSample.S32 => "s32",
        _ => "f32",
    };

    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="destination"/> (little-endian) in <paramref name="format"/>.
    /// Uses the decoder's scale (full scale = 2^(bits-1)) and rounds, so a 16- or 24-bit source comes out as the
    /// original integers; out-of-range samples are clipped, never wrapped.
    /// </summary>
    public static void Convert(ReadOnlySpan<float> source, Span<byte> destination, DeviceSample format)
    {
        var size = ContainerBytes(format);
        if (destination.Length < source.Length * size)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        // One tight loop per format: the format is not re-checked for every sample.
        switch (format)
        {
            case DeviceSample.F32:
                for (var i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 4)..], source[i]);
                }

                break;
            case DeviceSample.S16:
                for (var i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], (short)Scale(source[i], 15));
                }

                break;
            case DeviceSample.S24:
                for (var i = 0; i < source.Length; i++)
                {
                    var s24 = (int)Scale(source[i], 23);
                    var d = destination.Slice(i * 3, 3);
                    d[0] = (byte)s24;
                    d[1] = (byte)(s24 >> 8);
                    d[2] = (byte)(s24 >> 16);
                }

                break;
            case DeviceSample.S24In32:
                for (var i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], (int)Scale(source[i], 23) << 8);
                }

                break;
            default:
                for (var i = 0; i < source.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], (int)Scale(source[i], 31));
                }

                break;
        }
    }

    private static long Scale(float x, int bits)
    {
        var full = 1L << bits;
        if (float.IsNaN(x))
        {
            return 0;
        }

        var v = (long)Math.Round((double)x * full, MidpointRounding.ToEven);
        return Math.Clamp(v, -full, full - 1);
    }
}

/// <summary>Pulls whole buffers from an <see cref="IPcmSource"/>, which may return fewer samples than asked.</summary>
internal static class PcmReading
{
    /// <summary>Fills <paramref name="buffer"/>; whatever the source cannot give (end of stream, underrun) is silence.</summary>
    /// <returns>The samples the source gave.</returns>
    public static int Fill(IPcmSource source, Span<float> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = source.Read(buffer[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        buffer[total..].Clear();
        return total;
    }
}

/// <summary>The outcome of <see cref="DeviceSamples.Negotiate"/>: the format opened, or the failure that stopped the search (0 when every candidate was refused).</summary>
internal sealed record ExclusiveNegotiation(DeviceSample? Chosen, int FailedHr, IReadOnlyList<string> Refused);
