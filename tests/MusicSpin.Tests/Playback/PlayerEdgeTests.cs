using Microsoft.Extensions.Time.Testing;
using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>Edge cases of the single-track <see cref="Player"/> state machine (no timing: a manually pumped output).</summary>
public sealed class PlayerEdgeTests : IDisposable
{
    private static readonly string Song = Path.GetFullPath("/music/edge.flac");

    private readonly FakeDecoderFactory _decoders = new FakeDecoderFactory().With(TestAudio.Track(Song, sampleRate: 1000, seconds: 1));
    private readonly ManualOutputProvider _outputs = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Player _player;
    private readonly List<PlayerState> _states = [];

    public PlayerEdgeTests()
    {
        _player = new Player(_decoders, _outputs, new PlayerOptions { PositionInterval = TimeSpan.FromMilliseconds(100) }, _time);
        _player.StateChanged += (_, e) => _states.Add(e.NewState);
    }

    public void Dispose() => _player.Dispose();

    private static MusicSpinErrorCode Code(Action action) => Assert.Throws<MusicSpinException>(action).Code;

    [Fact]
    public void Play_while_playing_is_a_no_op_and_opens_the_device_once()
    {
        _player.Load(Song);
        _player.Play();
        _states.Clear();
        _player.Play();
        Assert.Empty(_states);
        Assert.Equal(1, _outputs.Output.OpenCount);
        Assert.Equal(PlayerState.Playing, _player.State);
    }

    [Fact]
    public void Pause_when_stopped_or_already_paused_changes_nothing()
    {
        _player.Load(Song);
        _states.Clear();
        _player.Pause();
        Assert.Equal(PlayerState.Stopped, _player.State);
        _player.Play();
        _player.Pause();
        _player.Pause();
        Assert.Equal([PlayerState.Playing, PlayerState.Paused], _states);
    }

    [Fact]
    public void Seek_and_cue_without_a_track_are_invalid_state_and_stop_is_harmless()
    {
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => _player.Seek(TimeSpan.FromSeconds(1))));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => _player.Cue(TimeSpan.FromSeconds(1))));
        _player.Stop();
        Assert.Equal(PlayerState.Stopped, _player.State);
        Assert.Null(_player.CurrentTrack);
    }

    [Fact]
    public void Negative_seek_clamps_to_the_start()
    {
        _player.Load(Song);
        _player.Seek(TimeSpan.FromMilliseconds(500));
        _player.Seek(TimeSpan.FromSeconds(-3));
        Assert.Equal(TimeSpan.Zero, _player.Position);
    }

    [Fact]
    public void Seek_while_paused_moves_the_position_and_stays_paused()
    {
        _player.Load(Song);
        _player.Play();
        _player.Pause();
        _player.Seek(TimeSpan.FromMilliseconds(900));
        Assert.Equal(PlayerState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromMilliseconds(900), _player.Position);
        Assert.Empty(_outputs.Output.Pump(10)); // paused: the device pulls nothing
    }

    [Fact]
    public void Cue_while_playing_pauses_the_device_at_the_new_position()
    {
        _player.Load(Song);
        _player.Play();
        _player.Cue(TimeSpan.FromMilliseconds(250));
        Assert.Equal(PlayerState.Paused, _player.State);
        Assert.False(_outputs.Output.Started);
        Assert.Equal(TimeSpan.FromMilliseconds(250), _player.Position);
        _player.Play();
        Assert.Equal(200, _outputs.Output.Pump(200).Length); // resumes from the cue point
        Assert.Equal(TimeSpan.FromMilliseconds(350), _player.Position);
    }

    [Fact]
    public void Cue_on_a_stopped_track_reports_paused_without_opening_the_device()
    {
        _player.Load(Song);
        _player.Cue(TimeSpan.FromMilliseconds(400));
        Assert.Equal(PlayerState.Paused, _player.State);
        Assert.Equal(0, _outputs.Output.OpenCount);
        Assert.Equal(TimeSpan.FromMilliseconds(400), _player.Position);
    }

    [Fact]
    public async Task Seek_after_the_end_and_play_continues_from_there_not_from_the_start()
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _player.PlaybackEnded += (_, _) => ended.TrySetResult();
        _player.Load(Song);
        _player.Play();
        _outputs.Output.Pump(2000); // the whole second (1000 stereo frames)
        _outputs.Output.Pump(10);   // end of stream
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerState.Stopped, _player.State);

        _player.Seek(TimeSpan.FromMilliseconds(600));
        _player.Play();
        Assert.Equal(TimeSpan.FromMilliseconds(600), _player.Position); // not rewound to 0 by the "ended" restart
    }

    [Fact]
    public void A_failing_device_stop_is_reported_as_an_error_event_not_thrown()
    {
        var faulty = new FaultyOutputProvider(_outputs);
        using var player = new Player(_decoders, faulty, new PlayerOptions(), _time);
        var errors = new List<Exception>();
        player.Error += (_, e) => errors.Add(e.Exception);
        player.Load(Song);
        player.Play();
        faulty.FailStop = true;
        player.Stop(); // must not throw
        Assert.Equal(PlayerState.Stopped, player.State);
        Assert.Contains(errors, e => e.Message.Contains("failed to stop", StringComparison.Ordinal));
    }

    [Fact]
    public void Commands_after_dispose_are_rejected_or_ignored()
    {
        _player.Load(Song);
        _player.Dispose();
        _player.Dispose(); // idempotent
        Assert.Throws<ObjectDisposedException>(_player.Play);
        Assert.Throws<ObjectDisposedException>(() => _player.Load(Song));
    }

    [Fact]
    public void Volume_rejects_non_finite_values_and_clamps()
    {
        _player.Volume = float.NaN;
        Assert.Equal(1f, _player.Volume);
        _player.Volume = 7f;
        Assert.Equal(1f, _player.Volume);
        _player.Volume = -1f;
        Assert.Equal(0f, _player.Volume);
    }
}

