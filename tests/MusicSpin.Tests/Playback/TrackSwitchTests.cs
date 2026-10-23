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
}
