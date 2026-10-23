using Microsoft.Extensions.Time.Testing;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

public sealed class PlayerTests : IDisposable
{
    private const string Song = "/music/song.flac";

    private readonly FakeDecoderFactory _decoders = new FakeDecoderFactory().With(TestAudio.Track(Song, sampleRate: 1000, seconds: 1));
    private readonly ManualOutputProvider _outputs = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Player _player;

    public PlayerTests()
    {
        _player = new Player(_decoders, _outputs, new PlayerOptions { PositionInterval = TimeSpan.FromMilliseconds(100) }, _time);
    }

    public void Dispose() => _player.Dispose();

    [Fact]
    public void Load_sets_track_and_raises_TrackLoaded()
    {
        TrackLoadedEventArgs? loaded = null;
        _player.TrackLoaded += (_, e) => loaded = e;

        var track = _player.Load(Song);

        Assert.Equal(Song, track.Path);
        Assert.Same(track, loaded?.Track);
        Assert.Equal(PlayerState.Stopped, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), _player.Duration);
    }

    [Fact]
    public void Play_without_track_is_invalid_state()
    {
        var ex = Assert.Throws<MusicSpinException>(_player.Play);
        Assert.Equal(MusicSpinErrorCode.InvalidState, ex.Code);
    }

    [Fact]
    public void Load_of_missing_file_keeps_current_track()
    {
        _player.Load(Song);
        var ex = Assert.Throws<MusicSpinException>(() => _player.Load("/missing.flac"));

        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
        Assert.Equal(Song, _player.CurrentTrack?.Path);
    }

    [Fact]
    public void Play_pause_stop_transitions_raise_events()
    {
        var states = new List<PlayerState>();
        _player.StateChanged += (_, e) => states.Add(e.NewState);
        _player.Load(Song);

        _player.Play();
        Assert.True(_outputs.Output.Started);
        _player.Pause();
        Assert.False(_outputs.Output.Started);
        _player.Play();
        _player.Stop();

        Assert.Equal([PlayerState.Playing, PlayerState.Paused, PlayerState.Playing, PlayerState.Stopped], states);
        Assert.False(_outputs.Output.IsOpen);
        Assert.Equal(TimeSpan.Zero, _player.Position);
    }

    [Fact]
    public void A_device_lost_mid_play_reports_an_error_pauses_at_the_position_and_play_reopens_it()
    {
        var errors = new List<Exception>();
        _player.Error += (_, e) => errors.Add(e.Exception);
        _player.Load(Song);
        _player.Play();
        _outputs.Output.Pump(500); // 250 ms in

        _outputs.Output.Fail("The audio device stopped unexpectedly (disconnected or taken away).");

        var error = Assert.IsType<MusicSpinException>(Assert.Single(errors));
        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, error.Code);
        Assert.Equal("The audio device stopped unexpectedly (disconnected or taken away).", error.Message);
        Assert.Equal(PlayerState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromMilliseconds(250), _player.Position);
        Assert.False(_outputs.Output.IsOpen);

        _player.Play();

        Assert.True(_outputs.Output.IsOpen);
        Assert.Equal(2, _outputs.Output.OpenCount);
        Assert.Equal(PlayerState.Playing, _player.State);
        Assert.All(_outputs.Output.Pump(10), s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void An_output_that_moved_device_keeps_playing_from_the_same_position_and_reports_where_it_went()
    {
        var errors = new List<Exception>();
        var opened = new List<OpenedOutput>();
        _player.Error += (_, e) => errors.Add(e.Exception);
        _player.OutputOpened += (_, e) => opened.Add(e.Opened);
        _player.Load(Song);
        _player.Play();
        _outputs.Output.Pump(500); // 250 ms in
        var states = new List<PlayerState>();
        _player.StateChanged += (_, e) => states.Add(e.NewState);

        var speakers = new OpenedOutput("Speakers", false, 48_000, 2, "f32", 32, true);
        _outputs.Output.Reroute(speakers, null); // the default changed: no word needed
        var shared = speakers with { Notice = "'BTR13' is not available: playing on the system default device." };
        _outputs.Output.Reroute(shared, shared.Notice);

        Assert.Equal([speakers, shared], opened.Skip(opened.Count - 2));
        var error = Assert.IsType<MusicSpinException>(Assert.Single(errors));
        Assert.Equal(shared.Notice, error.Message);
        Assert.Empty(states); // never paused
        Assert.Equal(PlayerState.Playing, _player.State);
        Assert.Equal(1, _outputs.Output.OpenCount);
        Assert.Equal(TimeSpan.FromMilliseconds(250), _player.Position);
        _outputs.Output.Pump(500);
        Assert.Equal(TimeSpan.FromMilliseconds(500), _player.Position);
    }

    [Fact]
    public void OutputOpened_subscribers_run_with_no_player_lock_held()
    {
        var reachedPlayerFromAnotherThread = false;
        _outputs.Output.OpenedAs = _ => new OpenedOutput("Speakers", false, 48_000, 2, "f32", 32, true);
        _player.OutputOpened += (_, _) =>
        {
            // A UI handler may hop to another thread that asks the player something while this one waits for it.
            reachedPlayerFromAnotherThread = Task.Run(() => _player.State, TestContext.Current.CancellationToken)
                .Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        };
        _player.Load(Song);

        _player.Play();

        Assert.True(reachedPlayerFromAnotherThread);
        Assert.Equal(PlayerState.Playing, _player.State);
    }

    [Fact]
    public void A_reroute_after_dispose_is_ignored()
    {
        var events = 0;
        _player.Error += (_, _) => events++;
        _player.OutputOpened += (_, _) => events++;
        _player.Load(Song);
        _player.Play();
        _player.Dispose();

        _outputs.Output.Reroute(new OpenedOutput("Speakers", false, 48_000, 2, "f32", 32, true), "moved");

        Assert.Equal(0, events);
    }

    [Fact]
    public void A_device_failure_after_dispose_is_ignored()
    {
        var errors = 0;
        _player.Error += (_, _) => errors++;
        _player.Load(Song);
        _player.Play();
        _player.Dispose();

        _outputs.Output.Fail("late");

        Assert.Equal(0, errors);
    }

    [Fact]
    public void Output_pulls_decoded_samples_and_position_advances()
    {
        _player.Load(Song);
        _player.Play();

        var samples = _outputs.Output.Pump(1000); // 500 frames of stereo @ 1 kHz

        Assert.Equal(1000, samples.Length);
        Assert.All(samples, s => Assert.Equal(0.5f, s));
        Assert.Equal(TimeSpan.FromMilliseconds(500), _player.Position);
    }

    [Fact]
    public void Volume_one_is_bit_exact_and_lower_volume_scales()
    {
        _player.Load(Song);
        _player.Play();
        Assert.Equal(0.5f, _outputs.Output.Pump(2)[0]);

        _player.Volume = 0.5f;
        Assert.Equal(0.25f, _outputs.Output.Pump(2)[0]);

        _player.Volume = 7f;
        Assert.Equal(1f, _player.Volume);
        _player.Volume = float.NaN;
        Assert.Equal(1f, _player.Volume);
    }

    [Fact]
    public void Seek_moves_position()
    {
        _player.Load(Song);
        _player.Seek(TimeSpan.FromMilliseconds(750));
        Assert.Equal(TimeSpan.FromMilliseconds(750), _player.Position);

        _player.Seek(TimeSpan.FromSeconds(-3));
        Assert.Equal(TimeSpan.Zero, _player.Position);
    }

    [Fact]
    public async Task End_of_track_stops_and_raises_PlaybackEnded_then_Play_restarts()
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _player.PlaybackEnded += (_, _) => ended.TrySetResult();
        _player.Load(Song);
        _player.Play();

        Assert.Equal(2000, _outputs.Output.Pump(4000).Length); // whole track
        Assert.Empty(_outputs.Output.Pump(100));               // end of stream

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerState.Stopped, _player.State);

        _player.Play();
        Assert.Equal(PlayerState.Playing, _player.State);
        Assert.Equal(TimeSpan.Zero, _player.Position);
        Assert.Equal(2, _outputs.Output.OpenCount);
    }

    [Fact]
    public void Position_events_fire_on_timer_while_playing()
    {
        var events = 0;
        _player.Load(Song);
        _player.PositionChanged += (_, _) => events++;

        _player.Play();
        _time.Advance(TimeSpan.FromMilliseconds(350));
        Assert.Equal(3, events);

        _player.Pause(); // pause raises one immediate update and stops the timer
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(4, events);
    }

    [Fact]
    public void Dispose_releases_decoder()
    {
        _player.Load(Song);
        _player.Dispose();
        Assert.True(Assert.Single(_decoders.Opened).Disposed);
    }
}
