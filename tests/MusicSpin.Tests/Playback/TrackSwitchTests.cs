using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>
/// Switching tracks must not tear the output device down when it can be kept: opening a new device stream is the slow part
/// of a track change (up to seconds on PulseAudio), so a same-format switch retargets the open device and only a different
/// rate or channel count reopens it. Driven deterministically through the manual output; no waiting on time.
/// </summary>
public sealed class TrackSwitchTests : IDisposable
{
    private readonly RampDecoderFactory _decoders = new();
    private readonly ManualOutputProvider _outputs = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private ManualAudioOutput Output => _outputs.Output;

    private string Add(RampSpec spec)
    {
        var path = $"/s/{spec.Id}.flac";
        _decoders.Add(path, spec);
        return path;
    }

    private Player CreatePlayer()
    {
        var player = new Player(_decoders, _outputs, new PlayerOptions());
        _owned.Add(player);
        return player;
    }

    private (Player Player, QueuePlayer Queue) CreateQueue(params RampSpec[] specs)
    {
        var player = CreatePlayer();
        var queue = new QueuePlayer(player);
        _owned.Add(queue);
        queue.SetQueue([.. specs.Select(s => new QueueItem(Add(s)))]);
        return (player, queue);
    }

    /// <summary>Waits until the decode thread has the first samples ready, then pulls them like the device would.</summary>
    private float[] Pull(int samples) => Output.Pump(samples);

    [Fact]
    public void A_same_format_switch_keeps_the_device_open_and_plays_only_the_new_track()
    {
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 4000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        var oldBuffer = player.Buffer!;
        Assert.Equal(a.Pcm()[..200], Pull(200));
        var oldRead = oldBuffer.Position;

        player.Load(Add(b));

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(1, Output.RetargetCount);
        Assert.True(Output.IsOpen);
        Assert.False(Output.Started); // loading does not play
        Assert.Equal(PlayerState.Stopped, player.State);
        Assert.Empty(Pull(64)); // a paused device pulls nothing

        player.Play();

        Assert.Equal(1, Output.OpenCount); // no new open for the new track
        Assert.True(Output.Started);
        Assert.Equal(b.Pcm()[..500], Pull(500)); // starts at the new track's first sample, nothing of the old one
        Assert.Equal(oldRead, oldBuffer.Position); // the output never read the old track again
    }

    [Theory]
    [InlineData(2000, 2)] // sample rate
    [InlineData(1000, 1)] // channels
    public void A_different_rate_or_channel_count_reopens_the_device_once(int rate, int channels)
    {
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 4000, rate, channels);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Pull(64);

