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

    [Theory]
    [InlineData("Speakers", "Speakers")] // not "Speakers (USB DAC)", listed first, which contains it
    [InlineData("speakers", "Speakers")]
    [InlineData("1", "1")] // a device named by digits: the name wins over the index
    [InlineData("0", "Speakers (USB DAC)")] // no device has that name: the index
    [InlineData("USB", "Speakers (USB DAC)")] // part of a name: the first that contains it
    public void A_whole_device_name_finds_that_device_before_an_index_or_a_part_of_a_name(string id, string expected)
    {
        var backend = new FakeBackend(Device("Speakers (USB DAC)", false), Device("Speakers", true), Device("1", false));
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(DeviceId: id));

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

    [Fact]
    public void An_exclusive_44k1_24bit_track_asks_for_exclusive_at_44k1_s24_and_reports_what_opened()
    {
        var backend = new FakeBackend(Device("FiiO BTR13", isDefault: true));
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions("FiiO BTR13", Exclusive: true));

        Assert.Null(output.Opened);
        output.Open(new Source(new PcmFormat(44_100, 2, 24)));

        var open = Assert.Single(backend.Opened);
        Assert.Equal((44_100, SampleFormat.S24, ShareMode.Exclusive), (open.Format.SampleRate, open.Format.Format, open.Config.Playback!.ShareMode));
        Assert.True(open.Config.Wasapi!.NoAutoConvertSRC);
        var opened = output.Opened!;
        Assert.Equal(("FiiO BTR13", true, 44_100, "s24", 24, true), (opened.Device, opened.Exclusive, opened.SampleRate, opened.SampleFormat, opened.BitsPerSample, opened.Verified));
        Assert.Null(opened.Notice);
        Assert.Equal("exclusive, 44.1 kHz 24-bit", opened.Describe());

        output.Stop();
        Assert.Null(output.Opened); // closed: nothing is in use
    }

    [Fact]
    public void A_device_that_opens_at_another_rate_is_reported_at_that_rate_with_a_resampling_notice()
    {
        // What #148 saw: asked for 44.1 kHz exclusive, the device ran at the Windows mix rate.
        var backend = new FakeBackend(Device("FiiO BTR13", isDefault: true))
        {
            OpenedAs = (d, f, _) => new OpenedOutput(d?.Name, true, 48_000, f.Channels, "s24in32", 24, Verified: true),
        };
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        output.Open(new Source(new PcmFormat(44_100, 2, 24)));

        var opened = output.Opened!;
        Assert.Equal(48_000, opened.SampleRate);
        Assert.Equal("exclusive, 48 kHz 24-bit", opened.Describe());
        Assert.Equal("The device runs at 48 kHz, not the track's 44.1 kHz: the audio is resampled.", opened.Notice);
    }

    [Fact]
    public void An_unverified_rate_is_not_flagged_as_resampled()
    {
        var backend = new FakeBackend
        {
            OpenedAs = (_, f, _) => new OpenedOutput(null, false, 48_000, f.Channels, "f32", 32, Verified: false),
        };
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());

        output.Open(new Source(new PcmFormat(44_100, 2, 32)));

        Assert.Null(output.Opened!.Notice); // shared, unverified: the OS mixer's rate is its business
        Assert.Equal("shared, 48 kHz 32-bit float", output.Opened.Describe());
    }

    [Fact]
    public void Exclusive_refused_and_played_shared_keeps_the_backends_reason_and_is_never_reported_as_exclusive()
    {
        var backend = new FakeBackend
        {
            OpenedAs = (_, f, _) => new OpenedOutput("BTR13", false, f.SampleRate, f.Channels, "s24", 24, Verified: false,
                Notice: "Exclusive mode unavailable: another app is using the device. Playing in shared mode through the Windows mixer."),
        };
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        output.Open(new Source(new PcmFormat(44_100, 2, 24)));

        Assert.False(output.Opened!.Exclusive);
        Assert.StartsWith("Exclusive mode unavailable: another app is using the device.", output.Opened.Notice);
        Assert.StartsWith("shared,", output.Opened.Describe());
    }

    [Fact]
    public void Exclusive_asked_but_shared_given_without_a_reason_still_gets_a_notice()
    {
        var backend = new FakeBackend { OpenedAs = (_, f, _) => new OpenedOutput(null, false, f.SampleRate, f.Channels, "s16", 16, Verified: false) };
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        output.Open(new Source(new PcmFormat(44_100, 2, 16)));

        Assert.Equal("Exclusive mode was requested, but the device opened in shared mode.", output.Opened!.Notice);
    }

    [Fact]
    public void Shared_mode_as_asked_has_no_notice()
    {
        var backend = new FakeBackend();
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());

        output.Open(new Source(new PcmFormat(44_100, 2, 16)));

        Assert.False(output.Opened!.Exclusive);
        Assert.Null(output.Opened.Notice);
    }

    [Fact]
    public void An_exclusive_open_that_fails_is_an_error_naming_exclusive_mode_and_reports_nothing_in_use()
    {
        var backend = new FakeBackend { OpenError = new InvalidOperationException("Result: ShareModeNotSupported") };
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        var ex = Assert.Throws<MusicSpinException>(() => output.Open(new Source(new PcmFormat(44_100, 2, 24))));

        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Equal("Could not open audio device in exclusive mode: Result: ShareModeNotSupported", ex.Message);
        Assert.False(output.IsOpen);
        Assert.Null(output.Opened);
    }

    [Fact]
    public void A_failed_reopen_drops_the_previous_tracks_format()
    {
        var backend = new FakeBackend();
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        Assert.NotNull(output.Opened);

        backend.OpenedAs = (_, _, _) => throw new InvalidOperationException("unplugged");
        Assert.Throws<MusicSpinException>(() => output.Open(new Source(new PcmFormat(96_000, 2, 24))));

        Assert.Null(output.Opened); // not still claiming 44.1 kHz exclusive
    }

    [Fact]
    public void The_device_rate_follows_each_tracks_format()
    {
        var backend = new FakeBackend();
        using var provider = new SoundFlowOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        var seen = new List<string>();
        foreach (var format in new[] { new PcmFormat(44_100, 2, 16), new PcmFormat(96_000, 2, 24), new PcmFormat(48_000, 2, 24) })
        {
            output.Open(new Source(format));
            seen.Add(output.Opened!.Describe());
        }

        Assert.Equal([44_100, 96_000, 48_000], backend.Opened.Select(o => o.Format.SampleRate));
        Assert.Equal(["exclusive, 44.1 kHz 16-bit", "exclusive, 96 kHz 24-bit", "exclusive, 48 kHz 24-bit"], seen);
        Assert.All(backend.Streams.Take(2), s => Assert.True(s.Disposed)); // each reopen released the previous device
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

    private sealed class FakeStream(OpenedOutput opened) : IPlaybackStream
    {
        public OpenedOutput Opened => opened;

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

        /// <summary>What the "device" reads back; default: exactly what was asked, verified.</summary>
        public Func<DeviceInfo?, AudioFormat, MiniAudioDeviceConfig, OpenedOutput>? OpenedAs { get; set; }

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
            var stream = new FakeStream(OpenedAs?.Invoke(device, format, config) ?? new OpenedOutput(
                device?.Name, config.Playback!.ShareMode == ShareMode.Exclusive, format.SampleRate, format.Channels,
                format.Format.ToString().ToLowerInvariant(), format.Format == SampleFormat.S16 ? 16 : format.Format == SampleFormat.S24 ? 24 : 32,
                Verified: true));
            Streams.Add(stream);
            return stream;
        }

        public void Dispose() => Disposed = true;
    }
}
