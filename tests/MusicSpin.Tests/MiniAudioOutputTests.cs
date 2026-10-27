using System.Collections.Concurrent;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output;
using MusicSpin.Core.Output.MiniAudio;

namespace MusicSpin.Tests;

public class MiniAudioOutputTests
{
    [Theory]
    [InlineData(16, nameof(DeviceSample.S16))]
    [InlineData(24, nameof(DeviceSample.S24))]
    [InlineData(20, nameof(DeviceSample.S24))]
    [InlineData(32, nameof(DeviceSample.F32))]
    [InlineData(1, nameof(DeviceSample.F32))] // DSD is decoded to float PCM
    [InlineData(0, nameof(DeviceSample.F32))]
    public void Exclusive_mode_hands_the_device_the_source_bit_depth(int bits, string expected)
        => Assert.Equal(Enum.Parse<DeviceSample>(expected), MiniAudioOutput.DeviceSampleFormat(bits, exclusive: true));

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Shared_mode_always_hands_the_os_mixer_float32(int bits)
        => Assert.Equal(DeviceSample.F32, MiniAudioOutput.DeviceSampleFormat(bits, exclusive: false));

    [Fact]
    public void The_request_carries_the_tracks_channels_and_depth_and_in_exclusive_mode_its_rate()
    {
        Assert.Equal(new OpenRequest(DeviceSample.S16, 44_100, 2, true, 16), MiniAudioOutput.BuildRequest(new PcmFormat(44_100, 2, 16), new AudioOutputOptions(Exclusive: true)));
        // Shared: rate 0, the device's own (the OS mixer's); the track is converted to it.
        Assert.Equal(new OpenRequest(DeviceSample.F32, 0, 6, false, 24), MiniAudioOutput.BuildRequest(new PcmFormat(96_000, 6, 24), new AudioOutputOptions()));
    }

    [Fact]
    public void A_lost_default_device_reopens_on_the_new_default_from_the_same_source_without_stopping()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var failed = 0;
        var moves = new List<AudioOutputReroutedEventArgs>();
        output.Failed += (_, _) => failed++;
        output.Rerouted += (_, e) => moves.Add(e);
        var source = new Source(new PcmFormat(44_100, 2, 16));
        output.Open(source);
        output.Start();
        backend.OpenedAs = (_, r) => new OpenedOutput("Headphones", false, 48_000, r.Channels, "f32", 32, Verified: true);

        backend.Streams[0].Lose("unplugged");
        Assert.Same(backend.Streams[0], ((MiniAudioOutput)output).Stream); // nothing happens on the audio thread
        posted.RunAll();