public sealed class PlaybackSourceTests
{
    [Fact]
    public void Reads_after_dispose_return_nothing_and_the_position_is_zero()
    {
        var source = new PlaybackSource(new FakeDecoder(TestAudio.Track(seconds: 1, sampleRate: 1000)), () => 1f);
        Assert.Equal(20, source.Read(new float[20]));
        source.Dispose();
        Assert.Equal(0, source.Read(new float[20]));
        Assert.Equal(TimeSpan.Zero, source.Position);
    }

    [Fact]
    public void Seek_on_a_non_seekable_decoder_is_ignored()
    {
        var source = new PlaybackSource(new FakeDecoder(TestAudio.Track(seconds: 1, sampleRate: 1000)) { CanSeek = false }, () => 1f);
        source.Read(new float[200]);
        source.Seek(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(100), source.Position);
    }

    [Fact]
    public void End_is_raised_once_until_more_audio_is_read_again()
    {
        var source = new PlaybackSource(new FakeDecoder(TestAudio.Track(seconds: 0.01, sampleRate: 1000)), () => 1f);
        var ends = 0;
        source.Ended += (_, _) => ends++;
        source.Read(new float[100]);
        source.Read(new float[100]);
        source.Read(new float[100]);
        Assert.Equal(1, ends);
        source.Seek(TimeSpan.Zero);
        source.Read(new float[100]);
        source.Read(new float[100]);
        Assert.Equal(2, ends);
    }
}

public sealed class PlaybackModesTests
{
    [Theory]
    [InlineData("off", RepeatMode.Off)]
    [InlineData(" NONE ", RepeatMode.Off)]
    [InlineData("no", RepeatMode.Off)]
    [InlineData("one", RepeatMode.One)]
    [InlineData("Track", RepeatMode.One)]
    [InlineData("single", RepeatMode.One)]
    [InlineData("all", RepeatMode.All)]
    [InlineData("QUEUE", RepeatMode.All)]
    public void Repeat_mode_aliases_parse(string text, RepeatMode expected)
    {
        Assert.True(PlaybackModes.TryParseRepeat(text, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("forever")]
    [InlineData("1")]
    public void Unknown_repeat_modes_are_rejected_as_off(string? text)
    {
        Assert.False(PlaybackModes.TryParseRepeat(text, out var mode));
        Assert.Equal(RepeatMode.Off, mode);
    }

    [Fact]
    public void Repeat_mode_names_round_trip()
    {
        foreach (var mode in Enum.GetValues<RepeatMode>())
        {
            Assert.True(PlaybackModes.TryParseRepeat(PlaybackModes.Name(mode), out var parsed));
            Assert.Equal(mode, parsed);
        }

        Assert.Equal("off", PlaybackModes.Name((RepeatMode)42));
    }

    [Theory]
    [InlineData("on", false, true)]
    [InlineData("YES", false, true)]
    [InlineData("0", true, false)]
    [InlineData("toggle", true, false)]
    [InlineData("toggle", false, true)]
    public void Shuffle_values_parse_and_toggle_resolves_against_the_current_mode(string text, bool current, bool expected)
    {
        Assert.True(PlaybackModes.TryParseShuffle(text, current, out var enabled));
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("maybe")]
    public void Unknown_shuffle_values_are_rejected_and_keep_the_current_mode(string? text)
    {
        Assert.False(PlaybackModes.TryParseShuffle(text, current: true, out var enabled));
        Assert.True(enabled);
    }
}
