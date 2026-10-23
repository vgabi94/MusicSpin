using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output;
using MusicSpin.Core.Output.MiniAudio;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

/// <summary>
/// The real native layer (native/msaudio, built for this RID) on miniaudio's Null backend: struct layout, device listing,
/// what opened, unhappy paths, and samples rendered through miniaudio's own playback path (client-to-device conversion
/// included) compared bit for bit.
/// </summary>
public sealed unsafe class MsAudioNativeTests : IDisposable
{
    private readonly MiniAudioBackend _backend = new(nullBackend: true);

    public void Dispose() => _backend.Dispose();

    [Fact]
    public void The_managed_structs_match_the_native_layout()
    {
        Assert.Equal(MsAudioNative.AbiVersion, MsAudioNative.AbiVersionOf());
        Assert.Equal(Marshal.SizeOf<MsAudioNative.NativeFormat>(), MsAudioNative.Layout(0, 0));
        Assert.Equal(sizeof(MsAudioNative.DeviceInfo), MsAudioNative.Layout(0, 1));
        Assert.Equal(sizeof(MsAudioNative.OpenParams), MsAudioNative.Layout(0, 2));
        Assert.Equal(sizeof(MsAudioNative.Opened), MsAudioNative.Layout(0, 3));
        AssertOffsets<MsAudioNative.NativeFormat>(1, "Format", "Channels", "SampleRate", "Flags");
        AssertOffsets<MsAudioNative.DeviceInfo>(2, "Id", "Name", "IsDefault", "NativeFormatCount", "Formats");
        AssertOffsets<MsAudioNative.OpenParams>(3, "Format", "Channels", "SampleRate", "Exclusive", "PeriodFrames", "HasDeviceId", "DeviceId");
        AssertOffsets<MsAudioNative.Opened>(4, "Format", "Channels", "SampleRate", "Exclusive", "PeriodFrames", "Name");
        Assert.Equal(-1, MsAudioNative.Layout(9, 0)); // unknown struct
        Assert.Equal(-1, MsAudioNative.Layout(1, 9)); // unknown field
    }