        player.Load(Add(b));
        Assert.Equal(0, Output.RetargetCount);
        Assert.False(Output.IsOpen); // released, nothing left reading the old track
        player.Play();

        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(b.Pcm()[..300], Pull(300));
    }

    [Fact]
    public void An_exclusive_device_configured_for_another_depth_reopens()
    {
        var a = new RampSpec(1, 4000, Bits: 24);
        var b = new RampSpec(2, 4000, Bits: 16);
        Output.Exclusive = true;
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();

        player.Load(Add(b));
        player.Play();

        Assert.Equal(0, Output.RetargetCount);
        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(b.Pcm()[..100], Pull(100));
    }

    [Fact]
    public void Stopping_after_a_switch_without_playing_releases_the_device()
    {
        var player = CreatePlayer();
        player.Load(Add(new RampSpec(1, 4000)));
        player.Play();
        player.Load(Add(new RampSpec(2, 4000)));

        player.Stop();

        Assert.False(Output.IsOpen);
        player.Play();
        Assert.Equal(2, Output.OpenCount); // reopened for the next play
    }

    [Fact]
    public void The_output_moves_to_the_new_track_before_the_player_swaps_its_buffer()
    {
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 4000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        var oldBuffer = player.Buffer!;
        Pull(100);
        var seen = new List<(IPcmSource Target, PlaybackBuffer? Current)>();
        Output.OnRetarget = next => seen.Add((next, player.Buffer));

        player.Load(Add(b));

        var (target, current) = Assert.Single(seen);
        Assert.Same(oldBuffer, current); // still the old track's buffer: nothing of the new one is visible yet
        Assert.NotSame(oldBuffer, target);
        Assert.Same(target, player.Buffer); // and the player ends up on the buffer the output was given
        Assert.Same(target, Output.Source);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_output_that_cannot_confirm_it_stopped_is_closed_and_the_new_track_plays_from_a_fresh_open()
    {
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 4000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Pull(100);
        var oldBuffer = player.Buffer!;
        var oldRead = oldBuffer.Position;
        Output.RetargetUnconfirmed = true;

        player.Load(Add(b));

        Assert.False(Output.IsOpen); // nothing left that could still read the old track
        Assert.Equal(0, Output.RetargetCount);
        Assert.Equal(PlayerState.Stopped, player.State);
        Output.RetargetUnconfirmed = false;
        player.Play();
        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(b.Pcm()[..100], Pull(100));
        Assert.Equal(oldRead, oldBuffer.Position); // the old buffer was never read again
    }

    [Fact]
    public void An_exclusive_device_stays_held_for_a_switch_that_is_followed_by_play()
    {
        Output.Exclusive = true;
        var (_, queue) = CreateQueue(new RampSpec(1, 4000), new RampSpec(2, 4000));
        Pull(64);

        queue.Next(); // loads, then plays

        Assert.Equal(1, Output.OpenCount);
        Assert.False(Output.Released);
        Assert.True(Output.Started);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_switch_that_ends_with_nothing_playing_hands_back_only_an_exclusive_device(bool exclusive)
    {
        Output.Exclusive = exclusive;
        var (_, queue) = CreateQueue(new RampSpec(1, 4000), new RampSpec(2, 4000));
        Pull(64);
        var other = new RampSpec(3, 4000);

        queue.SetQueue([new QueueItem(Add(other))], play: false);

        Assert.Equal(1, Output.RetargetCount); // still moved, not reopened
        Assert.False(Output.Started);
        Assert.Equal(exclusive, Output.Released); // a stopped player holds no exclusive device; a shared one is no lock
        queue.Play();
        Assert.False(Output.Released);
        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(other.Pcm()[..100], Pull(100));
    }

    [Fact]
    public void Loading_a_single_file_or_a_radio_station_without_playing_releases_an_exclusive_device()
    {
        Output.Exclusive = true;
        var (_, queue) = CreateQueue(new RampSpec(1, 4000), new RampSpec(2, 4000));
        Pull(64);

        queue.Load(Add(new RampSpec(3, 4000)));
        Assert.True(Output.Released);

        queue.Play();
        Assert.False(Output.Released);
        var pool = Enumerable.Range(10, 4).Select(i => new RadioCandidate($"id{i}", Add(new RampSpec(i, 4000)), $"artist{i}", $"album{i}")).ToList();
        queue.RadioPool = _ => pool;
        queue.RadioRandom = () => new Random(3);
        queue.StartRadio(new RadioFilter(), play: false);

        Assert.True(Output.Released);
        Assert.False(Output.Started);
    }

    [Fact]
    public void Cueing_after_a_switch_releases_an_exclusive_device_like_a_pause()
    {
        Output.Exclusive = true;
        var player = CreatePlayer();
        player.Load(Add(new RampSpec(1, 4000)));
        player.Play();
        player.Load(Add(new RampSpec(2, 4000)));
        Assert.False(Output.Released); // held, in case play follows

        player.Cue(TimeSpan.Zero);

        Assert.True(Output.Released);
        Assert.Equal(PlayerState.Paused, player.State);
        player.Play();
        Assert.Equal(1, Output.OpenCount);
        Assert.False(Output.Released);
    }

    [Fact]
    public void A_switch_while_paused_resumes_on_the_new_track_without_skipping_or_repeating()
    {
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 4000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Pull(100);
        player.Pause();

        player.Load(Add(b));
        Assert.Equal(PlayerState.Stopped, player.State);
        player.Play();

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(b.Pcm()[..100], Pull(100));
        Assert.Equal(b.Pcm()[100..200], Pull(100)); // continues without a repeat
    }

    [Fact]
    public void A_device_that_fails_on_open_reports_it_and_the_next_track_opens_it_again()
    {
        var player = CreatePlayer();
        player.Load(Add(new RampSpec(1, 4000)));
        player.Play();
        player.Load(Add(new RampSpec(2, 4000, SampleRate: 2000))); // needs a reopen
        Output.OpenError = new InvalidOperationException("device busy");

        var error = Assert.Throws<MusicSpinException>(player.Play);

        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, error.Code);
        Assert.False(Output.IsOpen);
        Output.OpenError = null;
        var c = new RampSpec(3, 4000, SampleRate: 2000); // the failed open left nothing open to retarget, so this opens it
        player.Load(Add(c));
        player.Play();

        Assert.True(Output.IsOpen);
        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(c.Pcm()[..100], Pull(100));
    }

    [Fact]
    public void An_output_that_fails_while_being_retargeted_is_reported_and_reopened()
    {
        var player = CreatePlayer();
        var errors = new List<Exception>();
        player.Error += (_, e) => errors.Add(e.Exception);
        player.Load(Add(new RampSpec(1, 4000)));
        player.Play();
        Output.RetargetError = new InvalidOperationException("stream gone");
        var b = new RampSpec(2, 4000);

        player.Load(Add(b));
        Output.RetargetError = null;
        player.Play();

        Assert.Equal("stream gone", Assert.Single(errors).Message);
        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(b.Pcm()[..100], Pull(100));
    }

    [Fact]
    public void Next_previous_and_play_index_on_same_format_tracks_never_reopen_the_device()
    {
        var specs = Enumerable.Range(1, 4).Select(i => new RampSpec(i, 4000)).ToArray();
        var (_, queue) = CreateQueue(specs);
        Pull(64);

        queue.Next();
        queue.Next(); // two quick presses
        queue.Previous();
        queue.Play(3);

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(4, Output.RetargetCount);
        Assert.Equal(3, queue.Status().QueueIndex);
        Assert.Equal(specs[3].Pcm()[..200], Pull(200));
    }

    [Fact]
    public void A_format_change_reopens_once_per_switch_and_not_for_the_tracks_after_it()
    {
        var (_, queue) = CreateQueue(new RampSpec(1, 4000), new RampSpec(2, 4000, SampleRate: 2000), new RampSpec(3, 4000, SampleRate: 2000));
        Pull(64);

        queue.Next(); // 1000 -> 2000 Hz
        queue.Next(); // 2000 -> 2000 Hz

        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(1, Output.RetargetCount);
    }

    [Fact]
    public void Radio_next_keeps_the_output_open()
    {
        var specs = Enumerable.Range(1, 6).Select(i => new RampSpec(i, 4000)).ToList();
        var pool = specs.Select(s => new RadioCandidate($"id{s.Id}", Add(s), $"artist{s.Id}", $"album{s.Id}")).ToList();
        var player = CreatePlayer();
        var queue = new QueuePlayer(player) { RadioPool = _ => pool, RadioRandom = () => new Random(3) };
        _owned.Add(queue);
        queue.StartRadio(new RadioFilter());
        Pull(64);

        queue.Next();
        queue.Next();

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(2, Output.RetargetCount);
        Assert.Equal(2, queue.Status().QueueIndex);
    }

    [Fact]
    public async Task A_switch_right_after_a_gapless_crossing_starts_the_wanted_track_from_its_first_sample()
    {
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 1000);
        var c = new RampSpec(3, 1000);
        var (player, queue) = CreateQueue(a, b, c);
        var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.TrackAdvanced += (_, _) => advanced.TrySetResult();
        Assert.Equal(a.Pcm(), Pull(2000));
        Assert.Equal(b.Pcm()[..50], Pull(50)); // crossed into b gaplessly
        await advanced.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, queue.Status().QueueIndex);

        queue.Next();

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(c.Pcm()[..400], Pull(400)); // c from its first sample: no rest of b, nothing skipped
    }

    [Fact]
    public void A_switch_right_before_a_gapless_boundary_does_not_play_the_old_tracks_remainder()
    {
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 1000);
        var c = new RampSpec(3, 1000);
        var (_, queue) = CreateQueue(a, b, c);
        Assert.Equal(a.Pcm()[..1998], Pull(1998)); // one frame of a left

        queue.Next(); // to b: the boundary into b was already queued, it must be played once

        Assert.Equal(b.Pcm()[..400], Pull(400));
        Assert.Equal(1, queue.Status().QueueIndex);
        Assert.Equal(1, Output.OpenCount);
    }

    // ---------------------------------------------------------------------------------- shared mode: convert, keep open

    private void SharedMode() => Output.Converter = (from, to) => new StepConverter(from, to);

    [Theory]
    [InlineData(2000, 2)] // sample rate
    [InlineData(1000, 1)] // channels
    [InlineData(500, 1)] // both
    public void Shared_mode_converts_another_rate_or_channel_count_and_keeps_the_device(int rate, int channels)
    {
        SharedMode();
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 8000, rate, channels);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Assert.Equal(a.Pcm()[..64], Pull(64));

        var loaded = player.Load(Add(b));
        player.Play();

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(1, Output.RetargetCount);
        Assert.Equal(a.Format with { SourceBitsPerSample = b.Bits }, player.Buffer!.Format); // the device's rate and channels
        Assert.Equal(StepConverter.Expected(b, a.Format)[..300], Pull(300));
        Assert.Equal(rate, loaded.SampleRate); // the track still says what it is
        var converter = Assert.IsType<StepConverter>(Assert.Single(Output.Converters));
        Assert.Equal(b.Format, converter.From);
    }

    [Fact]
    public void Shared_mode_converts_tracks_to_the_streams_own_rate_once_and_passes_one_at_that_rate_untouched()
    {
        SharedMode();
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 8000); // the same rate as a, but not the mixer's
        var c = new RampSpec(3, 8000, SampleRate: 2000); // the mixer's rate
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Pull(64);
        var mixer = new PcmFormat(2000, 2, 16);
        Output.StreamFormat = mixer; // the device runs at its mixer's rate (the output converts a; tracks are converted to it)

        player.Load(Add(b));
        player.Play();

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(1, Output.RetargetCount);
        var converter = Assert.IsType<StepConverter>(Assert.Single(Output.Converters));
        Assert.Equal(b.Format, converter.From);
        Assert.Equal(2000, converter.To.SampleRate); // to the mixer, not to a's rate
        Assert.Equal(StepConverter.Expected(b, mixer)[..300], Pull(300));

        player.Load(Add(c));
        player.Play();

        Assert.Equal(2, Output.RetargetCount);
        Assert.Single(Output.Converters); // none for c
        Assert.Equal(c.Pcm()[..300], Pull(300)); // bit for bit
    }

    [Fact]
    public void Shared_mode_seeks_a_converted_track_from_a_clean_converter()
    {
        SharedMode();
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 8000, SampleRate: 2000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Pull(64);
        player.Load(Add(b));
        player.Play();
        Pull(66);

        player.Seek(TimeSpan.FromSeconds(1)); // b's frame 2000, the device's frame 1000

        var converter = Assert.IsType<StepConverter>(Assert.Single(Output.Converters));
        Assert.Equal(1, converter.Resets);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
        Assert.Equal(StepConverter.Expected(b, a.Format).AsSpan(1000 * 2, 200).ToArray(), Pull(200));
        Assert.Equal(1, Output.OpenCount);
    }

    [Fact]
    public void A_converter_that_cannot_be_made_falls_back_to_reopening()
    {
        Output.Converter = (_, _) => throw new InvalidOperationException("no converter");
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 4000, SampleRate: 2000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Pull(64);

        player.Load(Add(b));
        player.Play();

        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(0, Output.RetargetCount);
        Assert.Equal(b.Pcm()[..300], Pull(300)); // its own format, unconverted
    }

    [Fact]
    public void A_converted_track_whose_device_was_released_reopens_at_the_converted_format()
    {
        SharedMode();
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 8000, SampleRate: 2000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();
        Pull(64);
        player.Load(Add(b));

        player.Stop(); // releases the device
        player.Play();

        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(a.Format.SampleRate, Output.Source!.Format.SampleRate);
        Assert.Equal(StepConverter.Expected(b, a.Format)[..100], Pull(100));
    }

    [Fact]
    public void Shared_mode_crosses_gaplessly_into_a_track_of_another_rate()
    {
        SharedMode();
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 2000, SampleRate: 2000);
        var c = new RampSpec(3, 1000, SampleRate: 500, Channels: 1);
        var (player, queue) = CreateQueue(a, b, c);
        var advanced = 0;
        player.TrackAdvanced += (_, _) => Interlocked.Increment(ref advanced);

        Assert.Equal(a.Pcm(), Pull(2000));
        Assert.Equal(StepConverter.Expected(b, a.Format), Pull(2000)); // no gap, no reopen
        Assert.Equal(StepConverter.Expected(c, a.Format)[..400], Pull(400));

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(0, Output.RetargetCount);
        Assert.Equal(2, Output.Converters.Count);
    }

    /// <summary>
    /// On a track change the queue sends the next tracks again, relative to the new one. When that lands while the decode
    /// thread is opening the next track from the old order, the track it opened is still next: it is kept, not dropped and
    /// opened (and converted) a second time. CI saw the second converter (main after #165 merged, Windows).
    /// </summary>
    [Fact]
    public void A_next_track_being_opened_when_the_order_is_sent_again_after_a_track_change_is_kept_not_opened_twice()
    {
        SharedMode();
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 2000, SampleRate: 2000);
        var c = new RampSpec(3, 1000, SampleRate: 500, Channels: 1);
        var nextB = new NextTrack(Add(b), "b");
        var nextC = new NextTrack(Add(c), "c");
        using var decoders = new GatedDecoderFactory(_decoders, nextC.Path);
        var player = new Player(decoders, _outputs, new PlayerOptions());
        _owned.Add(player);
        using var intoB = new ManualResetEventSlim();
        player.TrackAdvanced += (_, _) => intoB.Set();
        player.Load(Add(a));
        player.SetNextTracks([nextB, nextC]);
        player.Play();

        Assert.Equal(a.Pcm(), Pull(2000));
        var bPcm = StepConverter.Expected(b, a.Format);
        Assert.Equal(bPcm[..400], Pull(400));
        Assert.True(intoB.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "into b");
        Assert.True(decoders.Opening.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "c being opened, from the order [b, c]");
        player.SetNextTracks([nextC]); // what the queue sends now: relative to b
        decoders.Proceed.Set();

        Assert.Equal(bPcm[400..], Pull(bPcm.Length - 400));
        Assert.Equal(StepConverter.Expected(c, a.Format)[..400], Pull(400));
        Assert.Equal(1, decoders.Opens);
        Assert.Equal(2, Output.Converters.Count); // b's and c's
        Assert.Equal(1, Output.OpenCount);
    }

    /// <summary>
    /// The other side of the one above: when the order sent meanwhile doesn't have the track being opened next, that track
    /// is dropped (its decoder closed) and the one the new order names follows instead, gaplessly.
    /// </summary>
    [Fact]
    public void A_next_track_being_opened_when_an_order_without_it_next_is_sent_is_dropped_and_the_new_next_follows()
    {
        SharedMode();
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 2000, SampleRate: 2000);
        var c = new RampSpec(3, 1000, SampleRate: 500, Channels: 1);
        var d = new RampSpec(4, 1000); // a's format: no converter
        var nextB = new NextTrack(Add(b), "b");
        var nextC = new NextTrack(Add(c), "c");
        var nextD = new NextTrack(Add(d), "d");
        using var decoders = new GatedDecoderFactory(_decoders, nextC.Path);
        var player = new Player(decoders, _outputs, new PlayerOptions());
        _owned.Add(player);
        using var intoB = new ManualResetEventSlim();
        player.TrackAdvanced += (_, _) => intoB.Set();
        player.Load(Add(a));
        player.SetNextTracks([nextB, nextC]);
        player.Play();

        Assert.Equal(a.Pcm(), Pull(2000));
        var bPcm = StepConverter.Expected(b, a.Format);
        Assert.Equal(bPcm[..400], Pull(400));
        Assert.True(intoB.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "into b");
        Assert.True(decoders.Opening.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "c being opened, from the order [b, c]");
        player.SetNextTracks([nextD]); // c taken out meanwhile
        decoders.Proceed.Set();

        Assert.Equal(bPcm[400..], Pull(bPcm.Length - 400));
        Assert.Equal(d.Pcm()[..400], Pull(400)); // d, not c
        Assert.Equal(1, decoders.Opens);
        Assert.True(Assert.Single(_decoders.Opened, o => o.Spec.Id == c.Id).Disposed); // c's decoder closed, not leaked
        Assert.Equal(1, Output.OpenCount);
    }

    /// <summary>
    /// The window RechainAfterOpen alone leaves (#171 review): a chain step that started while the output existed but was
    /// closed (here: after a stop) asks it for a converter while Play is opening it. An output asked for exclusive mode says
    /// no until its open falls back to shared, and if the step then ends the stream after RechainAfterOpen ran, nothing
    /// tries again. The step sees the open through the opens counter and tries again instead.
    /// </summary>
    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_next_track_refused_a_converter_while_play_opened_the_device_is_tried_again_not_ended_after()
    {
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 2000, SampleRate: 2000);
        var nextB = new NextTrack(Add(b), "b");
        var asked = 0;
        // No converter the first time (exclusive asked, not open yet), one from then on (fell back to shared).
        Output.Converter = (from, to) => Interlocked.Increment(ref asked) == 1 ? null : new StepConverter(from, to);
        using var decoders = new GatedDecoderFactory(_decoders, nextB.Path);
        var player = new Player(decoders, _outputs, new PlayerOptions());
        _owned.Add(player);
        player.Load(Add(a));
        player.Play();
        player.Stop(); // the output object stays, closed

        player.Load(Add(a));
        player.SetNextTracks([nextB]);
        Assert.True(decoders.Opening.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "b being opened, the output closed");
        player.Play(); // opens the device while the step is still opening b
        decoders.Proceed.Set(); // the step now asks for a converter and is refused once

        Assert.Equal(a.Pcm(), Pull(a.Pcm().Length));
        var bPcm = StepConverter.Expected(b, a.Format);
        Assert.Equal(bPcm, Pull(bPcm.Length)); // b follows gaplessly, converted
        Assert.Equal(2, Volatile.Read(ref asked));
        Assert.Equal(PlayerState.Playing, player.State);
    }

    /// <summary>
    /// Before the first Play there is no output to convert with: a decode thread that reached the end of a short first
    /// track then could not chain a next track of another rate, and ended the stream after it. Play must try again once
    /// the device is open, or playback stops after the first track (what made the two tests above flaky under load).
    /// </summary>
    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_next_track_of_another_rate_given_up_on_before_the_first_play_is_chained_once_the_device_is_open()
    {
        SharedMode();
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 2000, SampleRate: 2000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.SetNextTracks([new NextTrack(Add(b), "b")]);
        Assert.True(player.Buffer!.WaitUntilReadable(a.Pcm().Length + 1, TimeSpan.FromSeconds(30)), "the end after a, written before any output");

        player.Play();

        Assert.Equal(a.Pcm(), Pull(a.Pcm().Length));
        var bPcm = StepConverter.Expected(b, a.Format);
        Assert.Equal(bPcm, Pull(bPcm.Length)); // b follows gaplessly, converted, on the same device
        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(PlayerState.Playing, player.State);
    }

    /// <summary>
    /// Once the last track is current, the queue sends the order again relative to it: nothing follows. The end already
    /// written after that track must stay: taking it away (to "try more entries", of which there are none) and writing
    /// it again left a moment in which a device reading at the end found neither audio nor the end, and played a block
    /// of silence as an underrun (seen as 32 frames too many in QueueLevellingTests on a macOS runner).
    /// </summary>
    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_order_sent_again_with_nothing_after_the_last_track_keeps_its_end_so_the_device_reads_no_gap()
    {
        var a = new RampSpec(1, 400);
        var b = new RampSpec(2, 400);
        var player = CreatePlayer();
        using var intoB = new ManualResetEventSlim();
        player.TrackAdvanced += (_, _) => intoB.Set();
        player.Load(Add(a));
        player.SetNextTracks([new NextTrack(Add(b), "b")]);
        player.Play();

        Assert.Equal([.. a.Pcm(), .. b.Pcm()], Pull(a.Pcm().Length + b.Pcm().Length));
        Assert.True(intoB.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "into b");
        var buffer = player.Buffer!;
        Assert.True(buffer.WaitUntilReadable(64, TimeSpan.FromSeconds(30)), "the end written after b");
        player.SetNextTracks([]); // what the queue sends once b is current: nothing after it

        Assert.Empty(Output.Pump(64, wait: false)); // the end, at once: not a block of silence
        Assert.Equal(0, buffer.Underruns);
    }

    [Fact]
    public void An_order_sent_again_with_a_new_track_after_the_last_one_takes_the_end_away_and_plays_it_gaplessly()
    {
        var a = new RampSpec(1, 400);
        var b = new RampSpec(2, 400);
        var player = CreatePlayer();
        using var intoB = new ManualResetEventSlim();
        player.TrackAdvanced += (_, _) => intoB.Set();
        player.Load(Add(a));
        player.Play();

        Assert.Equal(a.Pcm()[..400], Pull(400));
        Assert.True(player.Buffer!.WaitUntilReadable(a.Pcm().Length, TimeSpan.FromSeconds(30)), "a decoded");
        player.SetNextTracks([new NextTrack(Add(b), "b")]); // added to the queue once a's end was written

        Assert.Equal([.. a.Pcm()[400..], .. b.Pcm()], Pull(a.Pcm().Length - 400 + b.Pcm().Length));
        Assert.True(intoB.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "into b");
    }

    /// <summary>Holds the first open of <paramref name="gated"/> until <see cref="Proceed"/> (<see cref="Opening"/> says it started).</summary>
    private sealed class GatedDecoderFactory(RampDecoderFactory inner, string gated) : IAudioDecoderFactory, IDisposable
    {
        private int _opens;

        public ManualResetEventSlim Opening { get; } = new();

        public ManualResetEventSlim Proceed { get; } = new();

        /// <summary>How many times <c>gated</c> was opened.</summary>
        public int Opens => Volatile.Read(ref _opens);

        public string Name => inner.Name;

        public bool IsAvailable => inner.IsAvailable;

        public MusicSpin.Core.Metadata.TrackInfo Probe(string path) => inner.Probe(path);

        public IAudioDecoder Open(string path)
        {
            if (path == gated && Interlocked.Increment(ref _opens) == 1)
            {
                Opening.Set();
                Proceed.Wait(TimeSpan.FromSeconds(30));
            }

            return inner.Open(path);
        }

        public IReadOnlyList<AudioFormatSupport> GetSupportedFormats() => inner.GetSupportedFormats();

        /// <summary>Lets a held open go (a failed test must not leave the decode thread waiting). The events are left
        /// undisposed: the decode thread may still be returning from the wait.</summary>
        public void Dispose() => Proceed.Set();
    }

    [Fact]
    public void Shared_mode_started_with_the_device_closed_converts_each_track_once_to_the_mixers_rate()
    {
        SharedMode();
        var mixer = new PcmFormat(2000, 2, 16);
        Output.StreamFormat = mixer; // known only once the device is open
        var a = new RampSpec(1, 1000); // 1 kHz: not the mixer's rate
        var b = new RampSpec(2, 2000, SampleRate: 2000); // the mixer's rate
        var (player, _) = CreateQueue(a, b);

        Assert.Equal(StepConverter.Expected(a, mixer), Pull(4000)); // converted by the player, from its first sample
        Assert.Equal(b.Pcm()[..1000], Pull(1000)); // gapless, bit for bit: not converted to a's rate and back

        Assert.Equal(1, Output.OpenCount);
        Assert.Equal(1, Output.RetargetCount); // the unstarted stream moved to the converted buffer
        Assert.Equal(2000, Output.Source!.Format.SampleRate);
        var converter = Assert.IsType<StepConverter>(Assert.Single(Output.Converters)); // a's only
        Assert.Equal(a.Format, converter.From);
        Assert.Equal(2000, player.Buffer!.Format.SampleRate);
    }

    [Fact]
    public void A_device_that_will_not_move_to_the_converted_track_still_plays_it_converted_on_its_side()
    {
        SharedMode();
        Output.StreamFormat = new PcmFormat(2000, 2, 16);
        Output.RetargetError = new InvalidOperationException("device gone while moving");
        var errors = new List<Exception>();
        var a = new RampSpec(1, 1000);
        var player = CreatePlayer();
        player.Error += (_, e) => errors.Add(e.Exception);
        player.Load(Add(a));

        player.Play();

        Assert.Equal(a.Pcm()[..400], Pull(400)); // the original buffer, its decoder untouched by the unused wrapper
        Assert.Equal(a.Format, Output.Source!.Format);
        Assert.Contains(errors, e => e.Message.Contains("device gone"));
        Assert.Equal(PlayerState.Playing, player.State);
    }

    /// <summary>Pulls <paramref name="samples"/> a buffer's worth at a time (more than the buffer holds is decoded on the way).</summary>
    private float[] PullAll(int samples)
    {
        var pulled = new List<float>(samples);
        while (pulled.Count < samples)
        {
            var chunk = Pull(Math.Min(1000, samples - pulled.Count));
            Assert.NotEmpty(chunk);
            pulled.AddRange(chunk);
        }

        return [.. pulled];
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_converted_track_that_cannot_seek_back_to_its_start_plays_from_it_through_the_output_converting_it()
    {
        SharedMode();
        Output.StreamFormat = new PcmFormat(2000, 2, 16);
        var errors = new List<Exception>();
        var a = new RampSpec(1, 8000) { SeekFails = p => p == TimeSpan.Zero }; // 8 s: more than the 2 s decoded ahead
        var player = CreatePlayer();
        player.Error += (_, e) => errors.Add(e.Exception);
        player.Load(Add(a));

        player.Play();

        Assert.Equal(a.Pcm(), PullAll(16000)); // every sample once: back where the buffer's decoding stopped, none skipped
        Assert.Equal(a.Format, Output.Source!.Format); // the output went back to the buffer it opened with
        Assert.Equal(2, Output.RetargetCount); // to the converted buffer, then back
        Assert.Equal(1000, player.Buffer!.Format.SampleRate);
        Assert.Empty(errors); // nothing the listener hears went wrong
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_converted_track_whose_decoder_cannot_seek_at_all_plays_through_the_output_converting_it()
    {
        SharedMode();
        Output.StreamFormat = new PcmFormat(2000, 2, 16);
        var errors = new List<Exception>();
        var a = new RampSpec(1, 8000) { SeekFails = _ => true }; // each failed seek leaves it where it was
        var player = CreatePlayer();
        player.Error += (_, e) => errors.Add(e.Exception);
        player.Load(Add(a));

        player.Play();

        Assert.Equal(a.Pcm(), PullAll(16000)); // no seek back needed: still where the buffer's decoding stopped
        Assert.Equal(a.Format, Output.Source!.Format);
        Assert.Empty(errors);
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_converted_track_whose_failed_seek_moved_its_decoder_and_cannot_seek_back_is_reported_and_plays_on()
    {
        SharedMode();
        Output.StreamFormat = new PcmFormat(2000, 2, 16);
        var errors = new List<Exception>();
        var a = new RampSpec(1, 8000) { SeekFails = _ => true, FailedSeekLeavesAt = TimeSpan.FromSeconds(5) };
        var player = CreatePlayer();
        player.Error += (_, e) => errors.Add(e.Exception);
        player.Load(Add(a));

        player.Play();

        Assert.Equal(a.Pcm()[..400], Pull(400)); // the buffer it had, as decoded ahead
        Assert.Equal(a.Format, Output.Source!.Format);
        var error = Assert.IsType<AggregateException>(Assert.Single(errors)); // the seek, and the seek back
        Assert.All(error.InnerExceptions, e => Assert.Contains("Cannot seek", e.Message, StringComparison.Ordinal));
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_converted_track_that_cannot_seek_on_a_device_that_will_not_move_back_is_reported_and_plays_on_converted()
    {
        SharedMode();
        Output.StreamFormat = new PcmFormat(2000, 2, 16);
        var errors = new List<Exception>();
        var a = new RampSpec(1, 8000) { SeekFails = p => p == TimeSpan.Zero };
        Output.OnRetarget = next =>
        {
            if (next.Format == a.Format)
            {
                Output.RetargetError = new InvalidOperationException("device gone while moving back");
            }
        };
        var player = CreatePlayer();
        player.Error += (_, e) => errors.Add(e.Exception);
        player.Load(Add(a));

        player.Play();

        var pulled = Pull(400);
        Assert.Equal(2000, Output.Source!.Format.SampleRate); // still on the converted buffer
        var from = (int)((pulled[0] - a.Sample(0, 0)) / a.Channels); // from where the decoder is: past what was decoded ahead
        Assert.True(from > 0, $"plays on from frame {from}");
        Assert.Equal(StepConverter.Expected(a, Output.Source.Format).AsSpan(from * 2 * a.Channels, 400).ToArray(), pulled);
        var error = Assert.IsType<AggregateException>(Assert.Single(errors));
        Assert.Contains(error.InnerExceptions, e => e.Message.Contains("Cannot seek", StringComparison.Ordinal));
        Assert.Contains(error.InnerExceptions, e => e.Message.Contains("device gone", StringComparison.Ordinal));
        Assert.Equal(PlayerState.Playing, player.State);
    }

    [Theory]
    [InlineData(false, true)] // the stream cannot seek back to what is heard
    [InlineData(true, false)] // no converter can be made
    public void Without_a_way_to_convert_from_where_it_is_the_track_is_left_to_the_output(bool seekable, bool converter)
    {
        Output.Converter = converter ? (from, to) => new StepConverter(from, to) : (_, _) => null;
        Output.StreamFormat = new PcmFormat(2000, 2, 16);
        var a = new RampSpec(1, 1000) { Seekable = seekable };
        var player = CreatePlayer();
        player.Load(Add(a));

        player.Play();

        Assert.Equal(a.Pcm()[..400], Pull(400));
        Assert.Equal(0, Output.RetargetCount);
        Assert.Equal(a.Format, Output.Source!.Format);
    }

    [Fact]
    public async Task Without_a_converter_a_rate_change_ends_the_gapless_run_and_reopens()
    {
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 2000, SampleRate: 2000);
        var (player, queue) = CreateQueue(a, b);
        var replaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.StateChanged += (_, e) =>
        {
            if (e.OldState != PlayerState.Playing && e.NewState == PlayerState.Playing && Output.OpenCount == 2)
            {
                replaying.TrySetResult();
            }
        };

        var first = Output.Source!;
        Assert.Equal(a.Pcm(), Pull(2000));
        // Waits (failure-capped) for the decode thread to end a's stream rather than chain b, and reads that end.
        Assert.Empty(Pull(64));

        await replaying.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // failure cap
        // a's stream ended at a's end; b got a stream of its own at its own rate (not chained, nothing converted).
        Assert.NotSame(first, Output.Source);
        Assert.Equal(b.Format, Output.Source!.Format);
        Assert.Empty(Output.Converters);
        Assert.Equal(b.Pcm()[..200], Pull(200));
        Assert.Equal(1, queue.Status().QueueIndex);
    }

    [Fact]
    public void Exclusive_mode_never_converts()
    {
        Output.Exclusive = true; // the manual output's converter stays null, like the real exclusive output
        var a = new RampSpec(1, 4000);
        var b = new RampSpec(2, 4000, SampleRate: 2000);
        var player = CreatePlayer();
        player.Load(Add(a));
        player.Play();

        player.Load(Add(b));
        player.Play();

        Assert.Equal(2, Output.OpenCount);
        Assert.Equal(b.Format, player.Buffer!.Format);
        Assert.Equal(b.Pcm()[..100], Pull(100));
    }
}
