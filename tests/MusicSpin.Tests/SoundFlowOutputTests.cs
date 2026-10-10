using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output.SoundFlow;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace MusicSpin.Tests;

public class SoundFlowOutputTests
{
    [Theory]
    [InlineData(16, SampleFormat.S16)]
    [InlineData(24, SampleFormat.S24)]
    [InlineData(20, SampleFormat.S24)]
    [InlineData(32, SampleFormat.F32)]
    [InlineData(1, SampleFormat.F32)] // DSD is decoded to float PCM
    [InlineData(0, SampleFormat.F32)]
    public void Device_format_preserves_source_bit_depth(int bits, SampleFormat expected)
        => Assert.Equal(expected, SoundFlowAudioOutput.DeviceSampleFormat(bits));

    [Fact]
    public void Exclusive_mode_asks_every_backend_not_to_convert()
    {
        var config = SoundFlowAudioOutput.BuildConfig(new AudioOutputOptions(Exclusive: true));

        Assert.Equal(ShareMode.Exclusive, config.Playback!.ShareMode);
        Assert.True(config.NoClip);
        Assert.True(config.Wasapi!.NoAutoConvertSRC);
        Assert.True(config.Wasapi.NoDefaultQualitySRC);
        Assert.True(config.CoreAudio!.AllowNominalSampleRateChange);
        Assert.True(config.Alsa!.NoAutoFormat);
        Assert.True(config.Alsa.NoAutoResample);
        Assert.True(config.Alsa.NoAutoChannels);
    }

    [Fact]
    public void Shared_mode_leaves_conversion_to_the_os_but_never_clips()
    {
        var config = SoundFlowAudioOutput.BuildConfig(new AudioOutputOptions());

        Assert.Equal(ShareMode.Shared, config.Playback!.ShareMode);
        Assert.True(config.NoClip);
        Assert.False(config.Wasapi!.NoAutoConvertSRC);
        Assert.False(config.CoreAudio!.AllowNominalSampleRateChange);
        Assert.Null(config.Alsa);
    }

    [Fact]
    public void Devices_are_listed_with_index_ids_and_distinct_sorted_formats()
    {
        var backend = new FakeBackend(
            Device("Speakers", isDefault: true,
                Fmt(SampleFormat.S16, 48_000, 2), Fmt(SampleFormat.S24, 44_100, 2), Fmt(SampleFormat.S16, 44_100, 6),
                Fmt(SampleFormat.F32, 0, 0)),
            Device("USB DAC", isDefault: false));
        using var provider = new SoundFlowOutputProvider(() => backend);

        var devices = provider.GetDevices();

        Assert.Equal("SoundFlow/miniaudio (Fake)", provider.Name);
        Assert.Equal(2, devices.Count);
        var speakers = devices[0];
        Assert.Equal(("0", "Speakers", true, "Fake"), (speakers.Id, speakers.Name, speakers.IsDefault, speakers.Backend));
        Assert.Equal(["s16", "s24", "f32"], speakers.SampleFormats);
        Assert.Equal([44_100, 48_000], speakers.SampleRates);
        Assert.Equal([2, 6], speakers.Channels);
        Assert.Equal(("1", "USB DAC", false), (devices[1].Id, devices[1].Name, devices[1].IsDefault));
        Assert.Empty(devices[1].SampleFormats);
        Assert.Empty(devices[1].SampleRates);
    }

    [Fact]
    public void The_backend_is_created_once_and_disposed_with_the_provider()
    {
        var created = 0;
        var backend = new FakeBackend();
        var provider = new SoundFlowOutputProvider(() => { created++; return backend; });

        _ = provider.GetDevices();
        _ = provider.GetDevices();
        _ = provider.Name;
        provider.Dispose();

        Assert.Equal(1, created);
        Assert.True(backend.Disposed);
        provider.Dispose(); // idempotent
    }