    private static void AssertOffsets<T>(int which, params string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            Assert.True((int)Marshal.OffsetOf<T>(fields[i]) == MsAudioNative.Layout(which, i), $"{typeof(T).Name}.{fields[i]}");
        }
    }

    [Fact]
    public void The_null_backend_lists_its_device()
    {
        Assert.Equal("Null", _backend.ActiveBackend);
        var device = Assert.Single(_backend.RefreshPlaybackDevices());
        Assert.False(string.IsNullOrWhiteSpace(device.Name));
        Assert.Equal(MsAudioNative.IdBytes, device.Id.Length);
        Assert.Null(device.EndpointId);
    }

    [Theory]
    [InlineData(nameof(DeviceSample.S16), 44_100, "s16", 16)]
    [InlineData(nameof(DeviceSample.S24), 96_000, "s24", 24)]
    [InlineData(nameof(DeviceSample.S32), 192_000, "s32", 32)]
    [InlineData(nameof(DeviceSample.F32), 48_000, "f32", 32)]
    public void Opened_reports_the_format_miniaudio_really_opened(string format, int rate, string name, int bits)
    {
        var device = _backend.RefreshPlaybackDevices()[0];
        using var stream = _backend.Open(device, new OpenRequest(Enum.Parse<DeviceSample>(format), rate, 2, false, bits), Silence(rate));

        var opened = stream.Opened;
        Assert.Equal((device.Name, false, rate, 2, name, bits, true), (opened.Device, opened.Exclusive, opened.SampleRate, opened.Channels, opened.SampleFormat, opened.BitsPerSample, opened.Verified));
        Assert.Null(opened.Notice);
    }

    [Fact]
    public void A_reroute_reads_back_the_device_as_it_runs_now_and_is_reported_once()
    {
        var device = _backend.RefreshPlaybackDevices()[0];
        using var stream = (MiniAudioStream)_backend.Open(device, new OpenRequest(DeviceSample.S16, 44_100, 2, false, 16), Silence(44_100));
        stream.Notice = "stale";
        var rerouted = 0;
        stream.Rerouted += (_, _) => rerouted++;

        stream.OnRerouted(); // what miniaudio's reroute notification runs

        Assert.Equal(1, rerouted);
        Assert.Equal((device.Name, 44_100, 2, "s16", 16), (stream.Opened.Device, stream.Opened.SampleRate, stream.Opened.Channels, stream.Opened.SampleFormat, stream.Opened.BitsPerSample));
        Assert.Null(stream.Opened.Notice); // the old device's notice no longer applies
    }

    [Theory]
    [InlineData(44_100, 0)] // no channels
    [InlineData(44_100, 300)] // more than miniaudio's maximum
    [InlineData(7_999, 2)] // below miniaudio's range
    [InlineData(800_000, 2)] // above it
    public void An_unsupported_rate_or_channel_count_is_refused_as_invalid(int rate, int channels)
    {
        var ex = Assert.Throws<MiniAudioException>(() => _backend.Open(null, new OpenRequest(DeviceSample.F32, rate, channels, false, 32), Silence(rate)));
        Assert.Equal(MsAudioNative.InvalidArgs, ex.Result);
    }

    [Fact]
    public void An_unsupported_sample_format_is_refused_before_reaching_native_code()
        => Assert.Throws<ArgumentOutOfRangeException>(() => _backend.Open(null, new OpenRequest(DeviceSample.S24In32, 44_100, 2, false, 24), Silence(44_100)));

    [Fact]
    public void Invalid_arguments_to_the_flat_api_are_refused_not_crashed_on()
    {
        Assert.Equal(MsAudioNative.InvalidArgs, MsAudioNative.DeviceInfoAt(0, 0, null));
        Assert.Equal(MsAudioNative.InvalidArgs, MsAudioNative.ContextRefresh(0, out _));
        Assert.Equal(MsAudioNative.InvalidArgs, MsAudioNative.DeviceStart(0));
        Assert.Equal(MsAudioNative.InvalidArgs, MsAudioNative.DeviceRender(0, null, 1));
        Assert.Equal(MsAudioNative.InvalidArgs, MsAudioNative.DeviceOpened(0, null));
        MsAudioNative.DeviceClose(0); // no-op
        Assert.False(string.IsNullOrEmpty(MsAudioNative.Describe(MsAudioNative.InvalidArgs)));
    }

    [Fact]
    public void A_device_index_out_of_range_is_refused()
    {
        var info = new MsAudioNative.DeviceInfo();
        var ctx = CreateContext();
        try
        {
            Assert.Equal(MsAudioNative.Success, MsAudioNative.ContextRefresh(ctx, out var n));
            Assert.Equal(MsAudioNative.InvalidArgs, MsAudioNative.DeviceInfoAt(ctx, n, &info));
            Assert.Equal(MsAudioNative.InvalidArgs, MsAudioNative.DeviceInfoAt(ctx, -1, &info));
        }
        finally
        {
            MsAudioNative.ContextUninit(ctx);
        }
    }

    [Fact]
    public void An_unknown_device_name_is_an_audio_device_error_and_opens_nothing()
    {
        using var provider = new MiniAudioOutputProvider(nullBackend: true);
        using var output = provider.Create(new AudioOutputOptions(DeviceId: "FiiO BTR13"));

        var ex = Assert.Throws<MusicSpinException>(() => output.Open(Silence(44_100)));

        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Equal("Audio device 'FiiO BTR13' not found.", ex.Message);
        Assert.False(output.IsOpen);
    }

    [Fact]
    public void A_stale_device_id_that_names_no_device_fails_or_falls_back_but_never_crashes()
    {
        // ids the backend never handed out (on Null any id is its one device; real backends refuse with a result)
        var bogus = new BackendDevice("gone", false, Enumerable.Repeat((byte)0xAB, MsAudioNative.IdBytes).ToArray(), []);
        try
        {
            using var stream = _backend.Open(bogus, new OpenRequest(DeviceSample.F32, 48_000, 2, false, 32), Silence(48_000));
            Assert.Equal(48_000, stream.Opened.SampleRate);
        }
        catch (MiniAudioException ex)
        {
            Assert.NotEqual(MsAudioNative.Success, ex.Result);
        }
    }

    [Fact]
    public void Each_track_reopens_at_its_own_rate_through_the_real_output()
    {
        using var provider = new MiniAudioOutputProvider(nullBackend: true);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        var seen = new List<int>();
        foreach (var rate in new[] { 44_100, 96_000, 48_000 })
        {
            output.Open(Silence(rate, bits: 24));
            seen.Add(output.Opened!.SampleRate);
        }

        Assert.Equal([44_100, 96_000, 48_000], seen);
    }

    [Fact]
    public void Paused_renders_silence_without_reading_the_source()
    {
        var source = new CountingSource(new PcmFormat(48_000, 2, 16));
        using var stream = (MiniAudioStream)_backend.Open(null, new OpenRequest(DeviceSample.S16, 48_000, 2, false, 16), source);
        var bytes = new byte[64 * 2 * 2];
        Array.Fill(bytes, (byte)0x55);

        Assert.Equal(MsAudioNative.Success, stream.RenderForTest(bytes, 64));

        Assert.All(bytes, b => Assert.Equal(0, b));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void Pausing_a_running_device_stops_it_without_calling_it_lost_and_starting_again_resumes_it()
    {
        var source = new WaitableSource(new PcmFormat(48_000, 2, 16));
        using var stream = (MiniAudioStream)_backend.Open(null, new OpenRequest(DeviceSample.S16, 48_000, 2, false, 16), source);
        var lost = new List<string>();
        stream.Lost += (_, reason) => lost.Add(reason);
        var bytes = new byte[64 * 2 * 2];

        stream.Start();
        Assert.True(source.WaitForReads(1)); // the device thread is pulling
        stream.Pause();

        Assert.Empty(lost); // miniaudio reports its Stopped notification before Stop returns: ours is not a lost device
        var heard = source.Reads;
        Assert.Equal(MsAudioNative.Success, stream.RenderForTest(bytes, 64));
        Assert.Equal(heard, source.Reads); // nothing is read once paused
        Assert.All(bytes, b => Assert.Equal(0, b));

        stream.Start();
        Assert.True(source.WaitForReads(heard + 1)); // and the same device plays on
        stream.Pause();
        Assert.Empty(lost);
    }

    [Fact]
    public void A_stop_that_was_not_ours_is_still_reported_as_a_lost_device()
    {
        using var stream = (MiniAudioStream)_backend.Open(null, new OpenRequest(DeviceSample.S16, 48_000, 2, false, 16), Silence(48_000));
        var lost = new List<string>();
        stream.Lost += (_, reason) => lost.Add(reason);

        stream.RaiseLost("unplugged"); // what OnNotify does for a Stopped nobody asked for

        Assert.Equal(["unplugged"], lost);
    }

    [Fact]
    public void A_period_bigger_than_the_prepared_conversion_buffer_still_converts_every_sample()
    {
        using var stream = (MiniAudioStream)_backend.Open(null, new OpenRequest(DeviceSample.S16, 48_000, 2, false, 16), new ConstantSource(new PcmFormat(48_000, 2, 16), 0.5f));
        stream.ResumeForTest();
        const int frames = 10_000; // more than the buffer prepared for the device's period
        var bytes = new byte[frames * 2 * 2];

        Assert.Equal(MsAudioNative.Success, stream.RenderForTest(bytes, frames));

        Assert.All(MemoryMarshal.Cast<byte, short>(bytes).ToArray(), v => Assert.Equal(16_384, v));
    }

    [Fact]
    public void A_throwing_source_renders_silence_instead_of_crashing_the_audio_thread()
    {
        using var stream = (MiniAudioStream)_backend.Open(null, new OpenRequest(DeviceSample.S16, 48_000, 2, false, 16), new ThrowingSource());
        stream.ResumeForTest();
        var bytes = new byte[64 * 2 * 2];
        Array.Fill(bytes, (byte)0x55);

        Assert.Equal(MsAudioNative.Success, stream.RenderForTest(bytes, 64));

        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    public static TheoryData<string, int> BitExactCases => new()
    {
        { nameof(DeviceSample.S16), 16 },
        { nameof(DeviceSample.S24), 24 },
        { nameof(DeviceSample.S32), 32 },
        { nameof(DeviceSample.F32), 32 },
    };

    [Theory]
    [MemberData(nameof(BitExactCases))]
    public void At_volume_one_the_source_samples_reach_the_device_bit_for_bit(string format, int bits)
    {
        var sample = Enum.Parse<DeviceSample>(format);
        var (floats, expected) = Ramp(sample, bits);

        var rendered = RenderThroughPlaybackBuffer(sample, bits, floats, volume: 1f);

        Assert.Equal(expected, rendered);
    }

    [Theory]
    [InlineData(nameof(DeviceSample.F32), 0.5f)]
    [InlineData(nameof(DeviceSample.F32), 0.1f)]
    [InlineData(nameof(DeviceSample.S16), 0.5f)]
    [InlineData(nameof(DeviceSample.S24), 0.25f)]
    public void Below_volume_one_the_only_change_is_our_gain(string format, float volume)
    {
        var sample = Enum.Parse<DeviceSample>(format);
        var bits = DeviceSamples.ValidBits(sample);
        var (floats, _) = Ramp(sample, bits);

        var rendered = RenderThroughPlaybackBuffer(sample, bits, floats, volume);

        // Exactly the source times our volume, converted once: no pan law, no mixer, no second gain stage.
        var scaled = floats.Select(x => x * volume).ToArray();
        var expected = new byte[scaled.Length * DeviceSamples.ContainerBytes(sample)];
        DeviceSamples.Convert(scaled, expected, sample);
        Assert.Equal(expected, rendered);

        if (sample == DeviceSample.F32)
        {
            // In float the level change is exactly 20*log10(volume) dB for every nonzero sample.
            var output = MemoryMarshal.Cast<byte, float>(rendered);
            for (var i = 0; i < floats.Length; i++)
            {
                if (floats[i] != 0)
                {
                    Assert.Equal(20 * Math.Log10(volume), 20 * Math.Log10(output[i] / floats[i]), 5);
                }
            }
        }
    }

    /// <summary>Full-scale extremes, zero and a spread of values, as floats (decoder scale) and the device bytes they must become.</summary>
    private static (float[] Floats, byte[] Expected) Ramp(DeviceSample sample, int bits)
    {
        var values = new List<float>();
        var expected = new List<byte>();
        if (sample == DeviceSample.F32)
        {
            foreach (var v in new[] { -1f, -0.5f, -1e-7f, 0f, 1e-7f, 0.333333343f, 0.999999940f, 0.123456791f })
            {
                values.Add(v);
                expected.AddRange(BitConverter.GetBytes(v));
            }
        }
        else
        {
            var full = 1L << (bits - 1);
            // s32 from float: only values float holds exactly (24 significant bits) are a fair test of exactness
            var step = bits == 32 ? 256L : 1L;
            var ints = new List<long> { -full, -full + step, -step, 0, step, full - step };
            for (long k = -full; k < full; k += full / 37 / step * step)
            {
                ints.Add(k);
            }

            foreach (var k in ints)
            {
                values.Add((float)((double)k / full));
                var size = DeviceSamples.ContainerBytes(sample);
                var b = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(b, k);
                expected.AddRange(b.AsSpan(0, size).ToArray());
            }
        }

        // stereo: an even sample count
        if (values.Count % 2 == 1)
        {
            values.Add(values[0]);
            expected.AddRange(expected.Take(expected.Count / (values.Count - 1)).ToArray());
        }

        return (values.ToArray(), expected.ToArray());
    }

    private byte[] RenderThroughPlaybackBuffer(DeviceSample sample, int bits, float[] floats, float volume)
    {
        const int rate = 44_100;
        var format = new PcmFormat(rate, 2, bits);
        var decoder = new FakeDecoder(TestAudio.Track("/music/ramp.flac", rate, 2, 1, bits));
        var buffer = new PlaybackBuffer(format, 4096, new TrackSegment(decoder, null, TimeSpan.Zero, [], []), () => volume, () => { });
        buffer.Write(floats);

        using var stream = (MiniAudioStream)_backend.Open(null, new OpenRequest(sample, rate, 2, false, bits), buffer);
        Assert.Equal(DeviceSamples.Name(sample), stream.Opened.SampleFormat); // no conversion inside miniaudio
        stream.ResumeForTest();
        var bytes = new byte[floats.Length * DeviceSamples.ContainerBytes(sample)];
        Assert.Equal(MsAudioNative.Success, stream.RenderForTest(bytes, floats.Length / 2));
        return bytes;
    }

    private static nint CreateContext()
    {
        Assert.Equal(MsAudioNative.Success, MsAudioNative.ContextInit(1, out var ctx));
        return ctx;
    }

    private static CountingSource Silence(int rate, int bits = 32) => new(new PcmFormat(rate, 2, bits));

    private sealed class CountingSource(PcmFormat format) : IPcmSource
    {
        public int Reads { get; private set; }

        public PcmFormat Format => format;

        public int Read(Span<float> buffer)
        {
            Reads++;
            buffer.Clear();
            return buffer.Length;
        }
    }

    /// <summary>Counts reads on the device thread and lets a test wait for them without sleeping.</summary>
    private sealed class WaitableSource(PcmFormat format) : IPcmSource
    {
        private readonly object _gate = new();
        private int _reads;

        public int Reads
        {
            get { lock (_gate) { return _reads; } }
        }

        public PcmFormat Format => format;

        public int Read(Span<float> buffer)
        {
            buffer.Clear();
            lock (_gate)
            {
                _reads++;
                Monitor.PulseAll(_gate);
            }

            return buffer.Length;
        }

        /// <summary>True once at least <paramref name="count"/> reads have happened (bounded; a hang means the device never ran).</summary>
        public bool WaitForReads(int count)
        {
            var deadline = TimeSpan.FromSeconds(30);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            lock (_gate)
            {
                while (_reads < count)
                {
                    var left = deadline - clock.Elapsed;
                    if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                    {
                        return _reads >= count;
                    }
                }

                return true;
            }
        }
    }

    private sealed class ConstantSource(PcmFormat format, float value) : IPcmSource
    {
        public PcmFormat Format => format;

        public int Read(Span<float> buffer)
        {
            buffer.Fill(value);
            return buffer.Length;
        }
    }

    private sealed class ThrowingSource : IPcmSource
    {
        public PcmFormat Format => new(48_000, 2, 16);

        public int Read(Span<float> buffer) => throw new InvalidOperationException("decoder blew up");
    }
}
