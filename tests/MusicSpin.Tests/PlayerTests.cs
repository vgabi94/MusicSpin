using Microsoft.Extensions.Time.Testing;
using MusicSpin.Core;
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