    [Fact]
    public void A_backend_that_cannot_start_is_an_audio_device_error_and_the_name_says_unavailable()
    {
        using var provider = new SoundFlowOutputProvider(() => throw new InvalidOperationException("Result: NoBackend"));

        Assert.Equal("SoundFlow/miniaudio (unavailable)", provider.Name);
        var ex = Assert.Throws<MusicSpinException>(() => provider.GetDevices());
        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Contains("Could not initialise audio backend: Result: NoBackend", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void A_failing_device_enumeration_is_an_audio_device_error()
    {
        var backend = new FakeBackend { RefreshError = new InvalidOperationException("enumeration failed") };
        using var provider = new SoundFlowOutputProvider(() => backend);

        var ex = Assert.Throws<MusicSpinException>(() => provider.GetDevices());

        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Equal("Could not list devices: enumeration failed", ex.Message);
    }

    [Fact]
    public void Open_uses_the_sources_native_format_and_the_default_device_when_none_is_configured()
    {
        var backend = new FakeBackend(Device("Speakers", isDefault: true));
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        Assert.False(output.IsOpen);
        output.Open(new Source(new PcmFormat(96_000, 2, 24)));

        Assert.True(output.IsOpen);
        var open = Assert.Single(backend.Opened);
        Assert.Null(open.Device);
        Assert.Equal(96_000, open.Format.SampleRate);
        Assert.Equal(2, open.Format.Channels);
        Assert.Equal(SampleFormat.S24, open.Format.Format);
        Assert.Equal(ChannelLayout.Stereo, open.Format.Layout);
        Assert.Equal(ShareMode.Exclusive, open.Config.Playback!.ShareMode);
        Assert.Equal(96_000, open.Data.SampleRate);
    }

    [Theory]
    [InlineData("1", "USB DAC")]
    [InlineData("usb", "USB DAC")]
    [InlineData("SPEAK", "Speakers")]
    [InlineData("5", null)] // out of range index falls back to a name match; none here
    [InlineData("-1", null)]
    public void Devices_are_chosen_by_index_or_case_insensitive_name(string id, string? expected)
    {
        var backend = new FakeBackend(Device("Speakers", true), Device("USB DAC", false));
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(DeviceId: id));

        if (expected is null)
        {
            var ex = Assert.Throws<MusicSpinException>(() => output.Open(new Source(new PcmFormat(44_100, 2, 16))));
            Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
            Assert.Equal($"Audio device '{id}' not found.", ex.Message);
            Assert.False(output.IsOpen);
            Assert.Empty(backend.Opened);
            return;
        }

        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        Assert.Equal(expected, backend.Opened.Single().Device!.Value.Name);
    }

    [Fact]
    public void A_device_that_fails_to_open_is_wrapped_and_leaves_the_output_closed()
    {
        var backend = new FakeBackend { OpenError = new InvalidOperationException("format not supported") };
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());

        var ex = Assert.Throws<MusicSpinException>(() => output.Open(new Source(new PcmFormat(44_100, 2, 16))));

        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Equal("Could not open audio device: format not supported", ex.Message);
        Assert.False(output.IsOpen);
        Assert.Throws<MusicSpinException>(output.Start);
    }

    [Fact]
    public void Start_pause_and_stop_drive_the_open_stream_and_reopening_releases_the_old_one()
    {
        var backend = new FakeBackend();
        using var provider = new SoundFlowOutputProvider(() => backend);
        var output = provider.Create(new AudioOutputOptions());

        var start = Assert.Throws<MusicSpinException>(output.Start);
        Assert.Equal(MusicSpinErrorCode.InvalidState, start.Code);
        output.Pause(); // nothing open: no-op

        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        var first = backend.Streams[0];
        output.Start();
        output.Pause();
        output.Start();
        Assert.Equal(["start", "pause", "start"], first.Calls);

        output.Open(new Source(new PcmFormat(48_000, 2, 16)));
        Assert.True(first.Disposed);
        Assert.True(output.IsOpen);

        output.Stop();
        Assert.True(backend.Streams[1].Disposed);
        Assert.False(output.IsOpen);
        output.Dispose(); // idempotent
        Assert.Throws<ArgumentNullException>(() => output.Open(null!));
        Assert.Throws<ArgumentNullException>(() => provider.Create(null!));
    }