        Assert.True(backend.Streams[0].Disposed);
        Assert.Equal(2, backend.Opened.Count);
        Assert.Null(backend.Opened[1].Device); // the system default, whatever it is now
        Assert.Same(source, backend.Opened[1].Data); // the same reader: it goes on from where it was
        Assert.Equal(["start"], backend.Streams[1].Calls);
        Assert.Equal(0, failed);
        var move = Assert.Single(moves);
        Assert.Equal("Headphones", move.Opened.Device);
        Assert.Null(move.Message); // the default moved, as asked; the resampling is in the notice
        Assert.Contains("resampled", move.Opened.Notice, StringComparison.Ordinal);
        Assert.Same(move.Opened, output.Opened);
    }

    [Fact]
    public void A_device_lost_while_paused_reopens_but_stays_paused()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        output.Pause();

        backend.Streams[0].Lose("unplugged");
        posted.RunAll();

        Assert.True(output.IsOpen);
        Assert.Empty(backend.Streams[1].Calls);
    }

    [Fact]
    public void When_no_device_can_be_opened_after_a_loss_the_output_closes_and_fails_once()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var reasons = new List<string>();
        output.Failed += (_, e) => reasons.Add(e.Reason);
        output.Rerouted += (_, _) => Assert.Fail("not rerouted");
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        backend.OpenError = new InvalidOperationException("No output device");

        backend.Streams[0].Lose("unplugged.");
        backend.Streams[0].Lose("unplugged again.");
        posted.RunAll();

        var reason = Assert.Single(reasons);
        Assert.StartsWith("unplugged. Could not open audio device: No output device", reason, StringComparison.Ordinal);
        Assert.False(output.IsOpen);
        Assert.Null(output.Opened);
        Assert.True(backend.Streams[0].Disposed);
    }

    [Fact]
    public void When_the_new_device_will_not_start_the_output_closes_and_fails()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var reasons = new List<string>();
        output.Failed += (_, e) => reasons.Add(e.Reason);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        backend.StartError = new InvalidOperationException("device busy");

        backend.Streams[0].Lose("unplugged.");
        posted.RunAll();

        Assert.Contains("device busy", Assert.Single(reasons), StringComparison.Ordinal);
        Assert.False(output.IsOpen);
        Assert.True(backend.Streams[1].Disposed);
    }

    [Fact]
    public void A_lost_chosen_device_falls_back_to_the_default_says_so_and_returns_when_it_is_back()
    {
        var dac = Device("FiiO BTR13", isDefault: false);
        var speakers = Device("Speakers", isDefault: true);
        var backend = new FakeBackend(speakers, dac);
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions("FiiO BTR13"));
        var moves = new List<AudioOutputReroutedEventArgs>();
        output.Rerouted += (_, e) => moves.Add(e);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        Assert.Same(dac, backend.Opened[0].Device);

        backend.Devices.Remove(dac);
        backend.Streams[0].Lose("unplugged");
        posted.RunAll();

        Assert.Null(backend.Opened[1].Device);
        var move = Assert.Single(moves);
        Assert.StartsWith("'FiiO BTR13' is not available: playing on the system default device.", move.Message, StringComparison.Ordinal);
        Assert.Equal(["start"], backend.Streams[1].Calls);

        // The next track: still on the default while the DAC is gone (not "device not found")...
        output.Open(new Source(new PcmFormat(48_000, 2, 16)));
        Assert.Null(backend.Opened[2].Device);
        Assert.Contains("'FiiO BTR13' is not available", output.Opened!.Notice, StringComparison.Ordinal);

        // ...and back on it once it is plugged in again.
        backend.Devices.Add(dac);
        output.Open(new Source(new PcmFormat(48_000, 2, 16)));
        Assert.Same(dac, backend.Opened[3].Device);
        Assert.Null(output.Opened!.Notice);
    }

    [Fact]
    public void Exclusive_refused_on_the_new_device_plays_shared_and_says_so()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        var moves = new List<AudioOutputReroutedEventArgs>();
        output.Rerouted += (_, e) => moves.Add(e);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        // Bluetooth: the backend falls back to shared and says why.
        backend.OpenedAs = (_, r) => new OpenedOutput("Bluetooth", false, 44_100, r.Channels, "f32", 32, Verified: true,
            Notice: "Exclusive mode unavailable: refused. Playing in shared mode.");

        backend.Streams[0].ChangeDefault();
        posted.RunAll();

        var move = Assert.Single(moves);
        Assert.Equal("Exclusive mode unavailable: refused. Playing in shared mode.", move.Message);
        Assert.False(output.Opened!.Exclusive);
        Assert.True(backend.Opened[1].Request.Exclusive); // it still asks: the setting is kept
        Assert.Equal(["start"], backend.Streams[1].Calls);
    }

    [Fact]
    public void Rapid_repeated_notifications_reopen_once_and_a_replaced_devices_late_ones_are_dropped()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var moves = 0;
        output.Rerouted += (_, _) => moves++;
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        backend.Streams[0].ChangeDefault();
        backend.Streams[0].ChangeDefault();
        backend.Streams[0].Lose("unplugged");
        backend.Streams[0].Reroute();
        Assert.Equal(1, posted.Count); // one hand-off for the burst
        posted.RunAll();

        backend.Streams[0].ChangeDefault(); // the replaced device, late
        backend.Streams[0].Lose("late");
        posted.RunAll();

        Assert.Equal(2, backend.Opened.Count);
        Assert.Equal(1, moves);
        Assert.Same(backend.Streams[1], ((MiniAudioOutput)output).Stream);
    }

    [Fact]
    public void A_device_lost_during_a_track_switch_does_not_disturb_the_new_track()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var events = 0;
        output.Rerouted += (_, _) => events++;
        output.Failed += (_, _) => events++;
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        backend.Streams[0].Lose("unplugged"); // the hand-off is queued...
        var next = new Source(new PcmFormat(96_000, 2, 24));
        output.Open(next); // ...and the player opens the next track (on the new default) before it runs
        output.Start();
        posted.RunAll();

        Assert.Equal(2, backend.Opened.Count);
        Assert.Same(next, backend.Opened[1].Data);
        Assert.False(backend.Streams[1].Disposed);
        Assert.Equal(0, events);

        backend.Streams[1].Lose("unplugged too"); // the new device can still be followed
        posted.RunAll();
        Assert.Same(next, backend.Opened[2].Data);
        Assert.Equal(1, events);
    }

    [Fact]
    public void A_stop_before_the_hand_off_runs_leaves_the_output_closed()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        backend.Streams[0].Lose("unplugged");
        output.Stop();
        posted.RunAll();

        Assert.Single(backend.Opened);
        Assert.False(output.IsOpen);
    }

    [Fact]
    public void A_backend_reroute_updates_what_is_reported_without_reopening()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var moves = new List<AudioOutputReroutedEventArgs>();
        output.Rerouted += (_, e) => moves.Add(e);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        backend.Streams[0].Opened = new OpenedOutput("Headphones", false, 48_000, 2, "f32", 32, Verified: true);
        backend.Streams[0].Reroute();
        posted.RunAll();

        Assert.Single(backend.Opened);
        var move = Assert.Single(moves);
        Assert.Equal("Headphones", output.Opened!.Device);
        Assert.Null(move.Message);
        Assert.False(backend.Streams[0].Disposed);
    }

    [Fact]
    public async Task By_default_the_hand_off_runs_on_the_thread_pool_not_the_audio_thread()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var moved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.Rerouted += (_, _) => moved.TrySetResult(Thread.CurrentThread.IsThreadPoolThread);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        backend.Streams[0].Lose("unplugged");

        Assert.True(await moved.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Devices_are_listed_with_index_ids_and_distinct_sorted_formats()
    {
        var backend = new FakeBackend(
            Device("Speakers", isDefault: true,
                ("s16", 2, 48_000), ("s24", 2, 44_100), ("s16", 6, 44_100), ("f32", 0, 0)),
            Device("USB DAC", isDefault: false));
        using var provider = new MiniAudioOutputProvider(() => backend);

        var devices = provider.GetDevices();

        Assert.Equal("miniaudio (Fake)", provider.Name);
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
        var provider = new MiniAudioOutputProvider(() => { created++; return backend; });

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
        using var provider = new MiniAudioOutputProvider(() => throw new InvalidOperationException("Result: NoBackend"));

        Assert.Equal("miniaudio (unavailable)", provider.Name);
        var ex = Assert.Throws<MusicSpinException>(() => provider.GetDevices());
        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Contains("Could not initialise audio backend: Result: NoBackend", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void A_failing_device_enumeration_is_an_audio_device_error()
    {
        var backend = new FakeBackend { RefreshError = new InvalidOperationException("enumeration failed") };
        using var provider = new MiniAudioOutputProvider(() => backend);

        var ex = Assert.Throws<MusicSpinException>(() => provider.GetDevices());

        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Equal("Could not list devices: enumeration failed", ex.Message);
    }

    [Fact]
    public void Open_uses_the_sources_native_format_and_the_default_device_when_none_is_configured()
    {
        var backend = new FakeBackend(Device("Speakers", isDefault: true));
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        Assert.False(output.IsOpen);
        output.Open(new Source(new PcmFormat(96_000, 2, 24)));

        Assert.True(output.IsOpen);
        var open = Assert.Single(backend.Opened);
        Assert.Null(open.Device);
        Assert.Equal(new OpenRequest(DeviceSample.S24, 96_000, 2, Exclusive: true, SourceBits: 24), open.Request);
        Assert.Equal(96_000, open.Data.Format.SampleRate);
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
        using var provider = new MiniAudioOutputProvider(() => backend);
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
        Assert.Equal(expected, backend.Opened.Single().Device!.Name);
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
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(DeviceId: id));

        output.Open(new Source(new PcmFormat(44_100, 2, 16)));

        Assert.Equal(expected, backend.Opened.Single().Device!.Name);
    }

    [Fact]
    public void A_device_that_fails_to_open_is_wrapped_and_leaves_the_output_closed()
    {
        var backend = new FakeBackend { OpenError = new InvalidOperationException("format not supported") };
        using var provider = new MiniAudioOutputProvider(() => backend);
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
        using var provider = new MiniAudioOutputProvider(() => backend);
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
    public void Filling_a_buffer_reads_until_full_and_pads_with_silence_at_the_end()
    {
        var source = new Source(new PcmFormat(48_000, 2, 24)) { Remaining = 5, Chunk = 2 };
        var buffer = new float[8];
        Array.Fill(buffer, 9f);

        Assert.Equal(5, PcmReading.Fill(source, buffer)); // three short reads, then end of stream
        Assert.Equal([1f, 2f, 3f, 4f, 5f, 0f, 0f, 0f], buffer);
        Assert.Equal(0, PcmReading.Fill(source, buffer));
        Assert.All(buffer, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void An_exclusive_44k1_24bit_track_asks_for_exclusive_at_44k1_s24_and_reports_what_opened()
    {
        var backend = new FakeBackend(Device("FiiO BTR13", isDefault: true));
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions("FiiO BTR13", Exclusive: true));

        Assert.Null(output.Opened);
        output.Open(new Source(new PcmFormat(44_100, 2, 24)));

        var open = Assert.Single(backend.Opened);
        Assert.Equal((44_100, DeviceSample.S24, true), (open.Request.SampleRate, open.Request.Format, open.Request.Exclusive));
        var opened = output.Opened!;
        Assert.Equal(("FiiO BTR13", true, 44_100, "s24", 24, true), (opened.Device, opened.Exclusive, opened.SampleRate, opened.SampleFormat, opened.BitsPerSample, opened.Verified));
        Assert.Null(opened.Notice);
        Assert.Equal("exclusive, 44.1 kHz 24-bit", opened.Describe());

        output.Stop();
        Assert.Null(output.Opened); // closed: nothing is in use
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_24bit_track_on_a_16bit_only_device_stays_exclusive_at_its_rate_and_says_it_is_dithered()
    {
        // A 16-bit-only device (#168): the WASAPI stream opened s16 and says why; nothing falls back to the shared mixer.
        var notice = DeviceSamples.DitherNotice(24, ["s24in32", "s32", "s24"]);
        var backend = new FakeBackend(Device("USB DAC", isDefault: true))
        {
            OpenedAs = (d, f) => new OpenedOutput(d?.Name, true, f.SampleRate, f.Channels, "s16", 16, Verified: true, notice),
        };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        output.Open(new Source(new PcmFormat(44_100, 2, 24)));

        var opened = output.Opened!;
        Assert.Equal("exclusive, 44.1 kHz 16-bit", opened.Describe());
        Assert.Equal(notice, opened.Notice); // no "requested exclusive" or resampling notice on top: neither happened
        Assert.Null(output.StreamFormat); // exclusive: each track at its own rate, not converted

        Assert.True(output.Retarget(new Source(new PcmFormat(44_100, 2, 24)))); // the next 24-bit track keeps the stream
        Assert.Single(backend.Opened);
    }

    [Fact]
    public void A_device_that_opens_at_another_rate_is_reported_at_that_rate_with_a_resampling_notice()
    {
        // What #148 saw: asked for 44.1 kHz exclusive, the device ran at the Windows mix rate.
        var backend = new FakeBackend(Device("FiiO BTR13", isDefault: true))
        {
            OpenedAs = (d, f) => new OpenedOutput(d?.Name, true, 48_000, f.Channels, "s24in32", 24, Verified: true),
        };
        using var provider = new MiniAudioOutputProvider(() => backend);
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
            OpenedAs = (_, f) => new OpenedOutput(null, false, 48_000, f.Channels, "f32", 32, Verified: false),
        };
        using var provider = new MiniAudioOutputProvider(() => backend);
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
            OpenedAs = (_, f) => new OpenedOutput("BTR13", false, f.SampleRate, f.Channels, "s24", 24, Verified: false,
                Notice: "Exclusive mode unavailable: another app is using the device. Playing in shared mode through the Windows mixer."),
        };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        output.Open(new Source(new PcmFormat(44_100, 2, 24)));

        Assert.False(output.Opened!.Exclusive);
        Assert.StartsWith("Exclusive mode unavailable: another app is using the device.", output.Opened.Notice);
        Assert.StartsWith("shared,", output.Opened.Describe());
    }

    [Fact]
    public void Exclusive_asked_but_shared_given_without_a_reason_still_gets_a_notice()
    {
        var backend = new FakeBackend { OpenedAs = (_, f) => new OpenedOutput(null, false, f.SampleRate, f.Channels, "s16", 16, Verified: false) };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        output.Open(new Source(new PcmFormat(44_100, 2, 16)));

        Assert.Equal("Exclusive mode was requested, but the device opened in shared mode.", output.Opened!.Notice);
    }

    [Fact]
    public void Shared_mode_as_asked_has_no_notice()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());

        output.Open(new Source(new PcmFormat(44_100, 2, 16)));

        Assert.False(output.Opened!.Exclusive);
        Assert.Null(output.Opened.Notice);
    }

    [Fact]
    public void An_exclusive_open_that_fails_is_an_error_naming_exclusive_mode_and_reports_nothing_in_use()
    {
        var backend = new FakeBackend { OpenError = new InvalidOperationException("Result: ShareModeNotSupported") };
        using var provider = new MiniAudioOutputProvider(() => backend);
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
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        Assert.NotNull(output.Opened);

        backend.OpenedAs = (_, _) => throw new InvalidOperationException("unplugged");
        Assert.Throws<MusicSpinException>(() => output.Open(new Source(new PcmFormat(96_000, 2, 24))));

        Assert.Null(output.Opened); // not still claiming 44.1 kHz exclusive
    }

    [Fact]
    public void The_device_rate_follows_each_tracks_format()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));

        var seen = new List<string>();
        foreach (var format in new[] { new PcmFormat(44_100, 2, 16), new PcmFormat(96_000, 2, 24), new PcmFormat(48_000, 2, 24) })
        {
            output.Open(new Source(format));
            seen.Add(output.Opened!.Describe());
        }

        Assert.Equal([44_100, 96_000, 48_000], backend.Opened.Select(o => o.Request.SampleRate));
        Assert.Equal(["exclusive, 44.1 kHz 16-bit", "exclusive, 96 kHz 24-bit", "exclusive, 48 kHz 24-bit"], seen);
        Assert.All(backend.Streams.Take(2), s => Assert.True(s.Disposed)); // each reopen released the previous device
    }

    // The audio thread of a real device is joined when the device closes; if it reports a loss meanwhile (Windows reports
    // the default change and the invalidated device together), nothing the report needs may be held across that close.
    // FakeStream.WhileClosing runs such a report on its own thread and waits for it, like the join does.

    [Fact]
    public async Task Stop_returns_when_the_closing_devices_audio_thread_reports_a_loss_meanwhile()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        backend.Streams[0].WhileClosing = s => { s.Lose("The audio device stopped: invalidated."); s.ChangeDefault(); };

        await Task.Run(output.Stop, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
        posted.RunAll();

        Assert.False(output.IsOpen);
        Assert.True(backend.Streams[0].Disposed);
        Assert.Single(backend.Opened); // the report belonged to the device being closed: no reopen
    }

    [Fact]
    public async Task Opening_the_next_track_returns_when_the_closing_devices_audio_thread_reports_a_loss_meanwhile()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        backend.Streams[0].WhileClosing = s => s.Lose("unplugged");
        var next = new Source(new PcmFormat(96_000, 2, 24));

        await Task.Run(() => output.Open(next), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
        posted.RunAll();

        Assert.Equal(2, backend.Opened.Count);
        Assert.Same(next, backend.Opened[1].Data);
        Assert.False(backend.Streams[1].Disposed); // the stale report did not touch the new device
    }

    [Fact]
    public async Task Moving_to_the_default_returns_when_the_replaced_devices_audio_thread_reports_again_meanwhile()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var moves = 0;
        output.Rerouted += (_, _) => moves++;
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        backend.Streams[0].WhileClosing = s => { s.Lose("again"); s.ChangeDefault(); };

        backend.Streams[0].Lose("unplugged");
        await Task.Run(posted.RunAll, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(1, moves);
        Assert.Equal(0, posted.Count); // the second report was about the replaced device: it queued nothing
        Assert.Equal(2, backend.Opened.Count);
        Assert.Same(backend.Streams[1], ((MiniAudioOutput)output).Stream);
    }

    [Fact]
    public async Task Outcomes_are_raised_outside_the_outputs_own_lock()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var stopped = false;
        // A handler that waits for another thread to use the output (the Player's handlers take the Player's lock, which
        // another thread may hold while it calls into the output): it would hang if the output still held its own lock.
        output.Rerouted += (_, _) => Task.Run(() => { _ = output.IsOpen; output.Stop(); stopped = true; }).Wait(Bound);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        backend.Streams[0].Lose("unplugged");
        await Task.Run(posted.RunAll, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(stopped);
        Assert.False(output.IsOpen);
    }

    [Fact]
    public void Pausing_in_exclusive_mode_releases_the_device_and_resuming_reopens_it_on_the_same_source()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        var moves = 0;
        output.Rerouted += (_, _) => moves++;
        var source = new Source(new PcmFormat(44_100, 2, 24));
        output.Open(source);
        output.Start();
        var opened = output.Opened;

        output.Pause();

        Assert.Equal(["start", "pause"], backend.Streams[0].Calls); // drained first, then released
        Assert.True(backend.Streams[0].Disposed);
        Assert.True(output.IsOpen); // paused, not closed: the player keeps its output
        Assert.Equal(opened, output.Opened);

        output.Start();

        Assert.Equal(2, backend.Opened.Count);
        Assert.Same(source, backend.Opened[1].Data);
        Assert.Equal(backend.Opened[0].Request, backend.Opened[1].Request);
        Assert.Equal(["start"], backend.Streams[1].Calls);
        Assert.Same(backend.Streams[1], ((MiniAudioOutput)output).Stream);
        posted.RunAll();
        Assert.Equal(0, moves); // the same device, the same format: nothing to report
    }

    [Fact]
    public void Pausing_in_shared_mode_keeps_the_device_open()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        output.Pause();
        output.Start();

        Assert.Single(backend.Opened);
        Assert.False(backend.Streams[0].Disposed);
        Assert.Equal(["start", "pause", "start"], backend.Streams[0].Calls);
    }

    [Fact]
    public void A_loss_reported_just_before_an_exclusive_pause_does_not_reopen_the_released_device()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        backend.Streams[0].Lose("unplugged");
        output.Pause();
        posted.RunAll();

        Assert.Single(backend.Opened); // paused devices are released, not reopened behind the listener's back
        Assert.True(output.IsOpen);
    }

    [Fact]
    public void A_resume_that_cannot_reopen_the_device_says_so_and_can_be_tried_again()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        var source = new Source(new PcmFormat(44_100, 2, 16));
        output.Open(source);
        output.Start();
        output.Pause();
        backend.OpenError = new InvalidOperationException("another app is using the device");

        var ex = Assert.Throws<MusicSpinException>(output.Start);

        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        Assert.Contains("another app is using the device", ex.Message, StringComparison.Ordinal);
        Assert.True(output.IsOpen); // still paused on its source: the player stays paused and Play can retry
        backend.OpenError = null;

        output.Start();

        Assert.Same(source, backend.Opened[1].Data);
        Assert.Equal(["start"], backend.Streams[1].Calls);
        Assert.False(backend.Streams[1].Disposed);
    }

    [Fact]
    public void A_resume_whose_chosen_device_is_gone_plays_on_the_default_and_says_so()
    {
        var dac = Device("FiiO BTR13", isDefault: false);
        var backend = new FakeBackend(Device("Speakers", isDefault: true), dac);
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions("FiiO BTR13", Exclusive: true));
        var moves = new List<AudioOutputReroutedEventArgs>();
        output.Rerouted += (_, e) => moves.Add(e);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        output.Pause();
        backend.Devices.Remove(dac); // unplugged while paused: nothing was open to notice

        output.Start();
        posted.RunAll();

        Assert.Null(backend.Opened[1].Device);
        var move = Assert.Single(moves);
        Assert.Contains("'FiiO BTR13' is not available", move.Message, StringComparison.Ordinal);
        Assert.Contains("not available", output.Opened!.Notice, StringComparison.Ordinal);
        Assert.Equal(["start"], backend.Streams[1].Calls);
    }

    [Fact]
    public void A_resume_that_ends_up_in_shared_mode_reports_why()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        var moves = new List<AudioOutputReroutedEventArgs>();
        output.Rerouted += (_, e) => moves.Add(e);
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        output.Pause();
        backend.OpenedAs = (_, r) => new OpenedOutput(null, false, r.SampleRate, r.Channels, "f32", 32, Verified: true, "Exclusive mode unavailable: another app has it. Playing in shared mode.");

        output.Start();
        posted.RunAll();

        var move = Assert.Single(moves);
        Assert.Equal("Exclusive mode unavailable: another app has it. Playing in shared mode.", move.Message);
        Assert.False(output.Opened!.Exclusive);
    }

    [Fact]
    public void Stopping_a_paused_exclusive_output_forgets_its_source()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        output.Pause();

        output.Stop();

        Assert.False(output.IsOpen);
        Assert.Equal(MusicSpinErrorCode.InvalidState, Assert.Throws<MusicSpinException>(output.Start).Code);
        Assert.Single(backend.Opened); // nothing reopened
    }

    // ---- retargeting (track changes without reopening the device) ---------------------------------------------

    [Fact]
    public void Retargeting_to_a_same_format_source_keeps_the_stream_and_reads_only_the_new_source()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var first = new Source(new PcmFormat(44_100, 2, 16));
        var second = new Source(new PcmFormat(44_100, 2, 24)); // the depth does not matter in shared mode
        output.Open(first);
        output.Start();
        var stream = backend.Opened[0].Stream;
        var buffer = new float[8];
        Assert.Equal(8, stream.Read(buffer));
        var firstRemaining = first.Remaining;

        Assert.True(output.Retarget(second));

        Assert.Single(backend.Opened); // no new stream
        Assert.False(backend.Streams[0].Disposed);
        Assert.Equal(["start", "pause"], backend.Streams[0].Calls); // stopped, so no callback can still be reading the old one
        Assert.Same(second, backend.Opened[0].Data);
        Assert.Equal(8, stream.Read(buffer));
        Assert.Equal(firstRemaining, first.Remaining); // the old source is never read again
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, buffer); // the new source's first samples

        output.Start();

        Assert.Equal(["start", "pause", "start"], backend.Streams[0].Calls);
        Assert.Single(backend.Opened);
    }

    [Fact]
    public void Retargeting_a_paused_output_does_not_pause_it_again()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        output.Pause();

        Assert.True(output.Retarget(new Source(new PcmFormat(44_100, 2, 16))));

        Assert.Equal(["start", "pause"], backend.Streams[0].Calls);
    }

    [Fact]
    public void A_source_that_needs_another_channel_count_is_refused_and_nothing_changes()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var first = new Source(new PcmFormat(44_100, 2, 16));
        output.Open(first);
        output.Start();

        Assert.False(output.Retarget(new Source(new PcmFormat(44_100, 1, 16))));

        Assert.Equal(["start"], backend.Streams[0].Calls); // still playing the old track until the caller reopens
        Assert.Same(first, backend.Opened[0].Data);
        Assert.True(output.IsOpen);
    }

    [Fact]
    public void An_exclusive_output_refuses_a_source_of_another_rate()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        var first = new Source(new PcmFormat(44_100, 2, 16));
        output.Open(first);
        output.Start();

        Assert.False(output.Retarget(new Source(new PcmFormat(48_000, 2, 16)))); // the DAC must switch rate: reopen
        Assert.Equal(["start"], backend.Streams[0].Calls);
        Assert.Same(first, backend.Opened[0].Data);
        Assert.Null(output.StreamFormat); // exclusive plays each track at its own format
    }

    // ------------------------------------------------------------------- shared mode: the OS mixer's rate, converted once

    /// <summary>Everything the stream reads of its source, a block at a time like device callbacks.</summary>
    private static float[] ReadAll(IPcmSource stream, int samples)
    {
        var all = new float[samples];
        var at = 0;
        while (at < samples)
        {
            var n = stream.Read(all.AsSpan(at, Math.Min(480, samples - at)));
            if (n <= 0)
            {
                break;
            }

            at += n;
        }

        return all[..at];
    }

    [Fact]
    public void Shared_mode_opens_at_the_mixers_rate_and_converts_the_track_to_it()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var source = new Source(new PcmFormat(44_100, 2, 16)) { Remaining = 4_410 * 2 }; // 100 ms

        output.Open(source);

        var open = Assert.Single(backend.Opened);
        Assert.Equal(0, open.Request.SampleRate); // the device's own rate, asked for as such
        Assert.Same(source, open.Data);
        Assert.Equal(new PcmFormat(48_000, 2, 16), open.Stream.Format); // what the device reads
        Assert.Equal(new PcmFormat(48_000, 2, 16), output.StreamFormat);
        var read = ReadAll(open.Stream, 48_000);
        Assert.InRange(read.Length / 2, 4_799, 4_801); // 100 ms at 48 kHz, the filter's last frames drained at the end
        Assert.Equal(0, source.Remaining);
    }

    [Fact]
    public void A_track_at_the_mixers_rate_passes_bit_for_bit()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var source = new Source(new PcmFormat(48_000, 2, 24)) { Remaining = 1_000 };

        output.Open(source);

        var stream = backend.Opened[0].Stream;
        Assert.False(((SwitchableSource)stream).Route.Converts);
        Assert.Equal(Enumerable.Range(1, 1_000).Select(i => (float)i), ReadAll(stream, 1_000)); // untouched
    }

    [Fact]
    public void Shared_mode_retargets_a_track_of_any_rate_converted_to_the_streams_rate()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(48_000, 2, 16)));
        output.Start();
        var pump = (SwitchableSource)backend.Opened[0].Stream;
        var first = pump.Route;

        var next = new Source(new PcmFormat(96_000, 2, 24)) { Remaining = 9_600 * 2 };
        Assert.True(output.Retarget(next));

        Assert.Single(backend.Opened); // no reopen
        Assert.Same(next, pump.Target);
        Assert.True(pump.Route.Converts);
        Assert.Equal(48_000, pump.Format.SampleRate);
        Assert.NotSame(first, pump.Route);
        Assert.InRange(ReadAll(pump, 48_000).Length / 2, 4_799, 4_801); // 100 ms at the stream's rate, drained to the end

        var back = new Source(new PcmFormat(48_000, 2, 16)) { Remaining = 10 };
        Assert.True(output.Retarget(back));
        Assert.False(pump.Route.Converts); // at the stream's rate again: passed through
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (float)i), ReadAll(pump, 10));
    }

    [Fact]
    public void A_reopen_on_a_device_at_another_rate_converts_the_same_source_to_the_new_rate()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        var source = new Source(new PcmFormat(48_000, 2, 16));
        output.Open(source);
        output.Start();
        Assert.False(((SwitchableSource)backend.Opened[0].Stream).Route.Converts);

        backend.MixRate = 44_100; // the new default's mixer runs at 44.1 kHz
        backend.Streams[0].Lose("unplugged");
        posted.RunAll();

        var reopened = (SwitchableSource)backend.Opened[1].Stream;
        Assert.Same(source, reopened.Target); // goes on from where it was
        Assert.True(reopened.Route.Converts);
        Assert.Equal(44_100, reopened.Format.SampleRate);
        Assert.Equal(new PcmFormat(44_100, 2, 16), output.StreamFormat); // later tracks are converted to it
        Assert.Equal(["start"], backend.Streams[1].Calls);
    }

    [Fact]
    public void A_stream_that_cannot_say_its_rate_reads_the_source_as_it_is()
    {
        var backend = new FakeBackend
        {
            OpenedAs = (_, r) => new OpenedOutput(null, false, 0, r.Channels, "f32", 32, Verified: false), // rate unknown
        };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var source = new Source(new PcmFormat(44_100, 2, 16)) { Remaining = 4 };

        output.Open(source);

        var pump = (SwitchableSource)backend.Opened[0].Stream;
        Assert.False(pump.Route.Converts);
        Assert.Equal([1f, 2f, 3f, 4f], ReadAll(pump, 4));
    }

    [Fact]
    public void A_converted_source_that_has_more_after_its_end_starts_the_filter_afresh()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var source = new Source(new PcmFormat(44_100, 2, 16)) { Remaining = 4_410 * 2 };
        output.Open(source);
        var stream = backend.Opened[0].Stream;
        Assert.InRange(ReadAll(stream, 48_000).Length / 2, 4_799, 4_801);
        Assert.Equal(0, stream.Read(new float[480])); // drained: the end, and it stays the end

        source.Remaining = 4_410 * 2; // sought back after the end (a ring buffer flushed by a seek)

        Assert.InRange(ReadAll(stream, 48_000).Length / 2, 4_799, 4_801); // all of it again: not cut by an ended filter
    }

    [Fact]
    public void A_drained_tail_longer_than_one_read_comes_over_the_next_reads()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)) { Remaining = 441 * 2 }); // 10 ms
        var stream = backend.Opened[0].Stream;

        var total = 0;
        int n;
        var reads = 0;
        while ((n = stream.Read(new float[16])) > 0 && reads++ < 10_000)
        {
            Assert.Equal(0, n % 2);
            total += n;
        }

        Assert.InRange(total / 2, 479, 481); // tiny reads: the tail spans many, none lost
    }

    // ------------------------------------------------- exclusive refused: a shared stream at the mixer's rate

    private static (MiniAudioOutputProvider Provider, IAudioOutput Output) Exclusive(FakeBackend backend)
    {
        var provider = new MiniAudioOutputProvider(() => backend);
        return (provider, provider.Create(new AudioOutputOptions(Exclusive: true)));
    }

    [Fact]
    public void A_refused_exclusive_open_converts_the_track_to_the_shared_streams_rate_instead_of_playing_it_fast()
    {
        var backend = new FakeBackend { MixRate = 48_000, RefuseExclusive = true };
        var (provider, output) = Exclusive(backend);
        using var _ = provider;
        using var __ = output;
        var source = new Source(new PcmFormat(44_100, 2, 24)) { Remaining = 4_410 * 2 }; // 100 ms

        output.Open(source);

        Assert.False(output.Opened!.Exclusive);
        Assert.Contains("Exclusive mode unavailable", output.Opened.Notice);
        Assert.Equal(new PcmFormat(48_000, 2, 24), output.StreamFormat); // so the player converts the next tracks to it
        Assert.InRange(ReadAll(backend.Opened[0].Stream, 48_000).Length / 2, 4_799, 4_801); // 100 ms at 48 kHz, not 92 ms
        Assert.NotNull(output.CreateConverter(source.Format, new PcmFormat(48_000, 2, 24)));
    }

    [Fact]
    public void A_track_switch_on_a_refused_exclusive_stream_converts_the_next_track_too()
    {
        var backend = new FakeBackend { MixRate = 48_000, RefuseExclusive = true };
        var (provider, output) = Exclusive(backend);
        using var _ = provider;
        using var __ = output;
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();

        var next = new Source(new PcmFormat(44_100, 2, 16)) { Remaining = 4_410 * 2 };
        Assert.True(output.Retarget(next)); // same request: kept open

        var pump = (SwitchableSource)backend.Opened[0].Stream;
        Assert.True(pump.Route.Converts);
        Assert.InRange(ReadAll(pump, 48_000).Length / 2, 4_799, 4_801);
    }

    [Fact]
    public void Exclusive_refused_only_at_a_resume_converts_then_and_plays_bit_for_bit_once_exclusive_again()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        var (provider, output) = Exclusive(backend);
        using var _ = provider;
        using var __ = output;
        var source = new Source(new PcmFormat(44_100, 2, 24));
        output.Open(source);
        output.Start();
        Assert.True(output.Opened!.Exclusive);
        Assert.Null(output.StreamFormat);
        Assert.Null(output.CreateConverter(source.Format, new PcmFormat(48_000, 2, 24))); // exclusive: never converted
        Assert.False(((SwitchableSource)backend.Opened[0].Stream).Route.Converts);

        output.Pause(); // releases the device; another app takes it meanwhile
        backend.RefuseExclusive = true;
        output.Start();

        Assert.False(output.Opened!.Exclusive);
        Assert.Equal(48_000, output.StreamFormat!.Value.SampleRate);
        Assert.True(((SwitchableSource)backend.Opened[1].Stream).Route.Converts); // the mixer's 48 kHz, converted

        output.Pause(); // shared: not released, so release it the way a stop and play would
        output.Stop();
        backend.RefuseExclusive = false;
        var again = new Source(new PcmFormat(44_100, 2, 24)) { Remaining = 10 };
        output.Open(again);

        Assert.True(output.Opened!.Exclusive);
        Assert.Null(output.StreamFormat);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (float)i), ReadAll(backend.Opened[2].Stream, 10)); // bit for bit
    }

    [Fact]
    public void A_broken_sample_becomes_silence_instead_of_poisoning_the_converted_stream()
    {
        var backend = new FakeBackend { MixRate = 48_000 };
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new NanSource(new PcmFormat(44_100, 2, 16)));

        var read = ReadAll(backend.Opened[0].Stream, 9_600);

        Assert.True(read.Length > 9_000);
        Assert.All(read, x => Assert.True(float.IsFinite(x), $"non-finite {x}"));
    }

    /// <summary>A NaN and an infinity, then silence.</summary>
    private sealed class NanSource(PcmFormat format) : IPcmSource
    {
        private bool _sent;

        public PcmFormat Format => format;

        public int Read(Span<float> buffer)
        {
            buffer.Clear();
            if (!_sent && buffer.Length >= 2)
            {
                buffer[0] = float.NaN;
                buffer[1] = float.PositiveInfinity;
                _sent = true;
            }

            return buffer.Length;
        }
    }

    [Fact]
    public void An_output_that_is_not_open_refuses_to_retarget()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions());
        var source = new Source(new PcmFormat(44_100, 2, 16));

        Assert.False(output.Retarget(source)); // never opened

        output.Open(source);
        output.Stop();
        Assert.False(output.Retarget(source)); // closed again
        Assert.Throws<ArgumentNullException>(() => output.Retarget(null!));
    }

    [Fact]
    public void An_exclusive_device_is_retargeted_only_for_the_same_depth()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        output.Open(new Source(new PcmFormat(44_100, 2, 24)));
        output.Start();

        Assert.False(output.Retarget(new Source(new PcmFormat(44_100, 2, 16)))); // the device is configured for 24-bit
        var same = new Source(new PcmFormat(44_100, 2, 24));
        Assert.True(output.Retarget(same));

        Assert.Single(backend.Opened);
        Assert.False(backend.Streams[0].Disposed); // the exclusive device is kept, not handed back and re-acquired
        Assert.Same(same, backend.Opened[0].Data);
    }

    [Fact]
    public void Exclusive_sources_of_one_device_format_share_the_device_whatever_their_own_bit_depth()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        output.Open(new Source(new PcmFormat(44_100, 2, 32))); // 32-bit integer: opened as float32
        output.Start();
        var float32 = new Source(new PcmFormat(44_100, 2, 0)); // a float source: the same float32 device
        var twenty = new Source(new PcmFormat(44_100, 2, 20)); // 17..24 bits: the 24-bit device, not the float one

        Assert.True(output.Retarget(float32));
        Assert.False(output.Retarget(twenty));

        Assert.Single(backend.Opened);
        Assert.Same(float32, backend.Opened[0].Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unhappy")]
    public void A_device_that_does_not_confirm_it_stopped_is_closed_instead_of_being_given_the_next_source(bool exclusive)
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: exclusive));
        var first = new Source(new PcmFormat(44_100, 2, 24));
        var second = new Source(new PcmFormat(44_100, 2, 24));
        output.Open(first);
        output.Start();
        backend.Streams[0].PauseConfirmed = false; // the render thread is stuck in the driver and may still read `first`

        Assert.False(output.Retarget(second));

        Assert.True(backend.Streams[0].Disposed);
        Assert.Same(first, backend.Opened[0].Data); // never switched: the old stream cannot read the new source
        Assert.False(output.IsOpen);
        output.Open(second); // what the caller does on false
        output.Start();
        Assert.Equal(2, backend.Opened.Count);
        Assert.Same(second, backend.Opened[1].Data);
        Assert.Equal(["start"], backend.Streams[1].Calls);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_device_lost_on_one_thread_while_another_retargets_leaves_the_output_on_the_last_source()
    {
        const int Rounds = 200;
        var backend = new FakeBackend();
        var reopenings = new ConcurrentBag<Task>();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = work => reopenings.Add(Task.Run(work, TestContext.Current.CancellationToken)) };
        using var output = provider.Create(new AudioOutputOptions());
        var sources = Enumerable.Range(0, Rounds).Select(_ => new Source(new PcmFormat(44_100, 2, 16))).ToArray();
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        using var together = new Barrier(2);

        var retargeting = Task.Run(() =>
        {
            together.SignalAndWait(TestContext.Current.CancellationToken);
            foreach (var source in sources)
            {
                Assert.True(output.Retarget(source));
                output.Start();
            }
        }, TestContext.Current.CancellationToken);
        var losing = Task.Run(() =>
        {
            together.SignalAndWait(TestContext.Current.CancellationToken);
            for (var i = 0; i < Rounds; i++)
            {
                FakeStream current;
                lock (backend.Streams)
                {
                    current = backend.Streams[^1];
                }

                current.Lose("device gone"); // on this thread, as a device's audio thread would
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(retargeting, losing).WaitAsync(Bound, TestContext.Current.CancellationToken);
        while (reopenings.TryTake(out var reopening))
        {
            await reopening.WaitAsync(Bound, TestContext.Current.CancellationToken); // a reopening may itself be lost, and post more
        }

        Assert.True(output.IsOpen);
        lock (backend.Streams)
        {
            Assert.Same(sources[^1], backend.Opened[^1].Data); // whichever stream plays, it plays the last track
            Assert.All(backend.Streams.SkipLast(1), stream => Assert.True(stream.Disposed));
            Assert.False(backend.Streams[^1].Disposed);
        }
    }

    [Fact]
    public void A_paused_exclusive_device_stays_released_when_retargeted_and_resumes_on_the_new_source()
    {
        var backend = new FakeBackend();
        using var provider = new MiniAudioOutputProvider(() => backend);
        using var output = provider.Create(new AudioOutputOptions(Exclusive: true));
        output.Open(new Source(new PcmFormat(44_100, 2, 24)));
        output.Start();
        output.Pause(); // released
        var next = new Source(new PcmFormat(44_100, 2, 24));

        Assert.True(output.Retarget(next));

        Assert.Single(backend.Opened); // nothing reacquired yet: another app may still use the device
        output.Start();
        Assert.Equal(2, backend.Opened.Count);
        Assert.Same(next, backend.Opened[1].Data);
        Assert.Equal(["start"], backend.Streams[1].Calls);
    }

    [Fact]
    public void A_device_lost_after_a_retarget_reopens_on_the_new_source()
    {
        var backend = new FakeBackend();
        var posted = new Posted();
        using var provider = new MiniAudioOutputProvider(() => backend) { Post = posted.Post };
        using var output = provider.Create(new AudioOutputOptions());
        output.Open(new Source(new PcmFormat(44_100, 2, 16)));
        output.Start();
        var next = new Source(new PcmFormat(44_100, 2, 16));
        Assert.True(output.Retarget(next));
        output.Start();

        backend.Streams[0].Lose("unplugged");
        posted.RunAll();

        Assert.Equal(2, backend.Opened.Count);
        Assert.Same(next, backend.Opened[1].Data); // not the track that was playing before
        Assert.Equal(["start"], backend.Streams[1].Calls);
    }

    private static BackendDevice Device(string name, bool isDefault, params (string, int, int)[] formats)
        => new(name, isDefault, new byte[256], formats);

    private sealed class Source(PcmFormat format) : IPcmSource
    {
        private float _next = 1;

        public int Remaining { get; set; } = int.MaxValue;

        /// <summary>Most samples one read returns (short reads, like a ring buffer that is not full).</summary>
        public int Chunk { get; init; } = int.MaxValue;

        public PcmFormat Format => format;

        public int Read(Span<float> buffer)
        {
            var n = Math.Min(Math.Min(buffer.Length, Remaining), Chunk);
            for (var i = 0; i < n; i++)
            {
                buffer[i] = _next++;
            }

            Remaining -= n;
            return n;
        }
    }

    private sealed record OpenCall(BackendDevice? Device, OpenRequest Request, IPcmSource Stream)
    {
        /// <summary>What the stream reads right now (the stream gets a switchable wrapper around the caller's source).</summary>
        public IPcmSource Data => Stream is SwitchableSource pump ? pump.Target : Stream;
    }

    /// <summary>Longest a test waits for work that must not block; reached only by a deadlock.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class FakeStream(OpenedOutput opened, Exception? startError) : IPlaybackStream
    {
        /// <summary>
        /// What the device's audio thread does while the device closes: it runs on its own thread and Dispose waits for
        /// it, as closing a real device joins its audio thread.
        /// </summary>
        public Action<FakeStream>? WhileClosing { get; set; }

        public OpenedOutput Opened { get; set; } = opened;

        public List<string> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public event EventHandler<string>? Lost;

        public event EventHandler? Rerouted;

        public event EventHandler? DefaultDeviceChanged;

        public void Start()
        {
            if (startError is not null)
            {
                throw startError;
            }

            Calls.Add("start");
        }

        /// <summary>What Pause reports: false is a device thread that did not stop in time (WASAPI's bounded wait).</summary>
        public bool PauseConfirmed { get; set; } = true;

        public bool Pause()
        {
            Calls.Add("pause");
            return PauseConfirmed;
        }

        /// <summary>What the audio thread does when the device goes away.</summary>
        public void Lose(string reason) => Lost?.Invoke(this, reason);

        public void Reroute() => Rerouted?.Invoke(this, EventArgs.Empty);

        public void ChangeDefault() => DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            if (WhileClosing is { } audioThread)
            {
                Task.Run(() => audioThread(this)).Wait();
            }

            Disposed = true;
        }
    }

    /// <summary>The hand-off queue, drained by the test: device changes run exactly when the test says.</summary>
    private sealed class Posted
    {
        private readonly Queue<Action> _work = new();

        public int Count => _work.Count;

        public void Post(Action work) => _work.Enqueue(work);

        public void RunAll()
        {
            while (_work.TryDequeue(out var work))
            {
                work();
            }
        }
    }

    private sealed class FakeBackend(params BackendDevice[] devices) : IAudioBackend
    {
        public List<BackendDevice> Devices { get; } = [.. devices];

        public Exception? RefreshError { get; init; }

        public Exception? OpenError { get; set; }

        public Exception? StartError { get; set; }

        /// <summary>What the "device" reads back; default: exactly what was asked, verified.</summary>
        public Func<BackendDevice?, OpenRequest, OpenedOutput>? OpenedAs { get; set; }

        /// <summary>The OS mixer's rate, what a shared request (rate 0) opens at; null: the source's own.</summary>
        public int? MixRate { get; set; }

        /// <summary>An exclusive open is refused (another app has the device) and, like the real backend, opens shared.</summary>
        public bool RefuseExclusive { get; set; }

        public List<OpenCall> Opened { get; } = [];

        public List<FakeStream> Streams { get; } = [];

        public bool Disposed { get; private set; }

        public string ActiveBackend => "Fake";

        public IReadOnlyList<BackendDevice> RefreshPlaybackDevices()
            => RefreshError is null ? [.. Devices] : throw RefreshError;

        public IPlaybackStream Open(BackendDevice? device, OpenRequest request, IPcmSource source)
        {
            if (OpenError is not null)
            {
                throw OpenError;
            }

            var asked = request;
            string? notice = null;
            if (request.Exclusive && RefuseExclusive)
            {
                request = request with { Format = DeviceSample.F32, Exclusive = false, SampleRate = 0 };
                notice = "Exclusive mode unavailable: another app is using the device. Playing in shared mode.";
            }

            if (request.SampleRate == 0)
            {
                request = request with { SampleRate = MixRate ?? source.Format.SampleRate }; // what the device reads back
            }

            var stream = new FakeStream(OpenedAs?.Invoke(device, request) ?? new OpenedOutput(
                device?.Name, request.Exclusive, request.SampleRate, request.Channels,
                DeviceSamples.Name(request.Format), DeviceSamples.ValidBits(request.Format), Verified: true) { Notice = notice }, StartError);
            lock (Streams)
            {
                Opened.Add(new OpenCall(device, asked, source));
                Streams.Add(stream);
            }

            return stream;
        }

        public void Dispose() => Disposed = true;
    }
}