    [Fact]
    public void The_data_provider_pulls_from_the_source_and_reports_end_of_stream()
    {
        var source = new Source(new PcmFormat(48_000, 2, 24)) { Remaining = 5 };
        var provider = new PcmSourceDataProvider(source);
        var ends = 0;
        var positions = new List<int>();
        provider.EndOfStreamReached += (_, _) => ends++;
        provider.PositionChanged += (_, e) => positions.Add(e.NewPosition);

        Assert.Equal((-1, false, SampleFormat.F32, 48_000), (provider.Length, provider.CanSeek, provider.SampleFormat, provider.SampleRate));
        Assert.Null(provider.FormatInfo);

        var buffer = new float[4];
        Assert.Equal(4, provider.ReadBytes(buffer));
        Assert.Equal([1f, 2f, 3f, 4f], buffer);
        provider.Seek(100); // the Player owns seeking: ignored
        Assert.Equal(1, provider.ReadBytes(buffer));
        Assert.Equal(5, provider.Position);
        Assert.Equal([4, 5], positions);
        Assert.Equal(0, ends);

        Assert.Equal(0, provider.ReadBytes(buffer));
        Assert.Equal(1, ends);
        Assert.Equal(5, provider.Position);

        provider.Dispose();
        Assert.True(provider.IsDisposed);
        source.Remaining = 10;
        Assert.Equal(0, provider.ReadBytes(buffer)); // disposed: never touches the source again
        Assert.Equal(1, ends);
    }

    private static DeviceInfo Device(string name, bool isDefault, params NativeDataFormat[] formats)
        => new() { Name = name, IsDefault = isDefault, SupportedDataFormats = formats };

    private static NativeDataFormat Fmt(SampleFormat format, uint rate, uint channels)
        => new() { Format = format, SampleRate = rate, Channels = channels };

    private sealed class Source(PcmFormat format) : IPcmSource
    {
        private float _next = 1;

        public int Remaining { get; set; } = int.MaxValue;

        public PcmFormat Format => format;

        public int Read(Span<float> buffer)
        {
            var n = Math.Min(buffer.Length, Remaining);
            for (var i = 0; i < n; i++)
            {
                buffer[i] = _next++;
            }

            Remaining -= n;
            return n;
        }
    }

    private sealed record OpenCall(DeviceInfo? Device, AudioFormat Format, MiniAudioDeviceConfig Config, ISoundDataProvider Data);

    private sealed class FakeStream : IPlaybackStream
    {
        public List<string> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public void Start() => Calls.Add("start");

        public void Pause() => Calls.Add("pause");

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBackend(params DeviceInfo[] devices) : IAudioBackend
    {
        public Exception? RefreshError { get; init; }

        public Exception? OpenError { get; init; }

        public List<OpenCall> Opened { get; } = [];

        public List<FakeStream> Streams { get; } = [];

        public bool Disposed { get; private set; }

        public string ActiveBackend => "Fake";

        public IReadOnlyList<DeviceInfo> RefreshPlaybackDevices()
            => RefreshError is null ? devices : throw RefreshError;

        public IPlaybackStream Open(DeviceInfo? device, AudioFormat format, MiniAudioDeviceConfig config, ISoundDataProvider data)
        {
            if (OpenError is not null)
            {
                throw OpenError;
            }

            Opened.Add(new OpenCall(device, format, config, data));
            var stream = new FakeStream();
            Streams.Add(stream);
            return stream;
        }

        public void Dispose() => Disposed = true;
    }
}
